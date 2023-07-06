using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.FileExtractor.ExtractedFiles;
using Wabbajack.Hashing.PHash;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.VFS.Interfaces;

namespace Wabbajack.VFS;

public class Context
{
    public const ulong FileVersion = 0x03;
    public const string Magic = "WABBAJACK VFS FILE";
    private readonly TemporaryFileManager _manager;

    private readonly ParallelOptions _parallelOptions;

    public readonly FileExtractor.FileExtractor Extractor;
    public readonly FileHashCache HashCache;
    public readonly IResource<Context> Limiter;
    public readonly IResource<FileHashCache> HashLimiter;
    public readonly ILogger<Context> Logger;
    public readonly IVfsCache VfsCache;

    public Context(ILogger<Context> logger, ParallelOptions parallelOptions, TemporaryFileManager manager,
        IVfsCache vfsCache,
        FileHashCache hashCache, IResource<Context> limiter, IResource<FileHashCache> hashLimiter, 
        FileExtractor.FileExtractor extractor, IImageLoader imageLoader)
    {
        Limiter = limiter;
        HashLimiter = hashLimiter;
        Logger = logger;
        _manager = manager;
        Extractor = extractor;
        VfsCache = vfsCache;
        HashCache = hashCache;
        _parallelOptions = parallelOptions;
        ImageLoader = imageLoader;
    }

    public Context WithTemporaryFileManager(TemporaryFileManager manager)
    {
        return new Context(Logger, _parallelOptions, manager, VfsCache, HashCache, Limiter, HashLimiter,
            Extractor.WithTemporaryFileManager(manager), ImageLoader);
    }


    public IndexRoot Index { get; private set; } = IndexRoot.Empty;
    public IImageLoader ImageLoader { get; }

    public async Task<IndexRoot> AddRoot(AbsolutePath root, CancellationToken token)
    {
        return await AddRoots(new[] {root}, token);
    }

    public async Task<IndexRoot> AddRoots(IEnumerable<AbsolutePath> roots, CancellationToken token, Func<long, long, Task>? updateFunction = null)
    {
        var native = Index.AllFiles.Where(file => file.IsNative).ToDictionary(file => file.FullPath.Base);

        var filtered = Index.AllFiles.Where(file => ((AbsolutePath) file.Name).FileExists()).ToList();

        var filesToIndex = roots.SelectMany(root => root.EnumerateFiles()).ToList();

        var idx = 0;
        var allFiles = await filesToIndex
            .PMapAll(async f =>
            {
                using var job = await Limiter.Begin($"Indexing {f.FileName}", 0, token);
                if (native.TryGetValue(f, out var found))
                    if (found.LastModified == f.LastModifiedUtc().AsUnixTime() && found.Size == f.Size())
                        return found;
                Interlocked.Increment(ref idx);
                updateFunction?.Invoke(idx, filesToIndex.Count);
                
                return await VirtualFile.Analyze(this, null, new NativeFileStreamFactory(f), f, token);
            }).ToList();

        var newIndex = await IndexRoot.Empty.Integrate(filtered.Concat(allFiles).ToList());

        lock (this)
        {
            Index = newIndex;
        }

        await VfsCache.Clean();

        return newIndex;
    }

    /// <summary>
    ///     Extracts a file
    /// </summary>
    /// <param name="queue">Work queue to use when required by some formats</param>
    /// <param name="files">Predefined list of files to extract, all others will be skipped</param>
    /// <param name="callback">Func called for each file extracted</param>
    /// <param name="tempFolder">Optional: folder to use for temporary storage</param>
    /// <param name="runInParallel">Optional: run `callback`s in parallel</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public async Task Extract(HashSet<VirtualFile> files, Func<VirtualFile, IExtractedFile, ValueTask> callback,
        CancellationToken token, AbsolutePath? tempFolder = null, bool runInParallel = true)
    {
        var top = new VirtualFile();
        var filesByParent = files.SelectMany(f => f.FilesInFullPath)
            .Distinct()
            .GroupBy(f => f.Parent ?? top)
            .ToDictionary(f => f.Key);

        async Task HandleFile(VirtualFile file, IExtractedFile sfn)
        {
            if (token.IsCancellationRequested) return;

            if (filesByParent.ContainsKey(file))
                sfn.CanMove = false;

            if (files.Contains(file)) await callback(file, sfn);
            if (filesByParent.TryGetValue(file, out var children))
            {
                var fileNames = children.ToDictionary(c => c.RelativeName);
                try
                {
                    await Extractor.GatheringExtract(sfn,
                        r => fileNames.ContainsKey(r),
                        async (rel, csf) =>
                        {
                            await HandleFile(fileNames[rel], csf);
                            return 0;
                        },
                        token,
                        fileNames.Keys.ToHashSet());
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) return;
                    await using var stream = await sfn.GetStream();
                    var hash = await stream.HashingCopy(Stream.Null, token);
                    if (hash != file.Hash)
                        throw new Exception(
                            $"File {file.FullPath} is corrupt, please delete it and retry the installation, {ex.Message}", ex);
                    throw;
                }
            }
        }

        if (runInParallel)
        {
            await filesByParent[top].PDoAll(
                async file => await HandleFile(file, new ExtractedNativeFile(file.AbsoluteName) {CanMove = false}));
        }
        else
        {
            foreach (var file in filesByParent[top])
            {
                await HandleFile(file, new ExtractedNativeFile(file.AbsoluteName) {CanMove = false});
            }
        }
    }

    #region KnownFiles

    private List<HashRelativePath> _knownFiles = new();
    private readonly Dictionary<Hash, AbsolutePath> _knownArchives = new();


    public void AddKnown(IEnumerable<HashRelativePath> known, Dictionary<Hash, AbsolutePath> archives)
    {
        _knownFiles.AddRange(known);
        foreach (var (key, value) in archives)
            _knownArchives.TryAdd(key, value);
    }

    public async ValueTask BackfillMissing()
    {
        var newFiles = _knownArchives.ToDictionary(kv => kv.Key,
            kv => new VirtualFile
            {
                Name = kv.Value,
                Size = kv.Value.Size(),
                Hash = kv.Key
            });

        foreach (var f in newFiles.Values)
            f.FillFullPath(0);

        var parentchild = new Dictionary<(VirtualFile, RelativePath), VirtualFile>();

        void BackFillOne(HashRelativePath file)
        {
            var parent = newFiles[file.Hash];
            foreach (var path in file.Parts)
            {
                if (parentchild.TryGetValue((parent, path), out var foundParent))
                {
                    parent = foundParent;
                    continue;
                }

                var nf = new VirtualFile {Name = path, Parent = parent};
                nf.FillFullPath();
                parent.Children = parent.Children.Add(nf);
                parentchild.Add((parent, path), nf);
                parent = nf;
            }
        }

        _knownFiles.Where(f => f.Parts.Length > 0).Do(BackFillOne);

        var newIndex = await Index.Integrate(newFiles.Values.ToList());

        lock (this)
        {
            Index = newIndex;
        }

        _knownFiles = new List<HashRelativePath>();
    }

    #endregion
}