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
using JKBridge.Core.Agent;
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
    private sealed record PendingAgentRelayPlan(
        AgentRelayPlanRequest? InstallRequest,
        string Slug,
        AgentRelayPlanResponse Plan,
        AgentRelayProbeResponse? Probe);

    private readonly ConfigStore _configStore;
    private readonly RollingFileLogger _logger;
    private readonly IBridgeRuntime _runtime;
    private readonly IDevicePairingService _pairingService;
    private readonly IAgentEnrollmentService _agentEnrollmentService;
    private readonly IAgentCredentialStore _agentCredentialStore;
    private readonly IAgentManagementService _agentManagementService;
    private readonly IAgentDiscoveryService _agentDiscoveryService;
    private readonly ServiceProfileTransferService _serviceProfileTransferService;
    private readonly DiagnosticBundleService _diagnosticBundleService;
    private readonly IReleaseUpdateChecker _releaseUpdateChecker;
    private readonly bool _ownsReleaseUpdateChecker;
    private readonly Action<string> _installerLauncher;
    private readonly string _updateDirectory;
    private readonly StartupRegistration _startupRegistration;
    private readonly Func<Window?> _getWindow;
    private readonly Action<Action> _dispatchUi;
    private readonly string _logDirectory;
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private AppConfig _config = AppConfig.CreateDefault();
    private ServiceProfileImportPreview? _serviceProfileImportPreview;
    private PendingAgentRelayPlan? _pendingAgentRelayPlan;
    private AgentCatalogUpdatePreview? _pendingAgentCatalogUpdate;
    private ReleaseInstallerAsset? _pendingReleaseInstaller;
    private DateTimeOffset _agentEnrollmentCodeExpiresAt;
    private int _resumeAfterSleep;
    private CancellationTokenSource? _pairingCancellation;
    private CancellationTokenSource? _agentEnrollmentCancellation;
    private CancellationTokenSource? _agentManagementCancellation;
    private CancellationTokenSource? _agentDiscoveryCancellation;
    private CancellationTokenSource? _serviceProfileCancellation;
    private CancellationTokenSource? _agentEnrollmentCodeExpiryCancellation;
    private CancellationTokenSource? _diagnosticBundleCancellation;
    private CancellationTokenSource? _releaseUpdateCancellation;
    private int _pairingActive;
    private int _agentEnrollmentActive;
    private int _agentManagementActive;
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
        IAgentEnrollmentService? agentEnrollmentService = null,
        IAgentCredentialStore? agentCredentialStore = null,
        IAgentManagementService? agentManagementService = null,
        ServiceProfileTransferService? serviceProfileTransferService = null,
        IAgentDiscoveryService? agentDiscoveryService = null,
        Action<Action>? dispatchUi = null,
        DiagnosticBundleService? diagnosticBundleService = null,
        IReleaseUpdateChecker? releaseUpdateChecker = null,
        Action<string>? installerLauncher = null,
        IWindowsLocalServiceDiscovery? windowsLocalDiscovery = null)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _pairingService = pairingService ?? throw new ArgumentNullException(nameof(pairingService));
        _agentCredentialStore = agentCredentialStore ?? new WindowsAgentCredentialStore();
        var agentProtocolClientFactory = new AgentProtocolClientFactory();
        _agentEnrollmentService = agentEnrollmentService ?? new AgentEnrollmentService(
            agentProtocolClientFactory);
        _agentManagementService = agentManagementService ?? new AgentManagementService(
            agentProtocolClientFactory,
            _agentCredentialStore);
        _agentDiscoveryService = agentDiscoveryService ?? new AgentDiscoveryService(
            agentProtocolClientFactory,
            new TailscaleCliAgentDiscoveryStatusProvider());
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
        _updateDirectory = Path.Combine(
            Path.GetDirectoryName(_logDirectory) ?? _logDirectory,
            "Updates");
        ViewModel = new MainViewModel(this);

        _runtime.SnapshotChanged += RuntimeOnSnapshotChanged;
        _logger.EventWritten += LoggerOnEventWritten;
    }

    public MainViewModel ViewModel { get; }

    public AppConfig CurrentConfig => _config;

    public bool CloseToTray => _config.Behavior.CloseToTray;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await _configStore.LoadWithResultAsync(cancellationToken).ConfigureAwait(false);
            _config = loaded.Config;
            if (loaded.Source == ConfigLoadSource.Defaults || loaded.WasMigrated)
            {
                await _configStore.SaveAsync(_config, cancellationToken).ConfigureAwait(false);
            }

            await RepairStartupRegistrationAsync(cancellationToken).ConfigureAwait(false);

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

    public async Task EnrollWithAgentAsync(AgentEnrollmentDraft enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(ref _setupOperationActive, 1) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _agentEnrollmentActive, 1);

        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var previous = Interlocked.Exchange(ref _agentEnrollmentCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        RunOnUi(() =>
        {
            ViewModel.AgentRecoveryCode = string.Empty;
            ViewModel.BeginAgentEnrollment();
        });
        var configurationSaved = false;
        string? credentialReference = null;

        try
        {
            await _runtime.StopAsync(cancellation.Token).ConfigureAwait(false);
            var progress = new Progress<AgentEnrollmentProgress>(item =>
                RunOnUi(() => ViewModel.ApplyAgentEnrollmentProgress(item.Stage, item.Detail)));
            var result = await _agentEnrollmentService.EnrollAsync(
                    new AgentEnrollmentRequest(
                        enrollment.Endpoint,
                        enrollment.RecoveryCode,
                        enrollment.DeviceLabel,
                        enrollment.IdentityFile),
                    progress,
                    cancellation.Token)
                .ConfigureAwait(false);

            credentialReference = await _agentCredentialStore.SaveAsync(
                    result.Endpoint,
                    result.DeviceId,
                    result.DeviceCredential,
                    cancellation.Token)
                .ConfigureAwait(false);

            var saved = await SaveConfigAsync(
                    current => current with
                    {
                        SetupCompleted = true,
                        ExpectedTailnetDnsSuffix = result.TailnetDnsSuffix,
                        Server = current.Server with
                        {
                            DisplayName = result.ServerDisplayName,
                            Host = result.ServerHost,
                            User = result.SshUser,
                            Port = result.SshPort,
                            IdentityFile = result.PortableIdentityFile,
                            HostKeyAlias = "jk-bridge-linux",
                            PinnedHostKey = result.HostPublicKey,
                        },
                        Authorization = new ForwardAuthorizationGrant
                        {
                            IsKnown = true,
                            ClientKeyFingerprint = result.ClientFingerprint,
                            RemotePorts = result.PermitPorts.Distinct().Order().ToList(),
                        },
                        Services = MergeAgentCatalogServices(current.Services, result.Services),
                        Agent = new AgentRegistration
                        {
                            IsConfigured = true,
                            Endpoint = result.Endpoint,
                            DeviceId = result.DeviceId,
                            DeviceLabel = result.DeviceLabel,
                            CredentialReference = credentialReference,
                            CatalogRevision = result.CatalogRevision,
                            TailscaleNodeId = result.TailscaleNodeId,
                        },
                        Behavior = current.Behavior with { AutoConnect = true },
                    },
                    "Agent 设备登记和服务清单已安全保存。")
                .ConfigureAwait(false);
            if (!saved)
            {
                throw new AgentEnrollmentException(
                    "Linux 已登记此设备，但 Windows 配置保存失败；请在 Agent 设备列表中撤销本次登记后重试。");
            }
            configurationSaved = true;

            RunOnUi(() => ViewModel.CompleteAgentEnrollment(
                $"{result.ServerDisplayName} · {result.ServerHost} · {result.DeviceLabel}"));
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Agent",
                    "Linux Agent enrollment completed.",
                    $"Device ID: {result.DeviceId}; client fingerprint: {result.ClientFingerprint}; catalog: {result.CatalogRevision}")
                .ConfigureAwait(false);
            await _runtime.StartAsync(_config, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.FailAgentEnrollment("Agent 登记已取消或超时；可重试或改用一次性配对。"));
        }
        catch (Exception exception) when (exception is AgentEnrollmentException or IOException or UnauthorizedAccessException)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Agent",
                    "Linux Agent enrollment did not complete.",
                    exception.GetType().Name)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentEnrollment(exception.Message));
        }
        catch (Exception exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    configurationSaved
                        ? "Agent enrollment was saved, but the initial connection failed."
                        : "Linux Agent enrollment failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() =>
            {
                if (configurationSaved)
                {
                    ViewModel.ShowNotice(
                        "设备已经登记",
                        "安全配置已保存，但首次连接未能启动。请打开诊断页后重试连接。");
                }
                else
                {
                    ViewModel.FailAgentEnrollment("Agent 登记未能安全完成；可重试或改用一次性配对。");
                }
            });
        }
        finally
        {
            if (!configurationSaved && credentialReference is not null)
            {
                try
                {
                    await _agentCredentialStore.DeleteAsync(credentialReference).ConfigureAwait(false);
                }
                catch
                {
                    // Do not hide the original enrollment failure. The deterministic reference can
                    // be overwritten by a later enrollment of the same Agent device identity.
                }
            }

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _agentEnrollmentCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }

            Interlocked.Exchange(ref _agentEnrollmentActive, 0);
            Interlocked.Exchange(ref _setupOperationActive, 0);
        }
    }

    public Task SyncAgentCatalogAsync() => RunAgentStateActionAsync(saveCatalog: true);

    public Task RefreshAgentDevicesAsync() => RunAgentStateActionAsync(saveCatalog: false);

    public async Task DiscoverAgentsAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(ref _setupOperationActive, 1) != 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var previous = Interlocked.Exchange(ref _agentDiscoveryCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        RunOnUi(ViewModel.BeginAgentDiscovery);
        try
        {
            var result = await _agentDiscoveryService.DiscoverAsync(cancellation.Token)
                .ConfigureAwait(false);
            var candidates = result.Candidates.Select(candidate =>
                new AgentDiscoveryCandidateUiState(
                    candidate.Endpoint,
                    candidate.DisplayName,
                    candidate.ServerHost,
                    candidate.AgentVersion,
                    candidate.EnrollmentAvailable,
                    candidate.TailscaleNodeId)).ToArray();
            var feedback = result.Outcome switch
            {
                AgentDiscoveryOutcome.None =>
                    "没有发现可核验的 Agent。可手动输入地址，或确认 Linux Agent 正在 Tailscale 地址的 45871 端口运行。",
                AgentDiscoveryOutcome.Single =>
                    "发现 1 个已核验候选。请核对净化后的名称与 MagicDNS 身份，再点击“使用所选地址”；不会自动登记。",
                _ =>
                    $"发现 {candidates.Length} 个已核验候选。必须明确选择一个，JK Bridge 不会自动挑选或登记。",
            };
            RunOnUi(() => ViewModel.ApplyAgentDiscovery(candidates, feedback));
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Agent",
                    "Explicit read-only Agent discovery completed.",
                    $"Sanitized candidates: {candidates.Length}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.FailAgentDiscovery("Agent 发现已取消或超时；没有登记或修改任何设备。"));
        }
        catch (AgentDiscoveryException exception)
        {
            RunOnUi(() => ViewModel.FailAgentDiscovery(exception.Message));
        }
        catch (Exception exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Agent",
                    "Agent discovery failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentDiscovery(
                "Agent 发现未能安全完成；可重试或手动输入精确地址。"));
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _agentDiscoveryCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
            Interlocked.Exchange(ref _setupOperationActive, 0);
        }
    }

    public async Task RebindAgentEndpointAsync(
        AgentDiscoveryCandidateUiState candidate,
        bool confirmed)
    {
        if (!confirmed)
        {
            RunOnUi(() => ViewModel.AgentDiscoveryFeedback =
                "重新绑定前必须明确勾选确认；发现结果本身不会改变已登记 Agent。");
            return;
        }
        if (candidate is null ||
            !string.Equals(
                candidate.TailscaleNodeId,
                _config.Agent.TailscaleNodeId,
                StringComparison.Ordinal))
        {
            RunOnUi(() => ViewModel.AgentDiscoveryFeedback =
                "所选候选不是当前配置记录的同一 Tailscale 稳定节点；拒绝重新绑定。不同节点必须重新登记。");
            return;
        }
        if (!TryBeginAgentManagement("正在重新核验同一 Agent 的新 Tailscale 地址…", out var cancellation))
        {
            return;
        }

        try
        {
            var request = CreateAgentManagementRequest(_config);
            var verification = await _agentManagementService.VerifyEndpointRebindAsync(
                    request,
                    candidate.Endpoint,
                    cancellation.Token)
                .ConfigureAwait(false);
            var saved = await SaveConfigAsync(
                    current =>
                    {
                        if (current.Agent != request.Registration || current.Server != request.Server)
                        {
                            throw new AgentEnrollmentException(
                                "重新绑定期间 Agent 或服务器配置已经改变，请重新发现。");
                        }
                        return current with
                        {
                            Agent = current.Agent with
                            {
                                Endpoint = verification.Endpoint,
                                CatalogRevision = verification.CatalogRevision,
                            },
                        };
                    },
                    "Agent 管理地址已重新绑定到同一 Tailscale 稳定节点。")
                .ConfigureAwait(false);
            if (!saved)
            {
                throw new AgentEnrollmentException(
                    "新地址已通过身份核验，但 Windows 未能保存；原地址和凭据仍保留。");
            }

            RunOnUi(() =>
            {
                ViewModel.AgentEndpoint = verification.Endpoint;
                ViewModel.AgentEndpointRebindConfirmed = false;
                ViewModel.AgentDiscoveryFeedback =
                    "已重新绑定同一 Agent 的地址。DeviceId、设备凭据、SSH 密钥和服务器身份均保持不变；没有重新登记。";
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Agent",
                    "Agent endpoint was explicitly rebound after stable identity verification.",
                    "The device ID and credential reference were preserved.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.AgentDiscoveryFeedback =
                "地址重新绑定已取消或超时；原配置保持不变。");
        }
        catch (AgentEnrollmentException exception)
        {
            RunOnUi(() => ViewModel.AgentDiscoveryFeedback = exception.Message);
        }
        catch (Exception exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    "Agent endpoint rebind failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.AgentDiscoveryFeedback =
                "地址重新绑定未能安全完成；原配置保持不变。");
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
    }

    public void InvalidateTransientSetupState()
    {
        _pendingAgentRelayPlan = null;
        _pendingAgentCatalogUpdate = null;
        _serviceProfileImportPreview = null;
        _agentEnrollmentCodeExpiresAt = default;
        var expiry = Interlocked.Exchange(ref _agentEnrollmentCodeExpiryCancellation, null);
        expiry?.Cancel();
        expiry?.Dispose();
    }

    public async Task RevokeAgentDeviceAsync(string deviceId, bool confirmed)
    {
        if (!confirmed)
        {
            RunOnUi(() => ViewModel.AgentManagementFeedback =
                "撤销前必须勾选确认。此操作会删除 Linux 上该设备的 SSH 授权，且不能自动恢复。");
            return;
        }
        if (!TryBeginAgentManagement("正在核验管理员身份并撤销所选设备…", out var cancellation))
        {
            return;
        }

        try
        {
            var request = CreateAgentManagementRequest(_config);
            var receipt = await _agentManagementService.RevokeDeviceAsync(
                    request,
                    deviceId,
                    cancellation.Token)
                .ConfigureAwait(false);
            var state = await _agentManagementService.GetStateAsync(request, cancellation.Token)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.ApplyAgentDevices(
                ToAgentDeviceUiStates(state.Devices, request.Registration.DeviceId),
                state.CurrentDeviceRole,
                $"设备 {receipt.DeviceId} 已撤销。Linux 已删除其后续 SSH 授权；该设备已有的 SSH 会话需要在相应设备上断开。"));
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Agent",
                    "Agent device was revoked by an explicit Windows confirmation.",
                    $"Device ID: {receipt.DeviceId}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.FailAgentManagement("设备撤销已取消或超时；请刷新列表确认最终状态。"));
        }
        catch (AgentEnrollmentException exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Agent",
                    "Agent device revocation did not complete.",
                    exception.GetType().Name)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentManagement(exception.Message));
        }
        catch (Exception exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    "Agent device revocation failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentManagement(
                "撤销未能安全完成；本地配置没有改变，请刷新设备列表确认状态。"));
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
    }

    public async Task RenameAgentDeviceAsync(string deviceId, string deviceLabel)
    {
        if (!TryBeginAgentManagement("正在核验管理员身份并重命名所选设备…", out var cancellation))
        {
            return;
        }

        try
        {
            var request = CreateAgentManagementRequest(_config);
            var receipt = await _agentManagementService.RenameDeviceAsync(
                    request,
                    deviceId,
                    deviceLabel,
                    cancellation.Token)
                .ConfigureAwait(false);
            var state = await _agentManagementService.GetStateAsync(request, cancellation.Token)
                .ConfigureAwait(false);
            if (string.Equals(deviceId, request.Registration.DeviceId, StringComparison.Ordinal))
            {
                var saved = await SaveConfigAsync(
                        current =>
                        {
                            if (current.Agent != request.Registration)
                            {
                                throw new AgentEnrollmentException(
                                    "重命名期间 Agent 配置已经改变，请刷新设备列表。");
                            }
                            return current with
                            {
                                Agent = current.Agent with
                                {
                                    DeviceLabel = receipt.Device.DeviceLabel,
                                },
                            };
                        },
                        "当前设备名称已与 Agent 同步。")
                    .ConfigureAwait(false);
                if (!saved)
                {
                    throw new AgentEnrollmentException(
                        "Agent 已重命名当前设备，但 Windows 未能保存新名称；请刷新后重试。");
                }
            }

            RunOnUi(() => ViewModel.ApplyAgentDevices(
                ToAgentDeviceUiStates(state.Devices, request.Registration.DeviceId),
                state.CurrentDeviceRole,
                $"设备已从“{receipt.PreviousDeviceLabel}”重命名为“{receipt.Device.DeviceLabel}”。权限与端口授权没有改变。"));
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Agent",
                    "An Agent device was explicitly renamed by an administrator.",
                    $"Device ID: {receipt.Device.DeviceId}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.FailAgentManagement("设备重命名已取消或超时；请刷新列表确认状态。"));
        }
        catch (AgentEnrollmentException exception)
        {
            RunOnUi(() => ViewModel.FailAgentManagement(exception.Message));
        }
        catch (Exception exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    "Agent device rename failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentManagement(
                "设备重命名未能安全完成；请刷新设备列表后重试。"));
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
    }

    public async Task GenerateAgentEnrollmentCodeAsync()
    {
        if (!TryBeginAgentManagement("正在核验管理员身份并生成一次性设备添加码…", out var cancellation))
        {
            return;
        }

        var priorExpiry = Interlocked.Exchange(ref _agentEnrollmentCodeExpiryCancellation, null);
        priorExpiry?.Cancel();
        priorExpiry?.Dispose();
        _agentEnrollmentCodeExpiresAt = default;
        RunOnUi(() => ViewModel.ClearAgentEnrollmentCode(
            "正在生成；原添加码已从此界面清除，新添加码不会写入配置或日志。"));
        RunOnUi(() => ViewModel.AgentEnrollmentCodeFeedback = "正在生成；添加码不会写入配置或日志。");
        try
        {
            var response = await _agentManagementService.CreateEnrollmentCodeAsync(
                    CreateAgentManagementRequest(_config),
                    ttlSeconds: 600,
                    cancellation.Token)
                .ConfigureAwait(false);
            var code = response.EnrollmentCode.Export();
            _agentEnrollmentCodeExpiresAt = response.ExpiresAt;
            ScheduleAgentEnrollmentCodeExpiry(response.ExpiresAt);
            RunOnUi(() => ViewModel.ShowAgentEnrollmentCode(
                code,
                response.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")));
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Agent",
                    "A transient one-use device enrollment code was created by an administrator.",
                    $"Expires at: {response.ExpiresAt:O}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.SetAgentEnrollmentCodeFeedback("生成添加码已取消或超时。"));
        }
        catch (AgentEnrollmentException exception)
        {
            RunOnUi(() => ViewModel.SetAgentEnrollmentCodeFeedback(exception.Message));
        }
        catch (Exception exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    "Enrollment-code creation failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.SetAgentEnrollmentCodeFeedback(
                "添加码未能安全生成；没有凭据被保存。"));
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
    }

    public async Task CopyAgentEnrollmentCodeAsync(string enrollmentCode)
    {
        if (!ViewModel.HasAgentEnrollmentCode ||
            DateTimeOffset.UtcNow >= _agentEnrollmentCodeExpiresAt ||
            !string.Equals(ViewModel.AgentEnrollmentCode, enrollmentCode, StringComparison.Ordinal))
        {
            RunOnUi(() => ViewModel.ClearAgentEnrollmentCode("添加码已过期或不再可用，请重新生成。"));
            return;
        }

        await SetClipboardTextAsync(enrollmentCode);
        RunOnUi(() => ViewModel.AgentEnrollmentCodeFeedback =
            "已复制。请立即粘贴到目标 Windows；系统剪贴板不会自动清除，请在使用后覆盖。");
    }

    public async Task PlanAgentRelayAsync(AgentRelayDraft draft)
    {
        AgentRelayPlanRequest relay;
        try
        {
            relay = CreateAgentRelayRequest(draft);
        }
        catch (AgentEnrollmentException exception)
        {
            _pendingAgentRelayPlan = null;
            RunOnUi(() => ViewModel.FailAgentRelayOperation(exception.Message, preservePreview: false));
            return;
        }

        if (!TryBeginAgentManagement("正在生成中继计划并从 Linux 探测目标…", out var cancellation))
        {
            return;
        }

        _pendingAgentRelayPlan = null;
        _pendingAgentCatalogUpdate = null;
        RunOnUi(() => ViewModel.BeginAgentRelayOperation(
            "规划与探测是只读操作；Linux 配置和设备授权尚未改变。"));
        try
        {
            var state = await _agentManagementService.PlanRelayAsync(
                    CreateAgentManagementRequest(_config),
                    relay,
                    cancellation.Token)
                .ConfigureAwait(false);
            _pendingAgentRelayPlan = new PendingAgentRelayPlan(
                state.Request,
                state.Request.Spec.Slug,
                state.Plan,
                state.Probe);
            var canApply = IsAgentRelayProbeCompatible(state.Plan.RelayPlan, state.Probe);
            RunOnUi(() => ViewModel.ApplyAgentRelayPreview(
                FormatAgentRelayPreview(_pendingAgentRelayPlan),
                canApply));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _pendingAgentRelayPlan = null;
            RunOnUi(() => ViewModel.FailAgentRelayOperation(
                "中继规划或探测已取消或超时；Linux 尚未改变。",
                preservePreview: false));
        }
        catch (AgentEnrollmentException exception)
        {
            _pendingAgentRelayPlan = null;
            RunOnUi(() => ViewModel.FailAgentRelayOperation(exception.Message, preservePreview: false));
        }
        catch (Exception exception)
        {
            _pendingAgentRelayPlan = null;
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    "Relay planning failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentRelayOperation(
                "中继规划未能安全完成；Linux 尚未改变。",
                preservePreview: false));
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
    }

    public async Task PlanAgentRelayDeletionAsync(string slug)
    {
        slug = slug?.Trim() ?? string.Empty;
        if (!IsValidAgentRelaySlug(slug))
        {
            _pendingAgentRelayPlan = null;
            RunOnUi(() => ViewModel.FailAgentRelayOperation(
                "删除计划需要有效的服务 ID：小写字母开头，只能包含小写字母、数字和连字符，最长 48 个字符。",
                preservePreview: false));
            return;
        }
        if (!TryBeginAgentManagement("正在生成只读中继删除计划…", out var cancellation))
        {
            return;
        }

        _pendingAgentRelayPlan = null;
        RunOnUi(() => ViewModel.BeginAgentRelayOperation(
            "删除计划是只读操作；中继、服务清单和授权尚未改变。"));
        try
        {
            var plan = await _agentManagementService.PlanRelayDeletionAsync(
                    CreateAgentManagementRequest(_config),
                    slug,
                    cancellation.Token)
                .ConfigureAwait(false);
            _pendingAgentRelayPlan = new PendingAgentRelayPlan(null, slug, plan, null);
            RunOnUi(() => ViewModel.ApplyAgentRelayPreview(
                FormatAgentRelayPreview(_pendingAgentRelayPlan),
                plan.RelayPlan.CanApply));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _pendingAgentRelayPlan = null;
            RunOnUi(() => ViewModel.FailAgentRelayOperation(
                "删除规划已取消或超时；Linux 尚未改变。",
                preservePreview: false));
        }
        catch (AgentEnrollmentException exception)
        {
            _pendingAgentRelayPlan = null;
            RunOnUi(() => ViewModel.FailAgentRelayOperation(exception.Message, preservePreview: false));
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
    }

    public async Task ApplyAgentRelayPlanAsync(bool confirmed)
    {
        var pending = _pendingAgentRelayPlan;
        if (!confirmed || pending is null || !ViewModel.HasAgentRelayPreview)
        {
            RunOnUi(() => ViewModel.FailAgentRelayOperation(
                "必须先生成并核对当前只读计划，再明确勾选确认。",
                preservePreview: pending is not null));
            return;
        }
        if (!pending.Plan.RelayPlan.CanApply ||
            pending.Probe is not null &&
            !IsAgentRelayProbeCompatible(pending.Plan.RelayPlan, pending.Probe))
        {
            RunOnUi(() => ViewModel.FailAgentRelayOperation(
                "当前计划或目标探测不允许安全应用；请修正输入并重新规划。",
                preservePreview: true));
            return;
        }
        if (!TryBeginAgentManagement("正在按已确认计划更新中继、服务清单和精确授权…", out var cancellation))
        {
            return;
        }

        RunOnUi(() => ViewModel.BeginAgentRelayOperation(
            "正在提交与当前 Linux 状态绑定的计划；若状态已变化，Agent 会拒绝并要求重新规划。"));
        try
        {
            var request = CreateAgentManagementRequest(_config);
            AgentRelayApplyResponse receipt;
            if (pending.InstallRequest is not null)
            {
                receipt = await _agentManagementService.ApplyRelayAsync(
                        request,
                        pending.InstallRequest,
                        pending.Plan.PlanToken,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                receipt = await _agentManagementService.DeleteRelayAsync(
                        request,
                        pending.Slug,
                        pending.Plan.PlanToken,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            var state = await _agentManagementService.GetStateAsync(request, cancellation.Token)
                .ConfigureAwait(false);
            if (!string.Equals(
                    state.CatalogRevision,
                    receipt.Plan.NextServicesRevision,
                    StringComparison.Ordinal))
            {
                throw new AgentEnrollmentException(
                    "中继已提交，但刷新到的服务版本与提交回执不一致；请立即再次同步。");
            }
            if (!await PersistAgentStateAsync(request, state).ConfigureAwait(false))
            {
                throw new AgentEnrollmentException(
                    "Linux 中继已更新，但 Windows 未能保存刷新后的服务清单；请立即点击“同步服务清单”。");
            }

            _pendingAgentRelayPlan = null;
            RunOnUi(() =>
            {
                ViewModel.ApplyAgentDevices(
                    ToAgentDeviceUiStates(state.Devices, request.Registration.DeviceId),
                    state.CurrentDeviceRole,
                    $"Agent 服务清单已刷新至版本 {state.CatalogRevision}。");
                ViewModel.CompleteAgentRelayOperation(
                    receipt.Applied
                        ? $"中继计划已应用，并已刷新 Windows 服务清单。{(receipt.BackupCreated ? " Linux 已创建备份。" : string.Empty)}"
                        : "计划已收敛，无需更改中继文件；Windows 服务清单已刷新。");
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Agent",
                    "An explicitly confirmed relay plan was applied and the catalog was refreshed.",
                    $"Operation: {receipt.Plan.RelayPlan.Operation}; action: {receipt.Plan.RelayPlan.Action}; revision: {state.CatalogRevision}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _pendingAgentRelayPlan = null;
            RunOnUi(() => ViewModel.FailAgentRelayOperation(
                "应用操作已取消或超时，最终状态可能已经改变；请重新同步服务并生成新计划。",
                preservePreview: false));
        }
        catch (AgentEnrollmentException exception)
        {
            _pendingAgentRelayPlan = null;
            RunOnUi(() => ViewModel.FailAgentRelayOperation(exception.Message, preservePreview: false));
        }
        catch (Exception exception)
        {
            _pendingAgentRelayPlan = null;
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    "Relay apply failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentRelayOperation(
                "中继应用未能确认完成；请先同步服务清单和重新规划，不要重复使用旧计划。",
                preservePreview: false));
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
    }

    public async Task PreviewAgentCatalogUpdateAsync(bool applyDefaultsToAllDevices)
    {
        IReadOnlyList<AgentServiceDefinition> proposed;
        try
        {
            proposed = CreateAgentServiceCatalog(_config.Services);
        }
        catch (AgentEnrollmentException exception)
        {
            _pendingAgentCatalogUpdate = null;
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(exception.Message, preservePreview: false));
            return;
        }
        if (!TryBeginAgentManagement("正在只读比较本机与 Linux Agent 服务清单…", out var cancellation))
        {
            return;
        }

        _pendingAgentCatalogUpdate = null;
        RunOnUi(() => ViewModel.BeginAgentCatalogUpdate(
            "正在读取当前 Linux 清单和设备授权；此步骤不会修改 Linux 或本机配置。"));
        try
        {
            var preview = await _agentManagementService.PreviewServiceCatalogUpdateAsync(
                    CreateAgentManagementRequest(_config),
                    proposed,
                    applyDefaultsToAllDevices,
                    cancellation.Token)
                .ConfigureAwait(false);
            _pendingAgentCatalogUpdate = preview;
            RunOnUi(() => ViewModel.ApplyAgentCatalogUpdatePreview(
                FormatAgentCatalogUpdatePreview(preview),
                preview.CanApply,
                preview.CanApply
                    ? "只读差异已生成。核对服务、端口和受影响设备后，明确勾选确认才会提交。"
                    : preview.BlockingReason));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _pendingAgentCatalogUpdate = null;
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(
                "服务清单预览已取消或超时；没有修改 Linux 或本机配置。",
                preservePreview: false));
        }
        catch (AgentEnrollmentException exception)
        {
            _pendingAgentCatalogUpdate = null;
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(exception.Message, preservePreview: false));
        }
        catch (Exception exception)
        {
            _pendingAgentCatalogUpdate = null;
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    "Catalog update preview failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(
                "服务清单预览未能安全完成；没有修改 Linux 或本机配置。",
                preservePreview: false));
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
    }

    public async Task ApplyAgentCatalogUpdateAsync(bool confirmed)
    {
        var preview = _pendingAgentCatalogUpdate;
        if (!confirmed || preview is null || !ViewModel.HasAgentCatalogUpdatePreview)
        {
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(
                "必须先生成并核对当前服务清单差异，再明确勾选确认。",
                preservePreview: preview is not null));
            return;
        }
        if (!preview.CanApply)
        {
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(
                preview.BlockingReason,
                preservePreview: true));
            return;
        }

        IReadOnlyList<AgentServiceDefinition> currentProposal;
        try
        {
            currentProposal = CreateAgentServiceCatalog(_config.Services);
        }
        catch (AgentEnrollmentException exception)
        {
            _pendingAgentCatalogUpdate = null;
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(exception.Message, preservePreview: false));
            return;
        }
        if (ViewModel.AgentCatalogApplyDefaultsToAllDevices != preview.ApplyDefaultsToAllDevices ||
            !currentProposal.SequenceEqual(preview.ProposedServices))
        {
            _pendingAgentCatalogUpdate = null;
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(
                "本机服务或授权范围在预览后已改变，请重新生成差异。",
                preservePreview: false));
            return;
        }
        if (!TryBeginAgentManagement("正在提交已确认的 Linux Agent 服务清单…", out var cancellation))
        {
            return;
        }

        RunOnUi(() => ViewModel.BeginAgentCatalogUpdate(
            "正在用预览时的版本提交；若 Linux 状态已经变化，Agent 会拒绝并要求重新预览。"));
        try
        {
            var request = CreateAgentManagementRequest(_config);
            var receipt = await _agentManagementService.ApplyServiceCatalogUpdateAsync(
                    request,
                    preview,
                    cancellation.Token)
                .ConfigureAwait(false);
            var state = await _agentManagementService.GetStateAsync(request, cancellation.Token)
                .ConfigureAwait(false);
            if (!string.Equals(receipt.ServicesRevision, state.CatalogRevision, StringComparison.Ordinal))
            {
                throw new AgentEnrollmentException(
                    "Linux 已接受服务清单，但刷新回执版本不一致；请立即再次同步。");
            }
            if (!await PersistAgentStateAsync(request, state).ConfigureAwait(false))
            {
                throw new AgentEnrollmentException(
                    "Linux 已接受服务清单，但 Windows 未能保存刷新结果；请立即点击“同步服务清单”。");
            }

            _pendingAgentCatalogUpdate = null;
            RunOnUi(() =>
            {
                ViewModel.ApplyAgentDevices(
                    ToAgentDeviceUiStates(state.Devices, request.Registration.DeviceId),
                    state.CurrentDeviceRole,
                    $"完整设备列表与自身授权已刷新；服务版本 {state.CatalogRevision}。");
                ViewModel.CompleteAgentCatalogUpdate(
                    $"Linux Agent 服务清单已提交至版本 {state.CatalogRevision}，本机清单和精确授权回执已原子保存。");
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Agent",
                    "An explicitly confirmed service catalog update was applied and refreshed.",
                    $"Revision: {state.CatalogRevision}; services: {state.Services.Count}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _pendingAgentCatalogUpdate = null;
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(
                "提交已取消或超时，Linux 最终状态可能已改变；请先同步并重新预览。",
                preservePreview: false));
        }
        catch (AgentEnrollmentException exception)
        {
            _pendingAgentCatalogUpdate = null;
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(exception.Message, preservePreview: false));
        }
        catch (Exception exception)
        {
            _pendingAgentCatalogUpdate = null;
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    "Catalog update failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentCatalogUpdate(
                "服务清单提交未能确认完成；本机持久配置没有改变，请同步后重新预览。",
                preservePreview: false));
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
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
                    : $"已导出 {result.ServiceCount} 个服务：{result.Path}。文件不含 SSH 密钥、配对码、Agent 凭据或服务器身份。",
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
                "必须先完成只读预览，并明确勾选确认后才能导入。任何账号、密钥或 Agent 凭据都不会从配置文件导入。");
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
                    "导入包含尚未授权的 Linux 端口，连接前必须通过 Agent 同步或重新配对。",
                ForwardAuthorizationStatus.PairingRecommended =>
                    "Linux 仍保留部分未使用授权，建议通过 Agent 同步或重新配对以收紧权限。",
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
        Agent = config.Agent with { },
        Services = config.Services.Select(service => service with { }).ToList(),
        WindowsLocalServices = config.WindowsLocalServices.Select(service => service with { }).ToList(),
    };

    private static string FormatProfilePreview(ServiceProfileImportPreview preview)
    {
        var diff = preview.Diff;
        var authorization = diff.AuthorizationImpact switch
        {
            RemoteAuthorizationImpact.ReauthorizationRequired =>
                $"新增 Linux 端口：{string.Join(", ", diff.AddedLinuxPorts)}。导入后必须通过 Agent 同步或重新配对，才可连接这些端口。",
            RemoteAuthorizationImpact.ReauthorizationRecommended =>
                $"移除 Linux 端口：{string.Join(", ", diff.RemovedLinuxPorts)}。建议之后收紧 Linux 授权。",
            _ => "Linux 精确端口授权范围不变。",
        };
        return $"文件：{preview.SourcePath}\n" +
               $"配置名：{preview.DisplayName} · {preview.ServiceCount} 个服务\n" +
               $"新增 {diff.AddedServices} · 修改 {diff.ModifiedServices} · 删除 {diff.RemovedServices} · 不变 {diff.UnchangedServices}\n" +
               authorization;
    }

    private async Task RunAgentStateActionAsync(bool saveCatalog)
    {
        var progressText = saveCatalog
            ? "正在核验 Agent 身份并同步服务清单…"
            : "正在核验 Agent 身份并刷新设备列表…";
        if (!TryBeginAgentManagement(progressText, out var cancellation))
        {
            return;
        }

        try
        {
            var request = CreateAgentManagementRequest(_config);
            var state = await _agentManagementService.GetStateAsync(request, cancellation.Token)
                .ConfigureAwait(false);
            if (saveCatalog)
            {
                var saved = await SaveConfigAsync(
                        current =>
                        {
                            if (current.Agent != request.Registration || current.Server != request.Server)
                            {
                                throw new AgentEnrollmentException(
                                    "同步期间 Agent 或服务器配置已经改变，请重新操作。");
                            }

                            return current with
                            {
                                Services = MergeAgentCatalogServices(current.Services, state.Services),
                                Authorization = new ForwardAuthorizationGrant
                                {
                                    IsKnown = true,
                                    ClientKeyFingerprint = current.Authorization.ClientKeyFingerprint,
                                    RemotePorts = state.PermitPorts.Distinct().Order().ToList(),
                                },
                                Agent = current.Agent with { CatalogRevision = state.CatalogRevision },
                            };
                        },
                        "Agent 服务清单和精确授权已同步。")
                    .ConfigureAwait(false);
                if (!saved)
                {
                    throw new AgentEnrollmentException("Agent 数据已读取，但 Windows 配置未能安全保存。");
                }
            }

            RunOnUi(() => ViewModel.ApplyAgentDevices(
                ToAgentDeviceUiStates(state.Devices, request.Registration.DeviceId),
                state.CurrentDeviceRole,
                CreateAgentManagementFeedback(state, saveCatalog)));
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Agent",
                    saveCatalog ? "Agent service catalog was synchronized." : "Agent device list was refreshed.",
                    $"Catalog: {state.CatalogRevision}; devices: {state.Devices.Count}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.FailAgentManagement("Agent 管理操作已取消或超时。"));
        }
        catch (AgentEnrollmentException exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Agent",
                    "Agent management operation did not complete.",
                    exception.GetType().Name)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentManagement(exception.Message));
        }
        catch (Exception exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Agent",
                    "Agent management operation failed unexpectedly.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.FailAgentManagement(
                "Agent 管理操作未能安全完成；本地配置没有改变。"));
        }
        finally
        {
            EndAgentManagement(cancellation);
        }
    }

    private static string CreateAgentManagementFeedback(
        AgentManagementState state,
        bool savedCatalog)
    {
        var isAdmin = string.Equals(state.CurrentDeviceRole, "admin", StringComparison.Ordinal);
        if (savedCatalog)
        {
            return isAdmin
                ? $"服务清单已同步至版本 {state.CatalogRevision}；完整设备列表也已刷新。"
                : $"当前设备的服务与精确授权已同步至版本 {state.CatalogRevision}；完整设备列表仅管理员可见。";
        }

        return isAdmin
            ? $"完整设备列表已刷新，共 {state.Devices.Count} 台（含已撤销记录）。"
            : "已刷新当前设备的授权状态；完整设备列表与撤销功能仅管理员可用。";
    }

    private async Task<bool> PersistAgentStateAsync(
        AgentManagementRequest request,
        AgentManagementState state) =>
        await SaveConfigAsync(
                current =>
                {
                    if (current.Agent != request.Registration || current.Server != request.Server)
                    {
                        throw new AgentEnrollmentException(
                            "同步期间 Agent 或服务器配置已经改变，请重新操作。");
                    }

                    return current with
                    {
                        Services = MergeAgentCatalogServices(current.Services, state.Services),
                        Authorization = new ForwardAuthorizationGrant
                        {
                            IsKnown = true,
                            ClientKeyFingerprint = current.Authorization.ClientKeyFingerprint,
                            RemotePorts = state.PermitPorts.Distinct().Order().ToList(),
                        },
                        Agent = current.Agent with { CatalogRevision = state.CatalogRevision },
                    };
                },
                "Agent 服务清单和精确授权已同步。")
            .ConfigureAwait(false);

    private static AgentRelayPlanRequest CreateAgentRelayRequest(AgentRelayDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var slug = draft.Slug?.Trim() ?? string.Empty;
        var name = draft.ServiceName?.Trim() ?? string.Empty;
        var description = draft.Description?.Trim() ?? string.Empty;
        var targetHost = draft.TargetHost?.Trim() ?? string.Empty;
        var openPath = draft.OpenPath?.Trim() ?? string.Empty;
        if (!IsValidAgentRelaySlug(slug))
        {
            throw new AgentEnrollmentException(
                "服务 ID 必须以小写字母开头，只能包含小写字母、数字和连字符，最长 48 个字符。");
        }
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl) ||
            description.Length > 160 || description.Any(char.IsControl))
        {
            throw new AgentEnrollmentException("显示名称必须为 1–80 个字符，说明最多 160 个字符。");
        }
        if (!int.TryParse(
                draft.LinuxRelayPort,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var relayPort) || relayPort is < 1 or > 65535 ||
            !int.TryParse(
                draft.TargetPort,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var targetPort) || targetPort is < 1 or > 65535 ||
            !int.TryParse(
                draft.WindowsLocalPort,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var localPort) || localPort is < 1 or > 65535)
        {
            throw new AgentEnrollmentException("Linux 中继、目标和 Windows 本地端口都必须是 1–65535 的整数。");
        }
        if (targetHost.Length is < 1 or > 253 || targetHost != draft.TargetHost ||
            targetHost.IndexOfAny(['*', '/', '\\', ':', '@', '?', '#']) >= 0 ||
            targetHost.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new AgentEnrollmentException(
                "目标主机必须是一个精确 DNS 名或 IPv4，不能填写 URL、IPv6、通配符、网段或凭据。");
        }
        if (openPath.Length is < 1 or > 512 || !openPath.StartsWith('/') ||
            openPath.StartsWith("//", StringComparison.Ordinal) ||
            openPath.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new AgentEnrollmentException("打开路径必须以单个 / 开头，且不能包含空白或控制字符。");
        }

        return new AgentRelayPlanRequest(
            new AgentRelaySpec(
                slug,
                relayPort,
                targetHost,
                targetPort,
                draft.UseHttps ? "https" : "http",
                openPath),
            new AgentRelayServicePresentation(
                name,
                description,
                "Globe",
                localPort,
                "/",
                AgentHealthKinds.TcpConnect,
                EnabledByDefault: true),
            draft.GrantToAllDevices);
    }

    private static IReadOnlyList<AgentServiceDefinition> CreateAgentServiceCatalog(
        IReadOnlyList<ServiceDefinition> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Count is < 1 or > AgentProtocol.MaximumServices)
        {
            throw new AgentEnrollmentException(
                $"Agent 服务清单必须包含 1–{AgentProtocol.MaximumServices} 个服务。");
        }

        var result = new List<AgentServiceDefinition>(services.Count);
        foreach (var service in services)
        {
            if (!ServiceOpenUrl.TryParse(service.OpenUrl, service.LocalPort, out var openUrl) ||
                openUrl is null)
            {
                throw new AgentEnrollmentException(
                    $"服务 {service.Name} 的 Windows 打开地址无法转换成 Agent 清单。");
            }
            var healthKind = service.HealthKind switch
            {
                HealthCheckKind.HttpStatus => AgentHealthKinds.HttpStatus,
                HealthCheckKind.JsonOk => AgentHealthKinds.JsonOk,
                HealthCheckKind.TcpConnect => AgentHealthKinds.TcpConnect,
                _ => throw new AgentEnrollmentException(
                    $"服务 {service.Name} 的健康检查类型不受 Agent 支持。"),
            };
            result.Add(new AgentServiceDefinition(
                service.Id,
                service.Name,
                service.Description,
                service.Icon,
                EnabledByDefault: service.Enabled,
                FavoriteByDefault: false,
                SuggestedLocalPort: service.LocalPort,
                ServiceDefinition.LoopbackHost,
                service.RemotePort,
                openUrl.Scheme,
                openUrl.LaunchPath,
                service.HealthPath,
                healthKind));
        }

        return result;
    }

    private static string FormatAgentCatalogUpdatePreview(AgentCatalogUpdatePreview preview)
    {
        static string FormatServices(
            IReadOnlyList<string> ids,
            IReadOnlyList<AgentServiceDefinition> services)
        {
            if (ids.Count == 0)
            {
                return "无";
            }
            var byId = services.ToDictionary(service => service.Id, StringComparer.Ordinal);
            return string.Join(", ", ids.Select(id =>
                byId.TryGetValue(id, out var service)
                    ? $"{id}(:{service.RemotePort})"
                    : id));
        }

        var affected = preview.AffectedDeviceIds.Count == 0
            ? "无"
            : string.Join(", ", preview.AffectedDeviceIds);
        return $"Linux 当前版本：{preview.ExpectedRevision}\n" +
               $"新增服务：{FormatServices(preview.AddedServiceIds, preview.ProposedServices)}\n" +
               $"修改服务：{FormatServices(preview.ModifiedServiceIds, preview.ProposedServices)}\n" +
               $"删除服务：{(preview.RemovedServiceIds.Count == 0 ? "无" : string.Join(", ", preview.RemovedServiceIds))}\n" +
               $"新增 Linux 授权端口：{FormatPorts(preview.AddedRemotePorts)}\n" +
               $"移除 Linux 授权端口：{FormatPorts(preview.RemovedRemotePorts)}\n" +
               $"受影响设备：{affected}\n" +
               $"默认授权范围：{(preview.ApplyDefaultsToAllDevices ? "把默认启用服务应用到所有有效设备" : "不自动扩展现有设备")}\n" +
               $"结论：{(preview.CanApply ? "可提交（仍需明确确认）" : preview.BlockingReason)}";
    }

    private static bool IsValidAgentRelaySlug(string value) =>
        value.Length is >= 1 and <= 48 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private string FormatAgentRelayPreview(PendingAgentRelayPlan pending)
    {
        var plan = pending.Plan.RelayPlan;
        var spec = plan.Spec ?? pending.InstallRequest?.Spec;
        var localPort = pending.InstallRequest?.Service.SuggestedLocalPort ??
                        _config.Services.FirstOrDefault(service =>
                            string.Equals(service.Id, pending.Slug, StringComparison.Ordinal))?.LocalPort;
        var mapping = spec is null
            ? $"服务：{pending.Slug}（服务器未返回目标映射）"
            : $"规范化映射：{spec.Scheme}://127.0.0.1:{localPort?.ToString() ?? "待同步"}{spec.OpenPath} → SSH → Linux 127.0.0.1:{spec.RelayPort} → {spec.TargetHost}:{spec.TargetPort}";
        var change = pending.Plan.ServiceChange;
        var affected = change.AffectedDeviceIds.Count == 0
            ? "无"
            : string.Join(", ", change.AffectedDeviceIds);
        var probe = pending.Probe is null
            ? "探测：删除计划不连接目标。"
            : $"探测：依赖 {FormatProbeValue(pending.Probe.DependencyAvailable)}；中继端口占用 {FormatNullableProbeValue(pending.Probe.RelayPortInUse, invert: true)}；目标可达 {FormatNullableProbeValue(pending.Probe.TargetReachable)}；全新安装就绪 {FormatProbeValue(pending.Probe.ReadyToInstall)}" +
              (string.IsNullOrEmpty(pending.Probe.TargetError)
                  ? string.Empty
                  : $"；目标错误：{pending.Probe.TargetError}");
        var tlsCaveat = spec?.Scheme == "https"
            ? "\nHTTPS 提醒：这是透明 TCP 中继；证书主机名、SNI、Cookie 或重定向仍可能不接受 127.0.0.1。"
            : string.Empty;
        var grantScope = pending.InstallRequest is null
            ? string.Empty
            : $"\n授权范围：{(pending.InstallRequest.GrantToAllDevices ? "所有有效设备" : "不自动授予现有设备")}。";
        var targetChange = plan.Action == "update"
            ? "\n目标变更提醒：Linux/SSH 端口可能保持不变，但以上受影响设备的最终目标会在应用后改变；已有 SSH 会话通过该中继发起的新连接也会到达新目标。"
            : string.Empty;
        return $"操作：{plan.Operation}/{plan.Action} · 单元：{plan.UnitName}\n" +
               $"{mapping}\n{probe}\n" +
               $"新增 Linux 授权端口：{FormatPorts(change.AddedRemotePorts)}\n" +
               $"移除 Linux 授权端口：{FormatPorts(change.RemovedRemotePorts)}\n" +
               $"受影响设备：{affected}\n" +
               $"下一服务版本：{pending.Plan.NextServicesRevision}\n" +
               $"Agent 结论：{(plan.CanApply ? "可应用" : "不可应用")} · {plan.Reason}" +
               grantScope + targetChange + tlsCaveat;
    }

    private static string FormatPorts(IReadOnlyList<int> ports) =>
        ports.Count == 0 ? "无" : string.Join(", ", ports);

    private static string FormatProbeValue(bool value) => value ? "正常" : "失败";

    private static bool IsAgentRelayProbeCompatible(
        AgentRelayPlan plan,
        AgentRelayProbeResponse probe) =>
        plan.CanApply && plan.Action switch
        {
            "create" => probe.ReadyToInstall,
            "update" or "no_change" =>
                probe.DependencyAvailable && probe.TargetReachable is true,
            _ => false,
        };

    private static string FormatNullableProbeValue(bool? value, bool invert = false) => value switch
    {
        null => "未检查",
        true when invert => "是（冲突）",
        false when invert => "否（正常）",
        true => "是",
        false => "否",
    };

    private void ScheduleAgentEnrollmentCodeExpiry(DateTimeOffset expiresAt)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(
            ref _agentEnrollmentCodeExpiryCancellation,
            cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _ = ClearAgentEnrollmentCodeAtExpiryAsync(expiresAt, cancellation);
    }

    private async Task ClearAgentEnrollmentCodeAtExpiryAsync(
        DateTimeOffset expiresAt,
        CancellationTokenSource cancellation)
    {
        try
        {
            var delay = expiresAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            }
            if (expiresAt == _agentEnrollmentCodeExpiresAt)
            {
                _agentEnrollmentCodeExpiresAt = default;
                RunOnUi(() => ViewModel.ClearAgentEnrollmentCode(
                    "添加码已过期并从界面清除；如仍需添加设备，请重新生成。"));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _agentEnrollmentCodeExpiryCancellation,
                        null,
                        cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private bool TryBeginAgentManagement(
        string feedback,
        out CancellationTokenSource cancellation)
    {
        cancellation = null!;
        if (Volatile.Read(ref _disposed) != 0 || !_config.Agent.IsConfigured ||
            Interlocked.Exchange(ref _setupOperationActive, 1) != 0)
        {
            if (!_config.Agent.IsConfigured)
            {
                RunOnUi(() => ViewModel.FailAgentManagement(
                    "当前设备没有 Agent 登记信息；可使用上方 Agent 登记或一次性配对。"));
            }
            return false;
        }

        Interlocked.Exchange(ref _agentManagementActive, 1);
        cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var previous = Interlocked.Exchange(ref _agentManagementCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        RunOnUi(() => ViewModel.BeginAgentManagement(feedback));
        return true;
    }

    private void EndAgentManagement(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(
                Interlocked.CompareExchange(ref _agentManagementCancellation, null, cancellation),
                cancellation))
        {
            cancellation.Dispose();
        }
        Interlocked.Exchange(ref _agentManagementActive, 0);
        Interlocked.Exchange(ref _setupOperationActive, 0);
    }

    private static AgentManagementRequest CreateAgentManagementRequest(AppConfig config) => new(
        config.Agent with { },
        config.Server with { },
        config.Authorization with { RemotePorts = config.Authorization.RemotePorts.ToList() },
        config.ExpectedTailnetDnsSuffix);

    private static IReadOnlyList<AgentDeviceUiState> ToAgentDeviceUiStates(
        IEnumerable<JKBridge.Core.Agent.AgentDeviceInfo> devices,
        string currentDeviceId) => devices
        .OrderBy(device => device.Revoked)
        .ThenByDescending(device => device.LastSeenAt)
        .Select(device => new AgentDeviceUiState(
            device.DeviceId,
            device.DeviceLabel,
            device.Role,
            device.TailscaleLogin,
            device.ClientKeyFingerprint,
            device.LastSeenAt is DateTimeOffset lastSeen
                ? lastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "从未连接",
            string.Equals(device.DeviceId, currentDeviceId, StringComparison.Ordinal),
            device.Revoked))
        .ToArray();

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

    public async Task SaveServiceAsync(ServiceUiState state)
    {
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
                    };
                    return current with
                    {
                        Services = current.Services
                            .Select(service => service.Id == state.Id ? updated : service with { })
                            .ToList(),
                    };
                },
                $"{state.Name.Trim()} 已保存。");
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

    public async Task SaveSettingsAsync(SettingsDraft settings)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!int.TryParse(settings.SshPort, out var sshPort))
        {
            RunOnUi(() => ViewModel.SettingsFeedback = "SSH 端口必须是 1–65535 的整数。");
            return;
        }

        var theme = settings.ThemeIndex switch
        {
            1 => ThemePreference.Light,
            2 => ThemePreference.Dark,
            _ => ThemePreference.System,
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
            RunOnUi(() => ApplyTheme(theme));
        }
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
            ViewModel.UpdateReleaseUrl = string.Empty;
            ViewModel.UpdateCheckFeedback =
                "正在查询 JK Bridge 官方 GitHub Releases…";
        });
        _pendingReleaseInstaller = null;

        try
        {
            var result = await _releaseUpdateChecker
                .CheckAsync(GetApplicationVersion(), cancellation.Token)
                .ConfigureAwait(false);
            RunOnUi(() =>
            {
                ViewModel.HasUpdateReleaseInfo = true;
                ViewModel.HasUpdateInstaller = result.Installer is not null;
                ViewModel.UpdateReleaseUrl = result.ReleasePage.AbsoluteUri;
                ViewModel.UpdateCheckFeedback = result.Installer is not null
                    ? result.UpdateAvailable
                        ? $"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）。可点击下方按钮下载、核验 SHA-256 并启动覆盖安装。"
                        : $"当前版本为 {result.CurrentVersion}；可重新下载官方 {result.LatestVersion} 安装器并覆盖安装。"
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

        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        Interlocked.Exchange(ref _releaseUpdateCancellation, cancellation)?.Dispose();
        RunOnUi(() =>
        {
            ViewModel.IsUpdateChecking = true;
            ViewModel.UpdateCheckFeedback =
                $"正在下载官方 {installer.Version} 安装器并核验 SHA-256…";
        });

        try
        {
            var download = await _releaseUpdateChecker
                .DownloadInstallerAsync(installer, _updateDirectory, cancellation.Token)
                .ConfigureAwait(false);
            var installerPath = ValidateDownloadedInstallerPath(download.InstallerPath);
            _installerLauncher(installerPath);
            RunOnUi(() => ViewModel.UpdateCheckFeedback =
                $"SHA-256 校验通过，安装器已启动。请在 Windows 安装向导中确认覆盖安装；当前安装包尚未代码签名，SmartScreen 提示属预期。校验值：{download.Sha256}");
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Updates",
                    "A verified official installer was explicitly launched.",
                    $"Release: {installer.Version}; SHA-256: {download.Sha256}")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunOnUi(() => ViewModel.UpdateCheckFeedback =
                "安装器下载已取消或超时；没有启动任何安装程序。");
        }
        catch (Exception exception) when (
            exception is ReleaseUpdateException or IOException or UnauthorizedAccessException or
                InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Updates",
                    "The explicit installer download or launch failed safely.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.UpdateCheckFeedback =
                "无法完成官方安装器下载、SHA-256 校验或启动；没有执行覆盖安装。可改用上方官方 Release 地址手动下载。");
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
        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
        });
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
        var agentEnrollmentCancellation = Interlocked.Exchange(ref _agentEnrollmentCancellation, null);
        agentEnrollmentCancellation?.Cancel();
        agentEnrollmentCancellation?.Dispose();
        var agentManagementCancellation = Interlocked.Exchange(ref _agentManagementCancellation, null);
        agentManagementCancellation?.Cancel();
        agentManagementCancellation?.Dispose();
        var agentDiscoveryCancellation = Interlocked.Exchange(ref _agentDiscoveryCancellation, null);
        agentDiscoveryCancellation?.Cancel();
        agentDiscoveryCancellation?.Dispose();
        var agentEnrollmentCodeExpiryCancellation = Interlocked.Exchange(
            ref _agentEnrollmentCodeExpiryCancellation,
            null);
        agentEnrollmentCodeExpiryCancellation?.Cancel();
        agentEnrollmentCodeExpiryCancellation?.Dispose();
        _agentEnrollmentCodeExpiresAt = default;
        _pendingAgentRelayPlan = null;
        _pendingAgentCatalogUpdate = null;
        _pendingReleaseInstaller = null;
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
            ? "2.2.0"
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
    }

    private void ApplyConfigToViewModel()
    {
        ViewModel.LinuxHost = _config.Server.Host;
        ViewModel.LinuxUser = _config.Server.User;
        ViewModel.SshPort = _config.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ViewModel.IdentityFile = _config.Server.IdentityFile;
        ViewModel.PinnedHostKey = _config.Server.PinnedHostKey;
        ViewModel.AgentEndpoint = _config.Agent.Endpoint;
        ViewModel.AgentDeviceLabel = string.IsNullOrWhiteSpace(_config.Agent.DeviceLabel)
            ? Environment.MachineName
            : _config.Agent.DeviceLabel;
        ViewModel.ApplyAgentRegistration(_config.Agent.IsConfigured);
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

    private static ServiceUiState ToServiceUiState(ServiceDefinition service, ServiceSnapshot snapshot)
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
            service.HealthPath);
    }

    /// <summary>
    /// Keeps Windows-local port choices when re-enrolling against the same service IDs. Linux
    /// targets, launch paths, enabled defaults, and health policy remain server-owned catalog data.
    /// </summary>
    private static List<ServiceDefinition> MergeAgentCatalogServices(
        IReadOnlyList<ServiceDefinition> current,
        IReadOnlyList<ServiceDefinition> catalog)
    {
        var currentById = current.ToDictionary(service => service.Id, StringComparer.OrdinalIgnoreCase);
        var localOverrides = catalog
            .Where(service => currentById.ContainsKey(service.Id))
            .ToDictionary(
                service => service.Id,
                service => currentById[service.Id].LocalPort,
                StringComparer.OrdinalIgnoreCase);
        var usedPorts = localOverrides.Values.ToHashSet();
        var merged = new List<ServiceDefinition>(catalog.Count);
        foreach (var service in catalog)
        {
            var localPort = localOverrides.TryGetValue(service.Id, out var existingPort)
                ? existingPort
                : FindAvailableLocalPort(service.LocalPort, usedPorts);
            usedPorts.Add(localPort);
            if (!ServiceOpenUrl.TryParse(service.OpenUrl, service.LocalPort, out var openUrl) ||
                openUrl is null)
            {
                throw new AgentEnrollmentException("Agent 服务清单包含无法重建的 Windows 打开地址。");
            }

            merged.Add(service with
            {
                LocalPort = localPort,
                OpenUrl = ServiceOpenUrl.Build(openUrl.Scheme, localPort, openUrl.LaunchPath),
            });
        }

        return merged;
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

        throw new AgentEnrollmentException("没有可用于 Agent 服务清单的 Windows 本地端口。");
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
