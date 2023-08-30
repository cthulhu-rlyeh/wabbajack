﻿using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using DynamicData;
using DynamicData.Binding;
using MahApps.Metro.Controls;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Wabbajack.Common;
using Wabbajack.DTOs;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.DTOs.Interventions;
using Wabbajack.Messages;
using Wabbajack.Paths.IO;
using Wabbajack.UserIntervention;
using Wabbajack.Util;
using Wabbajack.Views;

namespace Wabbajack
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        private MainWindowVM _mwvm;
        private MainSettings _settings;
        private readonly ILogger<MainWindow> _logger;
        private readonly SystemParametersConstructor _systemParams;

        private ObservableCollection<ViewModel> TabVMs = new ObservableCollectionExtended<ViewModel>();

        public MainWindow(ILogger<MainWindow> logger, SystemParametersConstructor systemParams, LauncherUpdater updater, MainWindowVM vm)
        {
            InitializeComponent();
            _mwvm = vm;
            DataContext = vm;
            _logger = logger;
            _systemParams = systemParams;
            try
            {
                // Wire any unhandled crashing exceptions to log before exiting
                AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                {
                    // Don't do any special logging side effects
                    _logger.LogError((Exception)e.ExceptionObject, "Uncaught error");
                    Environment.Exit(-1);
                };

                Closed += (s, e) =>
                {
                    _mwvm.CancelRunningTasks(TimeSpan.FromSeconds(10));
                    Application.Current.Shutdown();
                };

                MessageBus.Current.Listen<TaskBarUpdate>()
                    .ObserveOnGuiThread()
                    .Subscribe(u =>
                    {
                        TaskbarItemInfoControl.Description = u.Description;
                        TaskbarItemInfoControl.ProgressValue = u.ProgressValue;
                        TaskbarItemInfoControl.ProgressState = u.State;
                    });


                _logger.LogInformation("Wabbajack Build - {Sha}",ThisAssembly.Git.Sha);
                _logger.LogInformation("Running in {EntryPoint}", KnownFolders.EntryPoint);

                var p = _systemParams.Create();

                _logger.LogInformation("Detected Windows Version: {Version}", Environment.OSVersion.VersionString);
                
                _logger.LogInformation(
                    "System settings - ({MemorySize} RAM) ({PageSize} Page), Display: {ScreenWidth} x {ScreenHeight} ({Vram} VRAM - VideoMemorySizeMb={ENBVRam})",
                    p.SystemMemorySize.ToFileSizeString(), p.SystemPageSize.ToFileSizeString(), p.ScreenWidth, p.ScreenHeight, p.VideoMemorySize.ToFileSizeString(), p.EnbLEVRAMSize);

                if (p.SystemPageSize == 0)
                    _logger.LogInformation( "Pagefile is disabled! Consider increasing to 20000MB. A disabled pagefile can cause crashes and poor in-game performance");
                else if (p.SystemPageSize < 2e+10)
                    _logger.LogInformation("Pagefile below recommended! Consider increasing to 20000MB. A suboptimal pagefile can cause crashes and poor in-game performance");

                //Warmup();
                
                var _ = updater.Run();

                var (settings, loadedSettings) = MainSettings.TryLoadTypicalSettings().AsTask().Result;
                // Load settings
                /*
                if (CLIArguments.NoSettings || !loadedSettings)
                {
                    _settings = new MainSettings {Version = Consts.SettingsVersion};
                    RunWhenLoaded(DefaultSettings);
                }
                else
                {
                    _settings = settings;
                    RunWhenLoaded(LoadSettings);
                }*/
                

                // Bring window to the front if it isn't already
                this.Initialized += (s, e) =>
                {
                    this.Activate();
                    this.Topmost = true;
                    this.Focus();
                };
                this.ContentRendered += (s, e) =>
                {
                    this.Topmost = false;
                };

                ((MainWindowVM) DataContext).WhenAnyValue(vm => vm.OpenSettingsCommand)
                    .BindTo(this, view => view.SettingsButton.Command);

                ((MainWindowVM)DataContext).WhenAnyValue(vm => vm.Installer.InstallState)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Select(v => v == InstallState.Installing ? Visibility.Collapsed : Visibility.Visible)
                    .BindTo(this, view => view.SettingsButton.Visibility);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "During Main Window Startup");
                Environment.Exit(-1);
            }

            vm.WhenAnyValue(vm => vm.ResourceStatus)
                .BindToStrict(this, view => view.ResourceUsage.Text);
            vm.WhenAnyValue(vm => vm.AppName)
                .BindToStrict(this, view => view.AppName.Text);
            
        }

        public void Init(MainWindowVM vm, MainSettings settings)
        {
            DataContext = vm;
            _mwvm = vm;
            _settings = settings;
        }

        private void RunWhenLoaded(Action a)
        {
            if (IsLoaded)
            {
                a();
            }
            else
            {
                this.Loaded += (sender, e) =>
                {
                    a();
                };
            }
        }

        private void LoadSettings()
        {
            Width = _settings.Width;
            Height = _settings.Height;
            Left = _settings.PosX;
            Top = _settings.PosY;
        }

        private void DefaultSettings()
        {
            Width = 1300;
            Height = 960;
            Left = 15;
            Top = 15;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _mwvm.ShutdownApplication().Wait();
        }

        private void UIElement_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            this.DragMove();
        }

    }
}
