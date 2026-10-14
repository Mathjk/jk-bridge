using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using JKBridge.App.Platform;
using JKBridge.App.ViewModels;
using JKBridge.Core.Authorization;
using JKBridge.Core.Configuration;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Health;
using JKBridge.Core.Models;
using JKBridge.Core.Pairing;
using JKBridge.Core.Routing;
using JKBridge.Core.Tunneling;
using JKBridge.Core.Updates;

namespace JKBridge.App.Services;

/// <summary>
/// Composes persistence, the connection runtime, platform actions, and the presentation model.
/// All process/network work remains outside the view model; UI updates are marshalled to Avalonia.
/// </summary>
public sealed partial class AppController : IBridgeUiActions, IAsyncDisposable
{
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Action<T> _report = report;

        public void Report(T value) => _report(value);
    }

    private readonly ConfigStore _configStore;
    private readonly RollingFileLogger _logger;
    private readonly IBridgeRuntime _runtime;
    private readonly IDevicePairingService _pairingService;
                    private readonly ServiceProfileTransferService _serviceProfileTransferService;
    private readonly DiagnosticBundleService _diagnosticBundleService;
    private readonly IReleaseUpdateChecker _releaseUpdateChecker;
    private readonly bool _ownsReleaseUpdateChecker;
    private readonly Action<string> _installerLauncher;
    private readonly Func<Task> _applicationExitRequester;
    private readonly string _updateDirectory;
    private readonly StartupRegistration _startupRegistration;
    private readonly HttpClient? _restartHttpClient;
    private readonly IRestartTokenStore _restartTokenStore;
    private readonly Func<Window?> _getWindow;
    private readonly Action<Action> _dispatchUi;
    private readonly string _logDirectory;
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private AppConfig _config = AppConfig.CreateDefault();
    private ServiceProfileImportPreview? _serviceProfileImportPreview;
                private ReleaseInstallerAsset? _pendingReleaseInstaller;
    private Uri? _pendingReleasePage;
    private ReleaseInstallerDownload? _downloadedReleaseInstaller;
        private int _resumeAfterSleep;
    private CancellationTokenSource? _pairingCancellation;
                private CancellationTokenSource? _serviceProfileCancellation;
        private CancellationTokenSource? _diagnosticBundleCancellation;
    private CancellationTokenSource? _releaseUpdateCancellation;
    private int _pairingActive;
            private int _setupOperationActive;
    private int _serviceProfileTransferActive;
    private int _diagnosticBundleActive;
    private int _releaseUpdateActive;
    private int _disposed;

    public AppController(
        ConfigStore configStore,
        RollingFileLogger logger,
        IBridgeRuntime runtime,
        IDevicePairingService pairingService,
        StartupRegistration startupRegistration,
        string logDirectory,
        Func<Window?> getWindow,
                                ServiceProfileTransferService? serviceProfileTransferService = null,
                Action<Action>? dispatchUi = null,
        DiagnosticBundleService? diagnosticBundleService = null,
        IReleaseUpdateChecker? releaseUpdateChecker = null,
        Action<string>? installerLauncher = null,
        IWindowsLocalServiceDiscovery? windowsLocalDiscovery = null,
        Func<Task>? applicationExitRequester = null,
        HttpClient? restartHttpClient = null,
        IRestartTokenStore? restartTokenStore = null)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _pairingService = pairingService ?? throw new ArgumentNullException(nameof(pairingService));
        _startupRegistration = startupRegistration ?? throw new ArgumentNullException(nameof(startupRegistration));
        _logDirectory = Path.GetFullPath(logDirectory);
        _getWindow = getWindow ?? throw new ArgumentNullException(nameof(getWindow));
        _dispatchUi = dispatchUi ?? DispatchOnAvaloniaUi;
        _serviceProfileTransferService = serviceProfileTransferService ?? new ServiceProfileTransferService(
            _configStore,
            new AvaloniaServiceProfilePathPicker(_getWindow));
        _diagnosticBundleService = diagnosticBundleService ?? new DiagnosticBundleService(
            new AvaloniaDiagnosticBundlePathPicker(_getWindow));
        _releaseUpdateChecker = releaseUpdateChecker ?? new GitHubReleaseUpdateService();
        _ownsReleaseUpdateChecker = releaseUpdateChecker is null;
        if (windowsLocalDiscovery is null)
        {
            _windowsLocalDiscovery = new WindowsLocalServiceDiscoveryService(
                new WindowsTcpListenerSource(),
                new LocalWebProbe());
            _ownsWindowsLocalDiscovery = true;
        }
        else
        {
            _windowsLocalDiscovery = windowsLocalDiscovery;
        }
        _installerLauncher = installerLauncher ?? LaunchInstaller;
        _applicationExitRequester = applicationExitRequester ?? (() => Task.CompletedTask);
        _restartHttpClient = restartHttpClient;
        _restartTokenStore = restartTokenStore ?? new WindowsRestartTokenStore();
        _updateDirectory = Path.Combine(
            Path.GetDirectoryName(_logDirectory) ?? _logDirectory,
            "Updates");
        ViewModel = new MainViewModel(this);
        var applicationVersion = GetApplicationVersion();
        ViewModel.ApplicationVersionText = $"JK Bridge {applicationVersion}";
        ViewModel.ApplicationFooterText = $"v{applicationVersion} · Windows 11";

        _runtime.SnapshotChanged += RuntimeOnSnapshotChanged;
        _logger.EventWritten += LoggerOnEventWritten;
    }

    public MainViewModel ViewModel { get; }

    public AppConfig CurrentConfig => _config;

    private async Task<AppConfig> MigrateRestartTokensAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var pending = config.Services
            .Where(service =>
                !string.IsNullOrWhiteSpace(service.RestartToken) &&
                string.IsNullOrWhiteSpace(service.RestartTokenReference))
            .ToArray();
        if (pending.Length == 0)
        {
            return config;
        }

        try
        {
            var migrated = new List<ServiceDefinition>();
            foreach (var service in pending)
            {
                var reference = await _restartTokenStore
                    .SaveAsync(service.Id, service.RestartToken.Trim(), cancellationToken)
                    .ConfigureAwait(false);
                lock (_restartTokenCache)
                {
                    _restartTokenCache[reference] = service.RestartToken.Trim();
                }

                migrated.Add(service with { RestartToken = string.Empty, RestartTokenReference = reference });
            }

            var next = config with
            {
                Services = config.Services
                    .Select(service => migrated.FirstOrDefault(item => item.Id == service.Id) ?? service with { })
                    .ToList(),
            };
            await _configStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            await SafeLogAsync(
                DiagnosticSeverity.Information,
                "Security",
                $"Moved {migrated.Count} restart token(s) into the Windows credential store.")
                .ConfigureAwait(false);
            return next;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or PlatformNotSupportedException or IOException or
                UnauthorizedAccessException or Win32Exception)
        {
            // Keep the plaintext token working when the credential store is unavailable.
            await SafeLogAsync(
                DiagnosticSeverity.Warning,
                "Security",
                "Restart tokens could not be moved into the Windows credential store.",
                exception.Message)
                .ConfigureAwait(false);
            return config;
        }
    }

    /// <summary>
    /// Resolves the effective restart token: credential-store reference first,
    /// then the legacy plaintext field.
    /// </summary>
    private async Task<string> ResolveRestartTokenAsync(
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(service.RestartTokenReference))
        {
            try
            {
                var stored = await _restartTokenStore
                    .LoadAsync(service.RestartTokenReference, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(stored))
                {
                    return stored;
                }
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or PlatformNotSupportedException or Win32Exception)
            {
                await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Service",
                    $"Could not read the restart token for {service.Name}.",
                    exception.Message)
                    .ConfigureAwait(false);
            }
        }

        return service.RestartToken.Trim();
    }

    public bool CloseToTray => _config.Behavior.CloseToTray;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await _configStore.LoadWithResultAsync(cancellationToken).ConfigureAwait(false);
            _config = await MigrateRestartTokensAsync(loaded.Config, cancellationToken).ConfigureAwait(false);
            ConfigureReleaseUpdateNetwork(_config.UpdateNetwork);
            if (loaded.Source == ConfigLoadSource.Defaults || loaded.WasMigrated)
            {
                await _configStore.SaveAsync(_config, cancellationToken).ConfigureAwait(false);
            }

            await RepairStartupRegistrationAsync(cancellationToken).ConfigureAwait(false);

            await WarmRestartTokenCacheAsync(cancellationToken).ConfigureAwait(false);

            ApplyHealthCheckInterval();

            await _logger.WriteAsync(
                DiagnosticSeverity.Information,
                "App",
                "JK Bridge initialized.",
                loaded.Warning,
                cancellationToken).ConfigureAwait(false);

            RunOnUi(() =>
            {
                ApplyConfigToViewModel();
                ApplyTheme(_config.Behavior.Theme);
                ApplySnapshot(_runtime.Snapshot);
                RefreshDiagnostics();
                ViewModel.ShowWelcomeTutorialIfNeeded(
                    _config.Behavior.WelcomeTutorialCompleted);
            });

            if (!_config.SetupCompleted)
            {
                RunOnUi(() => ViewModel.SelectedPage = AppPage.Setup);
                await _runtime.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (_config.Behavior.AutoConnect)
            {
                await _runtime.StartAsync(_config, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _runtime.RefreshAsync(_config, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown during startup.
        }
        catch (Exception exception)
        {
            await SafeLogAsync(DiagnosticSeverity.Error, "App", "Application initialization failed.", exception.ToString());
            ShowControllerError("初始化失败", exception.Message);
        }
    }

    public Task ConnectAsync()
    {
        Volatile.Write(ref _resumeAfterSleep, 0);
        if (!_config.SetupCompleted)
        {
            RunOnUi(() =>
            {
                ViewModel.SelectedPage = AppPage.Setup;
                ViewModel.ShowNotice("需要先配对设备", "运行 Linux 配对脚本并在“设备配置”页粘贴一次性配对码。");
            });
            return Task.CompletedTask;
        }

        var authorization = ForwardAuthorization.Evaluate(_config);
        if (authorization.Status == ForwardAuthorizationStatus.Unknown &&
            authorization.DesiredPorts.Count > 0)
        {
            RunOnUi(() =>
            {
                ViewModel.SelectedPage = AppPage.Setup;
                ViewModel.ShowNotice(
                    "升级后需要重新配对一次",
                    $"旧版没有保存 Linux 授权记录。重新配对后会确认端口 {string.Join(", ", authorization.DesiredPorts)}，以后端口变化将自动检测。");
            });
            return Task.CompletedTask;
        }

        if (authorization.Status == ForwardAuthorizationStatus.PairingRequired)
        {
            RunOnUi(() =>
            {
                ViewModel.SelectedPage = AppPage.Setup;
                ViewModel.ShowNotice(
                    "需要重新配对",
                    $"Linux 尚未授权端口 {string.Join(", ", authorization.MissingPorts)}。请运行一次性配对助手。");
            });
            return Task.CompletedTask;
        }

        return RunActionAsync("连接", () => _runtime.StartAsync(_config));
    }

    public Task DisconnectAsync()
    {
        Volatile.Write(ref _resumeAfterSleep, 0);
        return RunActionAsync("断开", () => _runtime.StopAsync());
    }

    public Task RefreshAsync()
    {
        if (!_config.SetupCompleted)
        {
            RunOnUi(() => ViewModel.SelectedPage = AppPage.Setup);
            return Task.CompletedTask;
        }

        return RunActionAsync("刷新", () => _runtime.RefreshAsync(_config));
    }

    public Task RetryAsync()
    {
        if (!_config.SetupCompleted)
        {
            RunOnUi(() => ViewModel.SelectedPage = AppPage.Setup);
            return Task.CompletedTask;
        }

        return RunActionAsync("立即重试", () => _runtime.RestartAsync(_config));
    }

    public Task RunDiagnosticsAsync() => RefreshAsync();

    public async Task PairDeviceAsync(PairingDraft pairing)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(ref _setupOperationActive, 1) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _pairingActive, 1);

        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var previous = Interlocked.Exchange(ref _pairingCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        RunOnUi(ViewModel.BeginPairing);
        var configurationSaved = false;

        try
        {
            var ports = _config.Services
                .Where(service => service.Enabled)
                .Select(service => ServiceRouteDefinition.Evaluate(service).Route!.RemoteTarget.Port)
                .Distinct()
                .Order()
                .ToArray();
            if (ports.Length == 0)
            {
                throw new PairingException("至少需要启用一个服务，Linux 才能生成精确的端口授权。");
            }

            await _runtime.StopAsync(cancellation.Token).ConfigureAwait(false);
            var progress = new Progress<DevicePairingProgress>(item =>
                RunOnUi(() => ViewModel.ApplyPairingProgress(item.Stage, item.Detail)));
            var result = await _pairingService.PairAsync(
                    new DevicePairingRequest(pairing.PairingCode, pairing.IdentityFile, ports),
                    progress,
                    cancellation.Token)
                .ConfigureAwait(false);

            var saved = await SaveConfigAsync(
                    current => current with
                    {
                        SetupCompleted = true,
                        ExpectedTailnetDnsSuffix = result.Server.TailnetDnsSuffix,
                        Server = current.Server with
                        {
                            DisplayName = result.Server.DisplayName,
                            Host = result.Server.ServerHost,
                            User = result.Server.SshUser,
                            Port = result.Server.SshPort,
                            IdentityFile = result.PortableIdentityFile,
                            HostKeyAlias = "jk-bridge-linux",
                            PinnedHostKey = result.Server.HostPublicKey,
                        },
                        Authorization = new ForwardAuthorizationGrant
                        {
                            IsKnown = true,
                            ClientKeyFingerprint = result.ClientFingerprint,
                            RemotePorts = result.Server.PermitPorts.Distinct().Order().ToList(),
                        },
                        Behavior = current.Behavior with { AutoConnect = true },
                    },
                    "设备配对配置已安全保存。")
                .ConfigureAwait(false);
            if (!saved)
            {
                throw new PairingException("Linux 已授权，但 Windows 配置保存失败；可使用同一密钥重新配对。");
            }
            configurationSaved = true;

            RunOnUi(() => ViewModel.CompletePairing(
                $"{result.Server.DisplayName} · {result.Server.ServerHost}"));
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Pairing",
                    "One-shot Linux pairing completed.",
                    $"Client fingerprint: {result.ClientFingerprint}")
                .ConfigureAwait(false);
            await _runtime.StartAsync(_config, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.FailPairing("配对已取消或超时；Linux 端的一次性配对码可能需要重新生成。"));
        }
        catch (Exception exception) when (exception is PairingException or IOException or UnauthorizedAccessException)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Pairing",
                    "One-shot Linux pairing did not complete.",
                    exception.Message)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailPairing(exception.Message));
        }
        catch (Exception exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Pairing",
                    configurationSaved
                        ? "Pairing configuration was saved, but the initial connection failed."
                        : "One-shot Linux pairing failed unexpectedly.",
                    exception.ToString())
                .ConfigureAwait(false);
            RunOnUi(() =>
            {
                if (configurationSaved)
                {
                    ViewModel.ShowNotice(
                        "设备已经配对",
                        "安全配置已保存，但首次连接未能启动。请打开诊断页后重试连接。");
                }
                else
                {
                    ViewModel.FailPairing("配对未能安全完成，请查看诊断信息后重新生成一次性配对码。");
                }
            });
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _pairingCancellation, null, cancellation), cancellation))
            {
                cancellation.Dispose();
            }

            Interlocked.Exchange(ref _pairingActive, 0);
            Interlocked.Exchange(ref _setupOperationActive, 0);
        }
    }

    public void InvalidateTransientSetupState()
    {
        _serviceProfileImportPreview = null;
    }

    public async Task ExportServiceProfileAsync()
    {
        if (!TryBeginServiceProfileAction(
                "正在选择导出位置并生成不含凭据的服务配置…",
                out var cancellation))
        {
            return;
        }

        try
        {
            var snapshot = CloneConfig(_config);
            var result = await _serviceProfileTransferService.ExportWithPickerAsync(
                    snapshot,
                    "JK Bridge services",
                    cancellation.Token)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.CompleteServiceProfileAction(
                result is null
                    ? "已取消导出，没有写入文件。"
                    : $"已导出 {result.ServiceCount} 个服务：{result.Path}。文件不含 SSH 密钥、配对码或服务器身份。",
                clearPreview: false));
            if (result is not null)
            {
                await SafeLogAsync(
                        DiagnosticSeverity.Information,
                        "Profile",
                        "Non-secret service profile exported.",
                        $"Services: {result.ServiceCount}; bytes: {result.DocumentBytes}")
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.CompleteServiceProfileAction("已取消导出，没有写入文件。", false));
        }
        catch (Exception exception) when (exception is
            ServiceProfileTransferException or
            ServiceProfileSerializationException or
            IOException or
            UnauthorizedAccessException)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Profile",
                    "Service profile export did not complete.",
                    exception.GetType().Name)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailServiceProfileAction(exception.Message, preservePreview: true));
        }
        finally
        {
            EndServiceProfileAction(cancellation);
        }
    }

    public async Task PreviewServiceProfileImportAsync()
    {
        if (!TryBeginServiceProfileAction(
                "正在只读验证服务配置并计算变更…",
                out var cancellation))
        {
            return;
        }

        try
        {
            _serviceProfileImportPreview = null;
            RunOnUi(ViewModel.ClearServiceProfilePreview);
            var snapshot = CloneConfig(_config);
            var preview = await _serviceProfileTransferService.PreviewImportWithPickerAsync(
                    snapshot,
                    cancellation.Token)
                .ConfigureAwait(false);
            if (preview is null)
            {
                RunOnUi(() => ViewModel.CompleteServiceProfileAction(
                    "已取消导入预览，本机配置没有改变。",
                    clearPreview: true));
                return;
            }

            _serviceProfileImportPreview = preview;
            RunOnUi(() => ViewModel.ApplyServiceProfilePreview(FormatProfilePreview(preview)));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.CompleteServiceProfileAction(
                "已取消导入预览，本机配置没有改变。",
                clearPreview: true));
        }
        catch (Exception exception) when (exception is
            ServiceProfileTransferException or
            ServiceProfileSerializationException or
            ConfigValidationException or
            IOException or
            UnauthorizedAccessException)
        {
            _serviceProfileImportPreview = null;
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Profile",
                    "Service profile import preview was rejected.",
                    exception.GetType().Name)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailServiceProfileAction(exception.Message, preservePreview: false));
        }
        finally
        {
            EndServiceProfileAction(cancellation);
        }
    }

    public async Task ApplyServiceProfileImportAsync(bool confirmed)
    {
        var preview = _serviceProfileImportPreview;
        if (!confirmed || preview is null)
        {
            RunOnUi(() => ViewModel.ServiceProfileFeedback =
                "必须先完成只读预览，并明确勾选确认后才能导入。任何账号、密钥都不会从配置文件导入。");
            return;
        }
        if (!TryBeginServiceProfileAction(
                "正在原子保存已确认的服务配置…",
                out var cancellation))
        {
            return;
        }

        try
        {
            ServiceProfileImportResult result;
            await _configGate.WaitAsync().ConfigureAwait(false);
            try
            {
                result = await _serviceProfileTransferService.ApplyImportAsync(
                        _config,
                        preview,
                        cancellation.Token)
                    .ConfigureAwait(false);
                _config = result.Config;
                _serviceProfileImportPreview = null;
            }
            finally
            {
                _configGate.Release();
            }

            var authorization = ForwardAuthorization.Evaluate(_config);
            var authorizationText = authorization.Status switch
            {
                ForwardAuthorizationStatus.PairingRequired =>
                    "导入包含尚未授权的 Linux 端口，连接前必须重新配对授权。",
                ForwardAuthorizationStatus.PairingRecommended =>
                    "Linux 仍保留部分未使用授权，建议重新配对以收紧权限。",
                ForwardAuthorizationStatus.Unknown =>
                    "当前无法核验 Linux 端口授权，连接前需要重新配对。",
                _ => "当前精确端口授权仍然匹配。",
            };
            RunOnUi(() =>
            {
                ApplyConfigToViewModel();
                ViewModel.CompleteServiceProfileAction(
                    $"已导入 {result.Config.Services.Count} 个服务。{authorizationText}" +
                    (result.BackupPath is null ? string.Empty : $" 备份：{result.BackupPath}"),
                    clearPreview: true);
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Profile",
                    "Confirmed service profile import applied.",
                    $"Added: {result.Diff.AddedServices}; modified: {result.Diff.ModifiedServices}; removed: {result.Diff.RemovedServices}")
                .ConfigureAwait(false);
            if (_config.SetupCompleted)
            {
                await _runtime.RefreshAsync(_config, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.FailServiceProfileAction(
                "服务配置导入已取消；请刷新服务状态确认最终结果。",
                preservePreview: false));
        }
        catch (Exception exception) when (exception is
            ServiceProfileTransferException or
            ServiceProfileSerializationException or
            ConfigValidationException or
            IOException or
            UnauthorizedAccessException)
        {
            _serviceProfileImportPreview = null;
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Profile",
                    "Confirmed service profile import was not applied.",
                    exception.GetType().Name)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailServiceProfileAction(
                $"导入未应用：{exception.Message} 请重新预览后再确认。",
                preservePreview: false));
        }
        finally
        {
            EndServiceProfileAction(cancellation);
        }
    }

    private bool TryBeginServiceProfileAction(
        string feedback,
        out CancellationTokenSource cancellation)
    {
        cancellation = null!;
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(ref _setupOperationActive, 1) != 0)
        {
            return false;
        }
        Interlocked.Exchange(ref _serviceProfileTransferActive, 1);
        cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var previous = Interlocked.Exchange(ref _serviceProfileCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        RunOnUi(() => ViewModel.BeginServiceProfileAction(feedback));
        return true;
    }

    private void EndServiceProfileAction(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(
                Interlocked.CompareExchange(ref _serviceProfileCancellation, null, cancellation),
                cancellation))
        {
            cancellation.Dispose();
        }
        Interlocked.Exchange(ref _serviceProfileTransferActive, 0);
        Interlocked.Exchange(ref _setupOperationActive, 0);
    }

    private static AppConfig CloneConfig(AppConfig config) => config with
    {
        Authorization = config.Authorization with
        {
            RemotePorts = config.Authorization.RemotePorts.ToList(),
        },
        Services = config.Services.Select(service => service with { }).ToList(),
        WindowsLocalServices = config.WindowsLocalServices.Select(service => service with { }).ToList(),
    };

    private static string FormatProfilePreview(ServiceProfileImportPreview preview)
    {
        var diff = preview.Diff;
        var authorization = diff.AuthorizationImpact switch
        {
            RemoteAuthorizationImpact.ReauthorizationRequired =>
                $"新增 Linux 端口：{string.Join(", ", diff.AddedLinuxPorts)}。导入后必须重新配对，才可连接这些端口。",
            RemoteAuthorizationImpact.ReauthorizationRecommended =>
                $"移除 Linux 端口：{string.Join(", ", diff.RemovedLinuxPorts)}。建议之后收紧 Linux 授权。",
            _ => "Linux 精确端口授权范围不变。",
        };
        return $"文件：{preview.SourcePath}\n" +
               $"配置名：{preview.DisplayName} · {preview.ServiceCount} 个服务\n" +
               $"新增 {diff.AddedServices} · 修改 {diff.ModifiedServices} · 删除 {diff.RemovedServices} · 不变 {diff.UnchangedServices}\n" +
               authorization;
    }

    private static string FormatPorts(IReadOnlyList<int> ports) =>
        ports.Count == 0 ? "无" : string.Join(", ", ports);

    private static string FormatProbeValue(bool value) => value ? "正常" : "失败";

    private static string FormatNullableProbeValue(bool? value, bool invert = false) => value switch
    {
        null => "未检查",
        true when invert => "是（冲突）",
        false when invert => "否（正常）",
        true => "是",
        false => "否",
    };

    public async Task OpenServiceAsync(string serviceId, string localUrl)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var service = _config.Services.FirstOrDefault(item => item.Id == serviceId);
        var route = service is null ? null : ServiceRouteDefinition.Evaluate(service).Route;
        if (service is null || route is null)
        {
            ShowControllerError("无法打开服务", "配置中的浏览器地址不是有效的 127.0.0.1 地址。");
            return;
        }

        var snapshot = _runtime.Snapshot.Services.FirstOrDefault(item => item.ServiceId == serviceId);
        if (!_runtime.Snapshot.IsConnected || !service.Enabled || snapshot is null || !snapshot.CanOpen)
        {
            var detail = snapshot?.Phase == ServicePhase.ForwardDenied
                ? "SSH 已明确拒绝这条远端转发，请重新配对并授权对应端口。"
                : "请先建立 SSH 隧道并确认该服务已启用。";
            ShowControllerError("服务尚不可打开", detail);
            return;
        }

        string? healthWarning = null;
        if (snapshot.Phase != ServicePhase.Available)
        {
            var health = await ProbeServiceAsync(service).ConfigureAwait(false);
            if (health.State != ServiceHealthState.Available)
            {
                healthWarning = health.Detail ?? "健康检查没有通过。";
                await SafeLogAsync(
                        DiagnosticSeverity.Warning,
                        "Service",
                        $"Opening {service.Name} despite an unavailable health result.",
                        healthWarning)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo(route.OpenUri.AbsoluteUri) { UseShellExecute = true });
            await SafeLogAsync(DiagnosticSeverity.Information, "Service", $"Opened {service.Name} in the browser.");
            if (healthWarning is not null)
            {
                RunOnUi(() => ViewModel.ShowNotice(
                    "健康检查未通过，已尝试打开",
                    $"SSH 转发仍在线；浏览器会继续访问本地入口。若是自签名 HTTPS，请确认浏览器证书提示。详情：{healthWarning}"));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await SafeLogAsync(DiagnosticSeverity.Error, "Service", $"Could not open {service.Name}.", exception.Message);
            ShowControllerError("无法打开浏览器", exception.Message);
        }
    }

    public async Task RetryServiceAsync(string serviceId)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var service = _config.Services.FirstOrDefault(item => item.Id == serviceId);
        if (service is null)
        {
            return;
        }

        await ProbeServiceAsync(service).ConfigureAwait(false);
    }

    public async Task CopyAddressAsync(string localUrl)
    {
        if (!Uri.TryCreate(localUrl, UriKind.Absolute, out var requestedUri))
        {
            return;
        }

        var route = _config.Services
            .Select(ServiceRouteDefinition.Evaluate)
            .Select(result => result.Route)
            .FirstOrDefault(candidate =>
                candidate is not null &&
                candidate.OpenUri.Equals(requestedUri));
        if (route is not null)
        {
            await SetClipboardTextAsync(route.OpenUri.AbsoluteUri);
            return;
        }

        var savedWindowsLocalUrl = _config.WindowsLocalServices
            .Select(service => WindowsLocalServiceUrl.TryBuild(
                service.Host,
                service.Scheme,
                service.Port,
                service.LaunchPath,
                out var url)
                ? url
                : null)
            .FirstOrDefault(url =>
                string.Equals(url, requestedUri.AbsoluteUri, StringComparison.Ordinal));
        var discoveredWindowsLocalUrl = _lastWindowsLocalDiscovery
            .Where(service => service.CanSave)
            .Select(service => service.OpenUrl)
            .FirstOrDefault(url =>
                string.Equals(url, requestedUri.AbsoluteUri, StringComparison.Ordinal));
        var allowedUrl = savedWindowsLocalUrl ?? discoveredWindowsLocalUrl;
        if (allowedUrl is not null)
        {
            await SetClipboardTextAsync(allowedUrl);
        }
    }

    public async Task SetServiceEnabledAsync(string serviceId, bool enabled)
    {
        await SaveConfigAsync(
            current => current with
            {
                Services = current.Services
                    .Select(service => service.Id == serviceId ? service with { Enabled = enabled } : service with { })
                    .ToList(),
            },
            "服务启用状态已保存。");
    }

    public async Task AddServiceAsync()
    {
        ServiceDefinition? addedService = null;
        try
        {
            var saved = await SaveConfigAsync(
                current =>
                {
                    var suffix = 1;
                    while (current.Services.Any(service => service.Id == $"service-{suffix}"))
                    {
                        suffix++;
                    }

                    var port = 8800;
                    while (current.Services.Any(service => service.LocalPort == port) && port < 65535)
                    {
                        port++;
                    }

                    if (port > 65535)
                    {
                        throw new InvalidOperationException("没有可自动分配的本地端口。");
                    }

                    addedService = new ServiceDefinition
                    {
                        Id = $"service-{suffix}",
                        Name = $"新服务 {suffix}",
                        Description = "编辑名称、端口和浏览器地址后保存",
                        Icon = "Globe",
                        Enabled = false,
                        LocalPort = port,
                        RemotePort = port,
                        OpenUrl = $"http://127.0.0.1:{port}/",
                        HealthPath = "/",
                        HealthKind = HealthCheckKind.HttpStatus,
                    };
                    return current with
                    {
                        Services = current.Services.Select(item => item with { }).Append(addedService).ToList(),
                    };
                },
                "已添加一个停用的新服务，请编辑后再启用。");

            if (saved && addedService is not null)
            {
                RunOnUi(() =>
                {
                    ViewModel.SelectedPage = AppPage.Services;
                    ViewModel.SelectedService = ViewModel.Services.FirstOrDefault(item => item.Id == addedService.Id);
                });
            }
        }
        catch (InvalidOperationException exception)
        {
            ShowControllerError("无法添加服务", exception.Message);
        }
    }

                    private static string IconForDiscoveredTemplate(string templateId) =>
        templateId switch
        {
            "syncthing" => "Sync",
            "jupyter" => "Notebook",
            "overleaf" => "FileText",
            "grafana" => "Chart",
            "ollama" => "Cpu",
            _ => "Globe",
        };

    public async Task SaveServiceAsync(ServiceUiState state)
    {
        string? tokenStoreWarning = null;
        try
        {
            await SaveConfigAsync(
                current =>
                {
                    var existing = current.Services.FirstOrDefault(service => service.Id == state.Id)
                        ?? throw new InvalidOperationException("该服务已不存在，请刷新后重试。");

                    var updated = existing with
                    {
                        Name = state.Name.Trim(),
                        Description = state.Description.Trim(),
                        LocalPort = state.LocalPort,
                        RemotePort = state.RemotePort,
                        OpenUrl = state.LocalUrl.Trim(),
                        HealthPath = state.HealthPath.Trim(),
                        HealthKind = state.HealthKind,
                        Enabled = state.IsEnabled,
                        RestartPath = state.RestartPath.Trim(),
                        RestartLocalPort = state.RestartLocalPort,
                    };

                    // The token lives in the OS credential store: an empty box means
                    // "no token", a non-empty box replaces the stored secret.
                    var token = state.RestartToken.Trim();
                    if (token.Length > 0)
                    {
                        try
                        {
                            var reference = _restartTokenStore
                                .SaveAsync(state.Id, token)
                                .GetAwaiter()
                                .GetResult();
                            lock (_restartTokenCache)
                            {
                                _restartTokenCache[reference] = token;
                            }

                            updated = updated with { RestartToken = string.Empty, RestartTokenReference = reference };
                        }
                        catch (Exception exception) when (exception is
                            InvalidOperationException or PlatformNotSupportedException or IOException or
                                UnauthorizedAccessException or Win32Exception)
                        {
                            // Fall back to the config field so the action keeps working.
                            updated = updated with { RestartToken = token, RestartTokenReference = string.Empty };
                            tokenStoreWarning = exception.Message;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(existing.RestartTokenReference))
                    {
                        try
                        {
                            _restartTokenStore.DeleteAsync(existing.RestartTokenReference).GetAwaiter().GetResult();
                        }
                        catch (Exception exception) when (exception is
                            InvalidOperationException or PlatformNotSupportedException or Win32Exception)
                        {
                            // The reference is dropped from config regardless.
                        }

                        lock (_restartTokenCache)
                        {
                            _restartTokenCache.Remove(existing.RestartTokenReference);
                        }

                        updated = updated with { RestartToken = string.Empty, RestartTokenReference = string.Empty };
                    }

                    return current with
                    {
                        Services = current.Services
                            .Select(service => service.Id == state.Id ? updated : service with { })
                            .ToList(),
                    };
                },
                $"{state.Name.Trim()} 已保存。");
            if (!string.IsNullOrWhiteSpace(tokenStoreWarning))
            {
                RunOnUi(() => ViewModel.ShowNotice(
                    "令牌已保存在配置文件中",
                    "Windows 凭据管理器当前不可用，重启令牌退回了配置文件。请检查凭据管理器后重新保存一次。"));
            }
        }
        catch (InvalidOperationException exception)
        {
            ShowControllerError("无法保存服务", exception.Message);
        }
    }

    public async Task RemoveServiceAsync(string serviceId)
    {
        try
        {
            await SaveConfigAsync(
                current =>
                {
                    if (current.Services.Count <= 1)
                    {
                        throw new InvalidOperationException("至少需要保留一个服务定义。");
                    }

                    return current with
                    {
                        Services = current.Services
                            .Where(service => service.Id != serviceId)
                            .Select(service => service with { })
                            .ToList(),
                    };
                },
                "服务已删除。");
        }
        catch (InvalidOperationException exception)
        {
            ShowControllerError("不能删除", exception.Message);
        }
    }

    public void PreviewTheme(ThemePreference theme)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        RunOnUi(() => ApplyTheme(theme));
    }

    public async Task CompleteWelcomeTutorialAsync()
    {
        var saved = await SaveConfigAsync(
            current => current with
            {
                Behavior = current.Behavior with
                {
                    WelcomeTutorialCompleted = true,
                },
            },
            "教程完成状态已保存。",
            refreshRuntime: false).ConfigureAwait(false);
        RunOnUi(() =>
        {
            if (saved)
            {
                ViewModel.MarkWelcomeTutorialCompleted();
            }
            else
            {
                ViewModel.WelcomeTutorialFeedback =
                    "暂时无法保存，请检查诊断信息后重试。";
            }
        });
    }

    public async Task SaveSettingsAsync(SettingsDraft settings)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _releaseUpdateActive) != 0)
        {
            RunOnUi(() => ViewModel.SettingsFeedback =
                "更新操作正在进行；请先完成或取消更新，再保存网络设置。");
            return;
        }

        if (!int.TryParse(settings.SshPort, out var sshPort))
        {
            RunOnUi(() => ViewModel.SettingsFeedback = "SSH 端口必须是 1–65535 的整数。");
            return;
        }

        if (settings.HealthCheckIntervalSeconds is < 5 or > 300)
        {
            RunOnUi(() => ViewModel.SettingsFeedback = "健康检查间隔必须是 5–300 秒。");
            return;
        }

        var theme = settings.ThemeIndex switch
        {
            1 => ThemePreference.Light,
            2 => ThemePreference.Dark,
            _ => ThemePreference.System,
        };
        var updateNetworkMode = settings.UpdateNetworkModeIndex switch
        {
            1 => UpdateNetworkMode.ManualProxy,
            2 => UpdateNetworkMode.Direct,
            _ => UpdateNetworkMode.System,
        };
        AppConfig BuildSettingsConfig(AppConfig current)
        {
            var server = current.Server with
            {
                Host = settings.LinuxHost.Trim().TrimEnd('.'),
                User = settings.LinuxUser.Trim(),
                Port = sshPort,
                IdentityFile = settings.IdentityFile.Trim(),
                PinnedHostKey = settings.PinnedHostKey.Trim(),
            };
            var identityChanged = server.Host != current.Server.Host ||
                                  server.User != current.Server.User ||
                                  server.Port != current.Server.Port ||
                                  server.IdentityFile != current.Server.IdentityFile ||
                                  server.PinnedHostKey != current.Server.PinnedHostKey;
            return current with
            {
                Server = server,
                Authorization = identityChanged ? new ForwardAuthorizationGrant() : current.Authorization with
                {
                    RemotePorts = current.Authorization.RemotePorts.ToList(),
                },
                Behavior = current.Behavior with
                {
                    AutoConnect = settings.AutoConnect,
                    AutoReconnect = settings.AutoReconnect,
                    CloseToTray = settings.CloseToTray,
                    StartWithWindows = settings.StartWithWindows,
                    Theme = theme,
                    HealthCheckIntervalSeconds = settings.HealthCheckIntervalSeconds,
                },
                UpdateNetwork = new UpdateNetworkSettings
                {
                    Mode = updateNetworkMode,
                    ManualProxyUrl = updateNetworkMode == UpdateNetworkMode.ManualProxy
                        ? settings.UpdateManualProxyUrl.Trim()
                        : string.Empty,
                },
            };
        }

        var validation = ConfigValidator.Validate(BuildSettingsConfig(_config));
        if (!validation.IsValid)
        {
            var detail = string.Join("；", validation.Issues.Select(issue => issue.Message));
            RunOnUi(() => ViewModel.SettingsFeedback = $"未保存：{detail}");
            return;
        }

        try
        {
            if (settings.StartWithWindows)
            {
                _startupRegistration.EnableCurrentProcess("--background");
            }
            else
            {
                _startupRegistration.Disable();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RunOnUi(() => ViewModel.SettingsFeedback = $"无法更新开机启动：{exception.Message}");
            return;
        }

        if (await SaveConfigAsync(BuildSettingsConfig, "设置已保存，连接配置已经重新应用。"))
        {
            ConfigureReleaseUpdateNetwork(_config.UpdateNetwork);
            ApplyHealthCheckInterval();
            RunOnUi(() =>
            {
                ApplyTheme(theme);
                ViewModel.SettingsFeedback = "设置已保存；新的更新网络方式将在下一次检查或下载时使用。";
            });
        }
    }

    /// <summary>
    /// Pushes the configured health-check interval into the running supervisor.
    /// </summary>
    private void ApplyHealthCheckInterval()
    {
        var seconds = _config.Behavior.HealthCheckIntervalSeconds;
        if (seconds is < 5 or > 300)
        {
            return;
        }

        try
        {
            _runtime.SetHealthCheckInterval(TimeSpan.FromSeconds(seconds));
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or ObjectDisposedException)
        {
            // The runtime replaces its supervisor loop on reconnect; the next
            // loop iteration picks the interval up.
        }
    }

    public async Task OpenReleasePageAsync()
    {
        var releasePage = _pendingReleasePage;
        if (releasePage is null)
        {
            RunOnUi(() => ViewModel.UpdateCheckFeedback = "请先检查更新，再打开对应的官方 Release 页面。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(releasePage.AbsoluteUri)
            {
                UseShellExecute = true,
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Updates",
                    "The official release page was explicitly opened in the default browser.")
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            await SetClipboardTextAsync(releasePage.AbsoluteUri).ConfigureAwait(false);
            RunOnUi(() => ViewModel.UpdateCheckFeedback =
                "无法打开默认浏览器；官方 Release 地址已复制到剪贴板。");
        }
    }

    public async Task CopyLinuxQuickPairCommandAsync()
    {
        await SetClipboardTextAsync(LinuxPairingBootstrap.PairCommand).ConfigureAwait(false);
        RunOnUi(() => ViewModel.LinuxQuickPairFeedback =
            "一行命令已复制。请粘贴到 Linux 终端运行，再把以 jkbridge:// 开头的配对码填回这里。");
    }

        public async Task CopyDiagnosticSummaryAsync()
    {
        var snapshot = _runtime.Snapshot;
        var summary = new StringBuilder()
            .AppendLine("JK Bridge diagnostic summary")
            .AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"Windows device: {Environment.MachineName}")
            .AppendLine($"Phase: {snapshot.Phase}")
            .AppendLine($"Expected tailnet: {_config.ExpectedTailnetDnsSuffix}")
            .AppendLine($"Linux peer: {_config.Server.Host}")
            .AppendLine($"Resolved target: {snapshot.TargetHost ?? "not resolved"}")
            .AppendLine($"Runtime IP fallback: {snapshot.UsingIpFallback}")
            .AppendLine($"SSH PID: {snapshot.SshProcessId?.ToString() ?? "not running"}")
            .AppendLine($"Host fingerprint: {ComputeFingerprint(_config.Server.PinnedHostKey)}")
            .AppendLine("Services:");
        foreach (var service in snapshot.Services)
        {
            summary.AppendLine($"- {service.Name}: {service.Phase}, HTTP {service.HttpStatus?.ToString() ?? "n/a"}");
        }

        await SetClipboardTextAsync(summary.ToString());
        await SafeLogAsync(DiagnosticSeverity.Information, "Diagnostics", "Sanitized diagnostic summary copied.");
    }

    public async Task ExportDiagnosticBundleAsync()
    {
        if (Interlocked.CompareExchange(ref _diagnosticBundleActive, 1, 0) != 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Interlocked.Exchange(ref _diagnosticBundleCancellation, cancellation)?.Dispose();
        RunOnUi(() =>
        {
            ViewModel.IsDiagnosticBundleBusy = true;
            ViewModel.DiagnosticBundleFeedback =
                "请选择保存位置。ZIP 只包含匿名运行状态和最近的脱敏日志。";
        });

        try
        {
            var result = await _diagnosticBundleService.ExportWithPickerAsync(
                    new DiagnosticBundleInput(
                        GetApplicationVersion(),
                        _config,
                        _runtime.Snapshot,
                        _logger.Snapshot()),
                    cancellation.Token)
                .ConfigureAwait(false);
            if (result is null)
            {
                RunOnUi(() => ViewModel.DiagnosticBundleFeedback =
                    "已取消导出；没有创建诊断包。");
                return;
            }

            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Diagnostics",
                    "A redacted diagnostic bundle was explicitly exported.",
                    $"Included log events: {result.IncludedLogEvents}.")
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.DiagnosticBundleFeedback =
                $"已导出安全诊断包（{result.IncludedLogEvents} 条脱敏记录）。保存位置：{result.Path}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.DiagnosticBundleFeedback =
                "诊断包导出已取消或超时；不会留下未完成的目标文件。");
        }
        catch (Exception exception) when (
            exception is DiagnosticBundleException or IOException or UnauthorizedAccessException)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Diagnostics",
                    "Diagnostic bundle export failed safely.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.DiagnosticBundleFeedback =
                "诊断包未导出。请确认目标目录可写，且不是符号链接或重解析点。");
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _diagnosticBundleCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
            Interlocked.Exchange(ref _diagnosticBundleActive, 0);
            RunOnUi(() => ViewModel.IsDiagnosticBundleBusy = false);
        }
    }

    public async Task CheckForUpdatesAsync()
    {
        if (Interlocked.CompareExchange(ref _releaseUpdateActive, 1, 0) != 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Interlocked.Exchange(ref _releaseUpdateCancellation, cancellation)?.Dispose();
        RunOnUi(() =>
        {
            ViewModel.IsUpdateChecking = true;
            ViewModel.HasUpdateReleaseInfo = false;
            ViewModel.HasUpdateInstaller = false;
            ViewModel.IsUpdateReadyToInstall = false;
            ViewModel.IsUpdateProgressVisible = false;
            ViewModel.IsUpdateProgressIndeterminate = false;
            ViewModel.UpdateProgressPercent = 0;
            ViewModel.UpdateProgressText = "尚未开始下载";
            ViewModel.UpdateStageText = "正在检查版本";
            ViewModel.UpdateReleaseUrl = string.Empty;
            ViewModel.UpdateCheckFeedback =
                "正在查询 JK Bridge 官方 GitHub Releases…";
        });
        _pendingReleaseInstaller = null;
        _pendingReleasePage = null;
        _downloadedReleaseInstaller = null;

        try
        {
            var result = await _releaseUpdateChecker
                .CheckAsync(GetApplicationVersion(), cancellation.Token)
                .ConfigureAwait(false);
            _pendingReleasePage = result.ReleasePage;
            RunOnUi(() =>
            {
                ViewModel.HasUpdateReleaseInfo = true;
                ViewModel.HasUpdateInstaller = result.Installer is not null;
                ViewModel.UpdateReleaseUrl = result.ReleasePage.AbsoluteUri;
                ViewModel.UpdateCheckFeedback = result.Installer is not null
                    ? result.UpdateAvailable
                        ? $"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）。可先下载并核验 SHA-256，完成后再单独开始安装。"
                        : $"当前版本为 {result.CurrentVersion}；可重新下载并核验官方 {result.LatestVersion} 安装器，再决定是否开始安装。"
                    : result.UpdateAvailable
                        ? $"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}），但该 Release 没有成对的 Windows 安装器和 SHA-256；请使用下方官方地址。"
                        : $"没有发现比当前构建号 {result.CurrentVersion} 更新的正式 Release；当前构建可能是未发布开发版。官方最新正式版：{result.LatestVersion}。";
            });
            _pendingReleaseInstaller = result.Installer;
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Updates",
                    "A manual read-only release update check completed.",
                    result.UpdateAvailable ? "A newer release is available." : "The current release is up to date.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.UpdateCheckFeedback =
                "更新检查已取消或超时；没有下载或安装任何内容。");
        }
        catch (ReleaseUpdateException exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Updates",
                    "Manual release update check failed safely.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.UpdateCheckFeedback =
                "无法从固定的 JK Bridge 官方 GitHub Releases 接口确认版本；没有下载或安装任何内容。");
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _releaseUpdateCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
            Interlocked.Exchange(ref _releaseUpdateActive, 0);
            RunOnUi(() => ViewModel.IsUpdateChecking = false);
        }
    }

    public async Task InstallCheckedReleaseAsync()
    {
        var installer = _pendingReleaseInstaller;
        if (installer is null || Interlocked.CompareExchange(ref _releaseUpdateActive, 1, 0) != 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        Interlocked.Exchange(ref _releaseUpdateCancellation, cancellation)?.Dispose();
        RunOnUi(() =>
        {
            ViewModel.IsUpdateChecking = true;
            ViewModel.IsUpdateReadyToInstall = false;
            ViewModel.IsUpdateProgressVisible = true;
            ViewModel.IsUpdateProgressIndeterminate = true;
            ViewModel.UpdateProgressPercent = 0;
            ViewModel.UpdateStageText = "准备下载";
            ViewModel.UpdateProgressText = "等待官方服务器响应";
            ViewModel.UpdateCheckFeedback =
                $"正在通过{DescribeUpdateNetwork(_config.UpdateNetwork)}下载官方 {installer.Version} 安装器并核验 SHA-256…";
        });
        _downloadedReleaseInstaller = null;

        try
        {
            var progress = new InlineProgress<ReleaseDownloadProgress>(
                ReportReleaseDownloadProgress);
            var download = await _releaseUpdateChecker
                .DownloadInstallerAsync(
                    installer,
                    _updateDirectory,
                    progress,
                    cancellation.Token)
                .ConfigureAwait(false);
            ValidateDownloadedInstallerPath(download.InstallerPath);
            _downloadedReleaseInstaller = download;
            RunOnUi(() =>
            {
                ViewModel.IsUpdateReadyToInstall = true;
                ViewModel.IsUpdateProgressIndeterminate = false;
                ViewModel.UpdateProgressPercent = 100;
                ViewModel.UpdateStageText = "下载与校验完成";
                ViewModel.UpdateProgressText = "100% · SHA-256 校验通过";
                ViewModel.UpdateCheckFeedback =
                    $"官方 {download.Version} 安装器已下载并通过 SHA-256 校验。此时 JK Bridge 仍在运行；确认后再点击“开始安装”。校验值：{download.Sha256}";
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Updates",
                    "An official installer was downloaded and verified for explicit installation.",
                    $"Release: {installer.Version}; SHA-256: {download.Sha256}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _downloadedReleaseInstaller = null;
            RunOnUi(() =>
            {
                ViewModel.IsUpdateReadyToInstall = false;
                ViewModel.IsUpdateProgressIndeterminate = false;
                ViewModel.UpdateStageText = "下载已取消";
                ViewModel.UpdateProgressText = "已保留当前版本，没有启动安装程序";
                ViewModel.UpdateCheckFeedback =
                    "安装器下载已取消或超时；没有启动任何安装程序，JK Bridge 会继续运行。";
            });
        }
        catch (Exception exception) when (
            exception is ReleaseUpdateException or IOException or UnauthorizedAccessException or
                InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _downloadedReleaseInstaller = null;
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Updates",
                    "The explicit installer download or verification failed safely.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() =>
            {
                ViewModel.IsUpdateReadyToInstall = false;
                ViewModel.IsUpdateProgressIndeterminate = false;
                ViewModel.UpdateStageText = "下载或校验失败";
                ViewModel.UpdateProgressText = "没有启动安装程序";
                ViewModel.UpdateCheckFeedback =
                    "无法完成官方安装器下载或 SHA-256 校验；没有执行覆盖安装。可改用上方官方 Release 地址手动下载。";
            });
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _releaseUpdateCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }

            Interlocked.Exchange(ref _releaseUpdateActive, 0);
            RunOnUi(() => ViewModel.IsUpdateChecking = false);
        }
    }

    private string ValidateDownloadedInstallerPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(_updateDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            throw new ReleaseUpdateException(
                "The verified installer was not staged in the managed update directory.");
        }

        return fullPath;
    }

    private static void LaunchInstaller(string installerPath)
    {
        using var process = Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
        }) ?? throw new System.ComponentModel.Win32Exception(
            "Windows did not return a process for the installer launch.");
    }

    public Task OpenLogFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowControllerError("无法打开日志目录", exception.Message);
        }

        return Task.CompletedTask;
    }

    public async Task SuspendForSleepAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var currentlyConnectedByIntent = _runtime.Snapshot.DesiredConnected;
        var shouldResume = currentlyConnectedByIntent || Volatile.Read(ref _resumeAfterSleep) != 0;
        Volatile.Write(ref _resumeAfterSleep, shouldResume ? 1 : 0);
        if (currentlyConnectedByIntent)
        {
            await _runtime.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        RunOnUi(() => ViewModel.ShowNotice(
            "系统即将睡眠",
            shouldResume ? "SSH 隧道已安全停止，唤醒后会自动重建。" : "当前没有活动隧道。"));
    }

    public async Task ResumeFromSleepAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _resumeAfterSleep) == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _resumeAfterSleep, 0) != 0 && Volatile.Read(ref _disposed) == 0)
        {
            await _runtime.StartAsync(_config, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var pairingCancellation = Interlocked.Exchange(ref _pairingCancellation, null);
        pairingCancellation?.Cancel();
        pairingCancellation?.Dispose();
        _pendingReleaseInstaller = null;
        _downloadedReleaseInstaller = null;
        _serviceProfileImportPreview = null;
        var serviceProfileCancellation = Interlocked.Exchange(ref _serviceProfileCancellation, null);
        serviceProfileCancellation?.Cancel();
        serviceProfileCancellation?.Dispose();
        var diagnosticBundleCancellation = Interlocked.Exchange(
            ref _diagnosticBundleCancellation,
            null);
        diagnosticBundleCancellation?.Cancel();
        diagnosticBundleCancellation?.Dispose();
        var releaseUpdateCancellation = Interlocked.Exchange(ref _releaseUpdateCancellation, null);
        releaseUpdateCancellation?.Cancel();
        releaseUpdateCancellation?.Dispose();
        var windowsDiscoveryCancellation = Interlocked.Exchange(ref _windowsLocalDiscoveryCancellation, null);
        windowsDiscoveryCancellation?.Cancel();
        windowsDiscoveryCancellation?.Dispose();

        await _configGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _runtime.SnapshotChanged -= RuntimeOnSnapshotChanged;
            _logger.EventWritten -= LoggerOnEventWritten;
            await _runtime.DisposeAsync().ConfigureAwait(false);
            await _logger.DisposeAsync().ConfigureAwait(false);
            _configStore.Dispose();
            if (_ownsReleaseUpdateChecker && _releaseUpdateChecker is IDisposable disposableUpdateChecker)
            {
                disposableUpdateChecker.Dispose();
            }
            if (_ownsWindowsLocalDiscovery && _windowsLocalDiscovery is IDisposable disposableDiscovery)
            {
                disposableDiscovery.Dispose();
            }
        }
        finally
        {
            _configGate.Release();
        }
    }

    private static string GetApplicationVersion()
    {
        var assembly = typeof(AppController).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0];
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "2.2.1"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private async Task<bool> SaveConfigAsync(
        Func<AppConfig, AppConfig> createNext,
        string successMessage,
        bool refreshRuntime = true)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        await _configGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            var next = createNext(_config);
            ConfigValidator.EnsureValid(next);
            await _configStore.SaveAsync(next).ConfigureAwait(false);
            _config = next;
            var authorization = ForwardAuthorization.Evaluate(next);
            var effectiveSuccessMessage = authorization.Status switch
            {
                ForwardAuthorizationStatus.Unknown when authorization.DesiredPorts.Count > 0 =>
                    $"{successMessage} 旧版授权记录未知，请重新配对一次。",
                ForwardAuthorizationStatus.PairingRequired =>
                    $"{successMessage} 新端口尚未授权，请重新配对。",
                ForwardAuthorizationStatus.PairingRecommended =>
                    $"{successMessage} Linux 仍保留未使用端口授权，建议重新配对以收紧权限。",
                _ => successMessage,
            };
            RunOnUi(() =>
            {
                ApplyConfigToViewModel();
                ViewModel.SettingsFeedback = effectiveSuccessMessage;
                ViewModel.ServiceFeedback = effectiveSuccessMessage;
            });

            await SafeLogAsync(DiagnosticSeverity.Information, "Config", effectiveSuccessMessage).ConfigureAwait(false);
            if (refreshRuntime)
            {
                if (_config.SetupCompleted)
                {
                    await _runtime.RefreshAsync(_config).ConfigureAwait(false);
                }
                else
                {
                    await _runtime.StopAsync().ConfigureAwait(false);
                }
            }

            return true;
        }
        catch (ConfigValidationException exception)
        {
            var detail = string.Join("；", exception.Issues.Select(issue => $"{issue.Path}: {issue.Message}"));
            RunOnUi(() =>
            {
                ViewModel.SettingsFeedback = $"未保存：{detail}";
                ViewModel.ServiceFeedback = $"未保存：{detail}";
            });
            ShowControllerError("配置未保存", detail);
            await SafeLogAsync(DiagnosticSeverity.Warning, "Config", "Invalid configuration was not saved.", detail)
                .ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RunOnUi(() =>
            {
                ViewModel.SettingsFeedback = $"保存失败：{exception.Message}";
                ViewModel.ServiceFeedback = $"保存失败：{exception.Message}";
            });
            await SafeLogAsync(DiagnosticSeverity.Error, "Config", "Configuration could not be saved.", exception.Message)
                .ConfigureAwait(false);
            return false;
        }
        finally
        {
            _configGate.Release();
        }
    }

    private void RuntimeOnSnapshotChanged(object? sender, TunnelSnapshot snapshot) =>
        RunOnUi(() => ApplySnapshot(snapshot));

    /// <summary>
    /// Raised when an enabled service transitions into a broken phase so the
    /// shell layer can surface a desktop notification.
    /// </summary>
    public event EventHandler<ServiceAlert>? ServiceAlert;

    private readonly Dictionary<string, ServicePhase> _lastServicePhases = new(StringComparer.Ordinal);

    private void DetectServiceAlerts(IReadOnlyList<ServiceSnapshot> services)
    {
        foreach (var service in services)
        {
            var definition = _config.Services.FirstOrDefault(item => item.Id == service.ServiceId);
            if (definition is null || !definition.Enabled)
            {
                continue;
            }

            var seen = _lastServicePhases.TryGetValue(service.ServiceId, out var previous);
            _lastServicePhases[service.ServiceId] = service.Phase;
            if (!seen)
            {
                continue;
            }

            var broken = service.Phase is ServicePhase.RemoteUnavailable or ServicePhase.ForwardDenied;
            var wasBroken = previous is ServicePhase.RemoteUnavailable or ServicePhase.ForwardDenied;
            if (broken && !wasBroken)
            {
                ServiceAlert?.Invoke(this, new ServiceAlert(
                    service.ServiceId,
                    service.Name,
                    service.Phase == ServicePhase.ForwardDenied ? "SSH 转发被拒绝" : "服务无响应",
                    service.Detail ?? "请检查 Linux 上的服务状态。"));
            }
        }
    }

    private void LoggerOnEventWritten(object? sender, DiagnosticEvent entry) =>
        RunOnUi(RefreshDiagnostics);

    private void ApplySnapshot(TunnelSnapshot snapshot)
    {
        var enabled = snapshot.Services.Count(service => service.Phase != ServicePhase.Disabled);
        var available = snapshot.Services.Count(service => service.Phase == ServicePhase.Available);
        var connecting = snapshot.Phase is TunnelPhase.Preflight or TunnelPhase.Starting or
            TunnelPhase.Reconnecting or TunnelPhase.Stopping;
        var connected = snapshot.Phase == TunnelPhase.Connected;
        var degraded = snapshot.Phase == TunnelPhase.Degraded;
        var hasIssue = degraded || snapshot.Phase is TunnelPhase.Reconnecting or
            TunnelPhase.NeedsTailscale or TunnelPhase.WrongTailnet or
            TunnelPhase.PeerUnavailable or TunnelPhase.NeedsKeySetup or TunnelPhase.NeedsPairing or
            TunnelPhase.HostKeyBlocked or
            TunnelPhase.PortConflict or TunnelPhase.Faulted;

        ViewModel.ApplyBridgeState(new BridgeUiState(
            StatusFor(snapshot.Phase),
            SummaryFor(snapshot, available, enabled),
            connected || degraded
                ? "断开"
                : connecting
                    ? "取消"
                    : snapshot.Phase == TunnelPhase.NeedsPairing ? "重新配对" : "连接全部",
            connected,
            connecting,
            degraded,
            ShortPeerName(snapshot.PeerDnsName ?? _config.Server.Host),
            snapshot.PeerDnsName ?? _config.Server.Host,
            $"{available} / {enabled} 可用",
            $"{snapshot.UpdatedAt:HH:mm:ss} 更新",
            hasIssue,
            hasIssue ? StatusFor(snapshot.Phase) : "连接运行正常",
            hasIssue ? DetailFor(snapshot) : "固定主机密钥、SSH 转发与网页健康检查运行正常。"));

        var runtimeServices = snapshot.Services.Count == 0
            ? _config.Services.Select(service => new ServiceSnapshot(
                service.Id,
                service.Name,
                service.Enabled ? ServicePhase.WaitingForTunnel : ServicePhase.Disabled)).ToArray()
            : snapshot.Services;
        ViewModel.UpdateServices(_config.Services.Select(service =>
            ToServiceUiState(
                service,
                runtimeServices.FirstOrDefault(item => item.ServiceId == service.Id)
                ?? new ServiceSnapshot(service.Id, service.Name, ServicePhase.WaitingForTunnel))));

        DetectServiceAlerts(runtimeServices);

        ViewModel.TailscaleExecutable = ExecutableLocator.FindTailscale() ?? "未找到 tailscale.exe";
        ViewModel.HasTailscaleIssue = snapshot.Phase is TunnelPhase.NeedsTailscale or TunnelPhase.WrongTailnet;
        var hasTailscaleIdentity = !string.IsNullOrWhiteSpace(snapshot.TailscaleSelfDnsName) ||
                                   !string.IsNullOrWhiteSpace(snapshot.CurrentTailnetDnsSuffix);
        ViewModel.IsTailscaleHealthy = hasTailscaleIdentity && !ViewModel.HasTailscaleIssue;
        ViewModel.TailscaleStatusText = ViewModel.HasTailscaleIssue
            ? "需要处理"
            : ViewModel.IsTailscaleHealthy ? "正常" : "检查中";
        ViewModel.TailscaleIdentity = snapshot.Phase == TunnelPhase.NeedsTailscale
            ? "未登录或未运行"
            : !string.IsNullOrWhiteSpace(snapshot.TailscaleLoginName)
                ? snapshot.TailscaleLoginName
                : snapshot.TailscaleSelfDnsName ?? "已登录，账号名不可用";
        ViewModel.TailnetName = string.IsNullOrWhiteSpace(snapshot.CurrentTailnetDnsSuffix)
            ? $"预期：{_config.ExpectedTailnetDnsSuffix}"
            : $"当前：{snapshot.CurrentTailnetDnsSuffix} · 预期：{_config.ExpectedTailnetDnsSuffix}";
        ViewModel.PeerRoute = snapshot.TargetHost is null
            ? "尚未解析 Linux 节点"
            : snapshot.UsingIpFallback
                ? $"{snapshot.TargetHost}（仅本次 IP 回退）"
                : $"MagicDNS · {snapshot.TargetHost}";
        ViewModel.IsLinuxHealthy = !string.IsNullOrWhiteSpace(snapshot.PeerDnsName) ||
                                   !string.IsNullOrWhiteSpace(snapshot.TargetHost);
        ViewModel.HasLinuxIssue = snapshot.Phase == TunnelPhase.PeerUnavailable;
        ViewModel.LinuxStatusText = ViewModel.HasLinuxIssue
            ? "不可用"
            : ViewModel.IsLinuxHealthy ? "在线" : "待检查";
        ViewModel.SshExecutable = ExecutableLocator.FindOpenSsh() ?? "未找到 ssh.exe";
        ViewModel.SshProcess = snapshot.SshProcessId is int pid ? $"PID {pid} · 正在运行" : "未启动";
        ViewModel.IsSshHealthy = snapshot.SshProcessId is not null && snapshot.IsConnected;
        ViewModel.HasSshIssue = snapshot.Phase is TunnelPhase.NeedsKeySetup or TunnelPhase.NeedsPairing or
            TunnelPhase.HostKeyBlocked or
            TunnelPhase.PortConflict or TunnelPhase.Faulted;
        ViewModel.SshStatusText = ViewModel.HasSshIssue
            ? "需要处理"
            : ViewModel.IsSshHealthy ? "已验证" : "未启动";
        ViewModel.HostFingerprint = ComputeFingerprint(_config.Server.PinnedHostKey);
        UpdateConnectionDoctor(snapshot, enabled, available);
    }

    private void UpdateConnectionDoctor(
        TunnelSnapshot snapshot,
        int enabledServices,
        int availableServices)
    {
        var diagnosis = snapshot.Phase switch
        {
            TunnelPhase.NeedsTailscale => (
                "第 1 层未通过",
                "Tailscale 0/1 · Linux 0/1 · SSH 0/1 · 网页 0/1",
                "Windows 尚未获得可用的 Tailscale 身份。",
                "Tailscale 未运行、未登录，或本机状态不可读。",
                "先打开 Tailscale 并确认已登录正确账号，再重新检查。",
                "重新检查",
                ConnectionDoctorActionKind.Refresh),
            TunnelPhase.WrongTailnet => (
                "第 1 层未通过",
                "Tailscale 0/1 · Linux 0/1 · SSH 0/1 · 网页 0/1",
                "当前登录的 tailnet 与配对时固定的 tailnet 不一致。",
                "Tailscale 账号或网络切换后，JK Bridge 拒绝连接到不同信任域。",
                "切回原 tailnet；若确实更换环境，请重新进行设备配置。",
                "打开设备配置",
                ConnectionDoctorActionKind.Setup),
            TunnelPhase.PeerUnavailable => (
                "第 2 层未通过",
                "Tailscale 1/1 · Linux 0/1 · SSH 0/1 · 网页 0/1",
                "Tailscale 正常，但找不到已配对的 Linux 节点。",
                "Linux 可能关机、Tailscale 离线，或节点名称已变化。",
                "确认 Linux 在线且 Tailscale 已连接；节点更换时重新配置。",
                "重新检查",
                ConnectionDoctorActionKind.Refresh),
            TunnelPhase.NeedsKeySetup or TunnelPhase.NeedsPairing => (
                "第 3 层未通过",
                "Tailscale 1/1 · Linux 1/1 · SSH 0/1 · 网页 0/1",
                "已到达 Linux，但当前 Windows 的 SSH 身份尚未获得精确端口授权。",
                "密钥未生成、未配对，或新服务端口尚未授权。",
                "打开设备配置完成一行配对。",
                "打开设备配置",
                ConnectionDoctorActionKind.Setup),
            TunnelPhase.HostKeyBlocked => (
                "第 3 层安全阻止",
                "Tailscale 1/1 · Linux 1/1 · SSH 0/1 · 网页 0/1",
                "Linux 返回的 SSH 主机密钥与已固定身份不一致。",
                "这可能是系统重装、主机变化或中间人风险，软件不会自动信任。",
                "先人工核对 Linux 主机密钥；确认设备确已更换后再重新配对。",
                "打开设备配置",
                ConnectionDoctorActionKind.Setup),
            TunnelPhase.PortConflict => (
                "第 3 层未通过",
                "Tailscale 1/1 · Linux 1/1 · SSH 0/1 · 网页 0/1",
                "Windows 本机端口已被其他程序占用，SSH 无法建立入口。",
                "某个服务选择的 Windows 访问端口发生冲突。",
                "打开服务页，采用建议端口并保存后重试连接。",
                "打开服务页",
                ConnectionDoctorActionKind.Services),
            TunnelPhase.Degraded => (
                "第 4 层部分未通过",
                $"Tailscale 1/1 · Linux 1/1 · SSH 1/1 · 网页 {availableServices}/{enabledServices}",
                "安全隧道已建立，但一个或多个 Linux 网页服务没有通过健康检查。",
                "对应进程可能未启动、端口填写错误，或 HTTPS/健康路径不兼容。",
                "打开服务页查看失败项；先确认 Linux 进程监听，再复核端口和健康检查。",
                "打开服务页",
                ConnectionDoctorActionKind.Services),
            TunnelPhase.Connected => (
                "四层全部通过",
                $"Tailscale 1/1 · Linux 1/1 · SSH 1/1 · 网页 {availableServices}/{enabledServices}",
                "从 Windows 到 Linux 网页服务的完整链路正常。",
                "未发现连接根因。",
                "可直接从概览或服务页打开网页；需要复查时重新运行连接医生。",
                "重新检查",
                ConnectionDoctorActionKind.Refresh),
            TunnelPhase.Starting or TunnelPhase.Preflight or TunnelPhase.Reconnecting => (
                "正在分层检查",
                "正在确认 Tailscale → Linux → SSH → 网页",
                "连接医生正在等待当前检查完成。",
                "尚未形成稳定结论。",
                "等待片刻；如长时间没有变化，可点击立即重试。",
                "立即重试",
                ConnectionDoctorActionKind.Retry),
            TunnelPhase.Faulted => (
                "连接流程异常",
                "已完成的层级见下方四张状态卡",
                "连接流程安全停止，未继续尝试不可信或不完整的链路。",
                DetailFor(snapshot),
                "先按四张状态卡定位；仍无法处理时导出安全诊断包。",
                "立即重试",
                ConnectionDoctorActionKind.Retry),
            _ => (
                "尚未连接",
                "等待开始 Tailscale → Linux → SSH → 网页 检查",
                "当前没有活动连接。",
                "连接尚未启动或已暂停。",
                "点击开始检查；如尚未配置设备，会自动转到设备配置。",
                "开始检查",
                ConnectionDoctorActionKind.Refresh),
        };
        ViewModel.ApplyConnectionDoctor(
            diagnosis.Item1,
            diagnosis.Item2,
            diagnosis.Item3,
            diagnosis.Item4,
            diagnosis.Item5,
            diagnosis.Item6,
            diagnosis.Item7);
    }

    private void ApplyConfigToViewModel()
    {
        ViewModel.LinuxHost = _config.Server.Host;
        ViewModel.LinuxUser = _config.Server.User;
        ViewModel.SshPort = _config.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ViewModel.IdentityFile = _config.Server.IdentityFile;
        ViewModel.PinnedHostKey = _config.Server.PinnedHostKey;
        ViewModel.AutoConnect = _config.Behavior.AutoConnect;
        ViewModel.AutoReconnect = _config.Behavior.AutoReconnect;
        ViewModel.CloseToTray = _config.Behavior.CloseToTray;
        ViewModel.StartWithWindows = _config.Behavior.StartWithWindows;
        ViewModel.ThemeIndex = _config.Behavior.Theme switch
        {
            ThemePreference.Light => 1,
            ThemePreference.Dark => 2,
            _ => 0,
        };
        ViewModel.UpdateNetworkModeIndex = _config.UpdateNetwork.Mode switch
        {
            UpdateNetworkMode.ManualProxy => 1,
            UpdateNetworkMode.Direct => 2,
            _ => 0,
        };
        ViewModel.UpdateManualProxyUrl = _config.UpdateNetwork.ManualProxyUrl;
        ViewModel.HealthCheckIntervalSeconds = _config.Behavior.HealthCheckIntervalSeconds is >= 5 and <= 300
            ? _config.Behavior.HealthCheckIntervalSeconds
            : 20;
        ViewModel.MarkThemeSaved(_config.Behavior.Theme);
        ViewModel.ApplySetupState(
            _config.SetupCompleted,
            _config.SetupCompleted
                ? $"{_config.Server.DisplayName} · {_config.Server.Host}"
                : "尚未配置 Linux 设备",
            _config.Server.IdentityFile);
        ViewModel.SetServices(_config.Services.Select(service => ToServiceUiState(
            service,
            new ServiceSnapshot(
                service.Id,
                service.Name,
                service.Enabled ? ServicePhase.WaitingForTunnel : ServicePhase.Disabled))));
        RefreshWindowsLocalPresentation();
    }

    private void RefreshDiagnostics() =>
        ViewModel.SetDiagnostics(_logger.Snapshot().Reverse().Take(100).Select(ToDiagnosticUiState));

    private ServiceUiState ToServiceUiState(ServiceDefinition service, ServiceSnapshot snapshot)
    {
        var available = snapshot.Phase == ServicePhase.Available;
        var warning = snapshot.Phase is ServicePhase.RemoteUnavailable or ServicePhase.ForwardDenied;
        var status = snapshot.Phase switch
        {
            ServicePhase.Disabled => "已停用",
            ServicePhase.WaitingForTunnel => "等待隧道",
            ServicePhase.Checking => "检查中",
            ServicePhase.Available => "可用",
            ServicePhase.RemoteUnavailable => "服务无响应",
            ServicePhase.ForwardDenied => "转发被拒绝",
            _ => "未知",
        };
        var detail = snapshot.Phase switch
        {
            ServicePhase.Available => FormatHealth(service, snapshot),
            ServicePhase.Checking => "正在检查本地网页入口…",
            ServicePhase.Disabled => "未加入 SSH 转发",
            ServicePhase.WaitingForTunnel => "等待安全隧道",
            _ => snapshot.Detail ?? "请检查 Linux 上的服务状态",
        };
        return new ServiceUiState(
            service.Id,
            ShortCode(service),
            service.Name,
            string.IsNullOrWhiteSpace(service.Description) ? "Linux 本地网页服务" : service.Description,
            service.OpenUrl,
            service.LocalPort,
            service.RemotePort,
            status,
            detail,
            service.Enabled,
            available,
            warning,
            snapshot.CanOpen,
            service.HealthKind,
            service.HealthPath,
            service.RestartPath,
            CachedRestartToken(service),
            service.RestartLocalPort,
            FormatLastRestart(service.Id));
    }

    /// <summary>
    /// Session-scoped audit text for the last confirmed restart of a service.
    /// </summary>
    private string FormatLastRestart(string serviceId)
    {
        lock (_lastSuccessfulRestarts)
        {
            if (!_lastSuccessfulRestarts.TryGetValue(serviceId, out var at))
            {
                return string.Empty;
            }

            var local = at.ToLocalTime();
            var today = DateTime.Today;
            return local.Date == today
                ? $"最近重启 今天 {local:HH:mm}"
                : local.Date == today.AddDays(-1)
                    ? $"最近重启 昨天 {local:HH:mm}"
                    : $"最近重启 {local:MM-dd HH:mm}";
        }
    }
    private static int FindAvailableLocalPort(int suggestedPort, ISet<int> usedPorts)
    {
        if (!usedPorts.Contains(suggestedPort))
        {
            return suggestedPort;
        }

        for (var candidate = suggestedPort + 1; candidate <= 65535; candidate++)
        {
            if (!usedPorts.Contains(candidate))
            {
                return candidate;
            }
        }

        for (var candidate = 1024; candidate < suggestedPort; candidate++)
        {
            if (!usedPorts.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("没有可用的 Windows 本地端口。");
    }

    private static string FormatHealth(ServiceDefinition service, ServiceSnapshot snapshot)
    {
        var status = snapshot.HttpStatus is int code
            ? $"HTTP {code}"
            : service.HealthKind == HealthCheckKind.TcpConnect
                ? "TCP 端口可达"
                : "可访问";
        return snapshot.Latency is TimeSpan latency
            ? $"{status} · {Math.Max(1, (int)Math.Round(latency.TotalMilliseconds))} ms"
            : status;
    }

    private static DiagnosticUiState ToDiagnosticUiState(DiagnosticEvent entry) => entry.Severity switch
    {
        DiagnosticSeverity.Error => new(entry.Timestamp.ToString("HH:mm:ss"), "错误", TranslateDiagnosticMessage(entry.Message), entry.Detail ?? entry.Category, "error"),
        DiagnosticSeverity.Warning => new(entry.Timestamp.ToString("HH:mm:ss"), "提醒", TranslateDiagnosticMessage(entry.Message), entry.Detail ?? entry.Category, "warning"),
        DiagnosticSeverity.Information => new(entry.Timestamp.ToString("HH:mm:ss"), "信息", TranslateDiagnosticMessage(entry.Message), entry.Detail ?? entry.Category, "info"),
        _ => new(entry.Timestamp.ToString("HH:mm:ss"), "调试", TranslateDiagnosticMessage(entry.Message), entry.Detail ?? entry.Category, "info"),
    };

    private static string StatusFor(TunnelPhase phase) => phase switch
    {
        TunnelPhase.Stopped => "未连接",
        TunnelPhase.Preflight => "正在检查",
        TunnelPhase.Starting => "正在连接",
        TunnelPhase.Connected => "已连接",
        TunnelPhase.Degraded => "部分可用",
        TunnelPhase.Reconnecting => "正在重连",
        TunnelPhase.Stopping => "正在断开",
        TunnelPhase.NeedsTailscale => "需要 Tailscale",
        TunnelPhase.WrongTailnet => "账号不一致",
        TunnelPhase.PeerUnavailable => "Linux 节点不可用",
        TunnelPhase.NeedsKeySetup => "需要 SSH 密钥",
        TunnelPhase.NeedsPairing => "需要重新配对",
        TunnelPhase.HostKeyBlocked => "主机密钥已阻止",
        TunnelPhase.PortConflict => "本地端口冲突",
        TunnelPhase.PausedForSleep => "等待系统唤醒",
        _ => "连接失败",
    };

    private static string SummaryFor(TunnelSnapshot snapshot, int available, int enabled) => snapshot.Phase switch
    {
        TunnelPhase.Connected => $"{available} 个服务全部可用",
        TunnelPhase.Degraded => $"SSH 正常，{available} / {enabled} 个网页可用",
        TunnelPhase.Stopped when snapshot.Message.Contains("Ready", StringComparison.OrdinalIgnoreCase) => "环境检查通过，可以连接",
        TunnelPhase.Stopped => "安全隧道当前已停止",
        TunnelPhase.Reconnecting when snapshot.RetryAt is DateTimeOffset at => $"将在 {at:HH:mm:ss} 自动重试",
        TunnelPhase.Preflight => "正在核对 Tailscale、密钥与端口",
        TunnelPhase.Starting => "正在建立一个多服务 SSH 隧道",
        _ => StatusFor(snapshot.Phase),
    };

    private static string DetailFor(TunnelSnapshot snapshot) => snapshot.Phase switch
    {
        TunnelPhase.NeedsTailscale => "请安装或启动 Tailscale，并登录与 Linux 主机相同的账号。",
        TunnelPhase.WrongTailnet => "当前 Windows 与 Linux 不在同一个 tailnet，请切换 Tailscale 登录账号。",
        TunnelPhase.PeerUnavailable => "没有精确找到在线的 Linux 节点；请确认 Linux 已开机且 Tailscale 在线。",
        TunnelPhase.NeedsKeySetup => "找不到专用 SSH 私钥，或 Linux 拒绝了该公钥。请运行首次配置脚本。",
        TunnelPhase.NeedsPairing when snapshot.Message.Contains("authorization receipt", StringComparison.OrdinalIgnoreCase) =>
            "旧版配置没有可核验的 Linux 端口授权记录。请重新配对一次建立基线。",
        TunnelPhase.NeedsPairing => "服务使用了尚未授权的 Linux 远端端口。请在 Linux 重新运行一次性配对助手。",
        TunnelPhase.HostKeyBlocked => "Linux 主机公钥与固定值不一致。请先人工核验，应用不会自动信任变更。",
        TunnelPhase.PortConflict => "至少一个 Windows 本地端口已被其他程序占用，请释放端口或修改服务配置。",
        TunnelPhase.Degraded => "SSH 隧道仍然在线，但至少一个 Linux 网页服务没有通过健康检查。",
        TunnelPhase.Reconnecting => "当前检查未通过，应用会按渐进等待自动重试；具体原因保留在诊断页。",
        TunnelPhase.Faulted => snapshot.Failure?.UserMessage ?? snapshot.Message,
        _ => snapshot.Message,
    };

    private static string ShortPeerName(string dnsName)
    {
        var separator = dnsName.IndexOf('.');
        return separator > 0 ? dnsName[..separator] : dnsName;
    }

    private static string TranslateDiagnosticMessage(string message)
    {
        if (message.Contains("configured Linux device was not found", StringComparison.OrdinalIgnoreCase))
        {
            return "当前 tailnet 中未找到配置的 Linux 设备";
        }

        if (message.Contains("SSH bridge started", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH 安全隧道已建立";
        }

        if (message.Contains("JK Bridge initialized", StringComparison.OrdinalIgnoreCase))
        {
            return "JK Bridge 已初始化";
        }

        return message;
    }

    private static string ShortCode(ServiceDefinition service)
    {
        if (service.Id == "overleaf") return "OL";
        if (service.Id == "gpu-dashboard") return "GPU";
        if (service.Id == "investment-news") return "NEWS";
        var letters = new string(service.Name.Where(char.IsLetterOrDigit).Take(3).ToArray());
        return letters.Length == 0 ? "WEB" : letters.ToUpperInvariant();
    }

    private static string ComputeFingerprint(string publicKey)
    {
        try
        {
            var parts = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hash = SHA256.HashData(Convert.FromBase64String(parts[1]));
            return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
        }
        catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException)
        {
            return "固定主机密钥格式无效";
        }
    }

    private async Task SetClipboardTextAsync(string value)
    {
        var window = _getWindow();
        var clipboard = window is null ? null : TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(value);
        }
    }

    private void ConfigureReleaseUpdateNetwork(UpdateNetworkSettings settings)
    {
        if (_releaseUpdateChecker is IReleaseUpdateNetworkConfigurator configurable)
        {
            configurable.ConfigureNetwork(settings);
        }
    }

    private static string DescribeUpdateNetwork(UpdateNetworkSettings settings) =>
        settings.Mode switch
        {
            UpdateNetworkMode.ManualProxy => $"本机代理 {settings.ManualProxyUrl} ",
            UpdateNetworkMode.Direct => "直连网络 ",
            _ => "Windows 系统代理 ",
        };

    private async Task<ServiceHealth> ProbeServiceAsync(ServiceDefinition service)
    {
        using var probe = new ServiceHealthProbe();
        var health = await probe.CheckAsync(service).ConfigureAwait(false);
        await SafeLogAsync(
                health.State == ServiceHealthState.Available
                    ? DiagnosticSeverity.Information
                    : DiagnosticSeverity.Warning,
                "Health",
                $"Manual health check for {service.Name}: {health.State}.",
                health.Detail)
            .ConfigureAwait(false);

        var runtimeService = _runtime.Snapshot.Services.FirstOrDefault(item => item.ServiceId == service.Id);
        var presentationSnapshot = runtimeService?.CanOpen == true
            ? ServiceSnapshot.FromHealth(service.Name, health)
            : runtimeService ?? new ServiceSnapshot(
                service.Id,
                service.Name,
                ServicePhase.WaitingForTunnel,
                Detail: "The SSH forward is not active.");

        RunOnUi(() =>
        {
            var current = ViewModel.Services.FirstOrDefault(item => item.Id == service.Id);
            current?.ApplyRuntimeState(ToServiceUiState(service, presentationSnapshot));
        });
        return health;
    }

    private async Task RepairStartupRegistrationAsync(CancellationToken cancellationToken)
    {
        if (!_startupRegistration.IsSupported)
        {
            return;
        }

        try
        {
            var state = _startupRegistration.GetState();
            if (_config.Behavior.StartWithWindows && !state.MatchesCurrentExecutable)
            {
                _startupRegistration.EnableCurrentProcess("--background");
                await SafeLogAsync(
                        DiagnosticSeverity.Information,
                        "Startup",
                        "Windows startup registration was repaired for the current executable path.")
                    .ConfigureAwait(false);
            }
            else if (!_config.Behavior.StartWithWindows && state.IsEnabled)
            {
                _startupRegistration.Disable();
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Startup",
                    "Windows startup registration could not be synchronized.",
                    exception.Message)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RunActionAsync(string action, Func<Task> operation)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer user command, sleep transition, or shutdown superseded this operation.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await SafeLogAsync(DiagnosticSeverity.Error, "Action", $"{action} failed.", exception.Message)
                .ConfigureAwait(false);
            ShowControllerError($"{action}失败", exception.Message);
        }
    }

    private async ValueTask SafeLogAsync(
        DiagnosticSeverity severity,
        string category,
        string message,
        string? detail = null)
    {
        try
        {
            await _logger.WriteAsync(severity, category, message, detail).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Logging is best-effort and never blocks a user action.
        }
    }

    private void ShowControllerError(string title, string detail) =>
        RunOnUi(() => ViewModel.ShowNotice(title, detail));

    private static void ApplyTheme(ThemePreference theme)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void RunOnUi(Action action) => _dispatchUi(action);

    private static void DispatchOnAvaloniaUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
