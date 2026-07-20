using System.Diagnostics;
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

namespace JKBridge.App.Services;

/// <summary>
/// Composes persistence, the connection runtime, platform actions, and the presentation model.
/// All process/network work remains outside the view model; UI updates are marshalled to Avalonia.
/// </summary>
public sealed class AppController : IBridgeUiActions, IAsyncDisposable
{
    private readonly ConfigStore _configStore;
    private readonly RollingFileLogger _logger;
    private readonly IBridgeRuntime _runtime;
    private readonly IDevicePairingService _pairingService;
    private readonly StartupRegistration _startupRegistration;
    private readonly Func<Window?> _getWindow;
    private readonly string _logDirectory;
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private AppConfig _config = AppConfig.CreateDefault();
    private int _resumeAfterSleep;
    private CancellationTokenSource? _pairingCancellation;
    private int _pairingActive;
    private int _disposed;

    public AppController(
        ConfigStore configStore,
        RollingFileLogger logger,
        IBridgeRuntime runtime,
        IDevicePairingService pairingService,
        StartupRegistration startupRegistration,
        string logDirectory,
        Func<Window?> getWindow)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _pairingService = pairingService ?? throw new ArgumentNullException(nameof(pairingService));
        _startupRegistration = startupRegistration ?? throw new ArgumentNullException(nameof(startupRegistration));
        _logDirectory = Path.GetFullPath(logDirectory);
        _getWindow = getWindow ?? throw new ArgumentNullException(nameof(getWindow));
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
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _pairingActive, 1) != 0)
        {
            return;
        }

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
        }
    }

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
        if (route is null)
        {
            return;
        }

        await SetClipboardTextAsync(route.OpenUri.AbsoluteUri);
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
                    var healthPath = state.HealthKind == HealthCheckKind.HttpStatus
                        ? ServiceRouteDefinition.DeriveHealthPath(state.LocalUrl, existing.HealthPath)
                        : existing.HealthPath;

                    var updated = existing with
                    {
                        Name = state.Name.Trim(),
                        Description = state.Description.Trim(),
                        LocalPort = state.LocalPort,
                        RemotePort = state.RemotePort,
                        OpenUrl = state.LocalUrl.Trim(),
                        HealthPath = healthPath,
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

        await _configGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _runtime.SnapshotChanged -= RuntimeOnSnapshotChanged;
            _logger.EventWritten -= LoggerOnEventWritten;
            await _runtime.DisposeAsync().ConfigureAwait(false);
            await _logger.DisposeAsync().ConfigureAwait(false);
            _configStore.Dispose();
        }
        finally
        {
            _configGate.Release();
        }
    }

    private async Task<bool> SaveConfigAsync(
        Func<AppConfig, AppConfig> createNext,
        string successMessage)
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
            if (_config.SetupCompleted)
            {
                await _runtime.RefreshAsync(_config).ConfigureAwait(false);
            }
            else
            {
                await _runtime.StopAsync().ConfigureAwait(false);
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
            service.HealthKind);
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

    private static void RunOnUi(Action action)
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
