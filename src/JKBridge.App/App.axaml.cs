using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JKBridge.App.Platform;
using JKBridge.App.Services;
using JKBridge.App.ViewModels;
using JKBridge.App.Views;
using JKBridge.Core.Configuration;
using JKBridge.Core.Diagnostics;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace JKBridge.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private AppController? _controller;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _powerGate = new(1, 1);
    private readonly object _shutdownGate = new();
    private Task? _initializationTask;
    private Task? _lastPowerTask;
    private Task? _shutdownTask;
    private bool _powerEventsSubscribed;
    private int _exitStarted;

    public static SingleInstanceCoordinator? InstanceCoordinator { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var dataDirectory = GetDataDirectory();
            var logDirectory = Path.Combine(dataDirectory, "logs");
            var configStore = new ConfigStore(Path.Combine(dataDirectory, "config.json"));
            var logger = new RollingFileLogger(
                logDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var runtime = BridgeRuntime.CreateDefault(
                logger,
                BridgeRuntimeOptions.CreateDefault(dataDirectory));
            _controller = new AppController(
                configStore,
                logger,
                runtime,
                new DevicePairingService(),
                new StartupRegistration(),
                logDirectory,
                () => _mainWindow,
                applicationExitRequester: () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _ = ExitAsync();
                    });
                    return Task.CompletedTask;
                });

            _mainWindow = new MainWindow { DataContext = _controller.ViewModel };
            _mainWindow.Closing += MainWindow_OnClosing;
            _controller.ServiceAlert += Controller_OnServiceAlert;
            desktop.MainWindow = _mainWindow;
            desktop.Exit += Desktop_OnExit;

            var startHidden = desktop.Args?.Any(argument =>
                string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase)) == true;
            if (startHidden)
            {
                EventHandler? hideOnce = null;
                hideOnce = (_, _) =>
                {
                    _mainWindow.Opened -= hideOnce;
                    _mainWindow.Hide();
                };
                _mainWindow.Opened += hideOnce;
            }

            InstanceCoordinator?.StartListening(
                () => Dispatcher.UIThread.Post(ShowMainWindow),
                SynchronizationContext.Current);
            if (OperatingSystem.IsWindows())
            {
                SubscribePowerEvents();
            }
            _initializationTask = _controller.InitializeAsync(_lifetimeCancellation.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void MainWindow_OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (eventArgs.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            Interlocked.Exchange(ref _exitStarted, 1);
            return;
        }

        if (Volatile.Read(ref _exitStarted) != 0)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_controller?.CloseToTray != false)
        {
            _mainWindow?.Hide();
        }
        else
        {
            _ = ExitAsync();
        }
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs eventArgs) => ShowMainWindow();

    private void Controller_OnServiceAlert(object? sender, ServiceAlert alert)
    {
        if (!WindowsNotifier.IsSupported)
        {
            return;
        }

        try
        {
            WindowsNotifier.ShowBalloon(
                $"{alert.ServiceName}：{alert.Title}",
                alert.Detail);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Notification support is optional; the in-app banner still shows.
        }
    }

    private void ShowMenuItem_OnClick(object? sender, EventArgs eventArgs) => ShowMainWindow();

    private void ToggleMenuItem_OnClick(object? sender, EventArgs eventArgs) =>
        _controller?.ViewModel.ToggleBridgeCommand.Execute(null);

    private void OpenOverleafMenuItem_OnClick(object? sender, EventArgs eventArgs) =>
        _ = _controller?.OpenServiceAsync("overleaf", "http://127.0.0.1:8080/project");

    private void OpenGpuMenuItem_OnClick(object? sender, EventArgs eventArgs) =>
        _ = _controller?.OpenServiceAsync("gpu-dashboard", "http://127.0.0.1:8765/");

    private void OpenInvestmentNewsMenuItem_OnClick(object? sender, EventArgs eventArgs) =>
        _ = _controller?.OpenServiceAsync("investment-news", "http://127.0.0.1:8793/");

    private void DiagnosticsMenuItem_OnClick(object? sender, EventArgs eventArgs) =>
        ShowPage(AppPage.Diagnostics);

    private void SettingsMenuItem_OnClick(object? sender, EventArgs eventArgs) =>
        ShowPage(AppPage.Settings);

    private void ExitMenuItem_OnClick(object? sender, EventArgs eventArgs) => _ = ExitAsync();

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void ShowPage(AppPage page)
    {
        if (_controller is not null)
        {
            _controller.ViewModel.SelectedPage = page;
        }

        ShowMainWindow();
    }

    private async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
        {
            return;
        }

        await ShutdownResourcesAsync();
        _desktop?.Shutdown();
    }

    private void Desktop_OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs eventArgs)
    {
        Interlocked.Exchange(ref _exitStarted, 1);
        ShutdownResourcesAsync().GetAwaiter().GetResult();
    }

    [SupportedOSPlatform("windows")]
    private void SubscribePowerEvents()
    {
        if (!OperatingSystem.IsWindows() || _powerEventsSubscribed)
        {
            return;
        }

        SystemEvents.PowerModeChanged += SystemEvents_OnPowerModeChanged;
        _powerEventsSubscribed = true;
    }

    [SupportedOSPlatform("windows")]
    private void UnsubscribePowerEvents()
    {
        if (!_powerEventsSubscribed)
        {
            return;
        }

        SystemEvents.PowerModeChanged -= SystemEvents_OnPowerModeChanged;
        _powerEventsSubscribed = false;
    }

    [SupportedOSPlatform("windows")]
    private void SystemEvents_OnPowerModeChanged(object? sender, PowerModeChangedEventArgs eventArgs)
    {
        if (Volatile.Read(ref _exitStarted) != 0 ||
            eventArgs.Mode is not (PowerModes.Suspend or PowerModes.Resume))
        {
            return;
        }

        _lastPowerTask = HandlePowerModeAsync(eventArgs.Mode);
    }

    [SupportedOSPlatform("windows")]
    private async Task HandlePowerModeAsync(PowerModes mode)
    {
        await _powerGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            if (_controller is null)
            {
                return;
            }

            if (mode == PowerModes.Suspend)
            {
                await _controller.SuspendForSleepAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            }
            else
            {
                await _controller.ResumeFromSleepAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() => _controller?.ViewModel.ShowNotice(
                "睡眠恢复失败",
                exception.Message));
        }
        finally
        {
            _powerGate.Release();
        }
    }

    private Task ShutdownResourcesAsync()
    {
        lock (_shutdownGate)
        {
            return _shutdownTask ??= ShutdownResourcesCoreAsync();
        }
    }

    private async Task ShutdownResourcesCoreAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            UnsubscribePowerEvents();
        }
        _lifetimeCancellation.Cancel();

        if (_initializationTask is not null)
        {
            try
            {
                await _initializationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when shutdown interrupts initialization.
            }
        }

        if (_lastPowerTask is not null)
        {
            try
            {
                await _lastPowerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when shutdown interrupts a resume delay.
            }
        }

        if (_controller is not null)
        {
            await _controller.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string GetDataDirectory()
    {
        var explicitDirectory = Environment.GetEnvironmentVariable("JKBRIDGE_DATA_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return Path.GetFullPath(explicitDirectory);
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(root, "JKBridge");
    }
}
