using System.ComponentModel;
using System.Net.Sockets;
using JKBridge.Core.Authorization;
using JKBridge.Core.Configuration;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Health;
using JKBridge.Core.Models;
using JKBridge.Core.Processes;
using JKBridge.Core.Tunneling;

namespace JKBridge.App.Services;

public interface IBridgeRuntime : IAsyncDisposable
{
    event EventHandler<TunnelSnapshot>? SnapshotChanged;

    TunnelSnapshot Snapshot { get; }

    Task StartAsync(AppConfig config, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task RestartAsync(AppConfig config, CancellationToken cancellationToken = default);

    Task RefreshAsync(AppConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adjusts the service health-check interval without reconnecting.
    /// </summary>
    void SetHealthCheckInterval(TimeSpan interval);
}

/// <summary>
/// Serializes connection commands and supervises the app's one SSH process.
/// </summary>
public sealed class BridgeRuntime : IBridgeRuntime
{
    private readonly ITailscaleClient _tailscale;
    private readonly ITunnelProcessFactory _processFactory;
    private readonly IServiceHealthProbe _healthProbe;
    private readonly ILocalPortChecker _portChecker;
    private readonly IKnownHostsManager _knownHosts;
    private readonly IAppLogger _logger;
    private readonly BridgeRuntimeOptions _options;
    private readonly IRuntimeDelay _delay;
    private readonly bool _ownsHealthProbe;
    private readonly SshCommandBuilder _commandBuilder = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private readonly object _processGate = new();

    private TunnelSnapshot _snapshot = TunnelSnapshot.Stopped();
    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisorTask;
    private ITunnelProcess? _activeProcess;
    private CancellationTokenSource? _readinessCancellation;
    private AppConfig? _config;
    private bool _desiredConnected;
    private int _disposed;

    public BridgeRuntime(
        ITailscaleClient tailscale,
        ITunnelProcessFactory processFactory,
        IServiceHealthProbe healthProbe,
        ILocalPortChecker portChecker,
        IKnownHostsManager knownHosts,
        IAppLogger logger,
        BridgeRuntimeOptions options,
        IRuntimeDelay? delay = null)
        : this(
            tailscale,
            processFactory,
            healthProbe,
            portChecker,
            knownHosts,
            logger,
            options,
            delay ?? new SystemRuntimeDelay(),
            ownsHealthProbe: false)
    {
    }

    private BridgeRuntime(
        ITailscaleClient tailscale,
        ITunnelProcessFactory processFactory,
        IServiceHealthProbe healthProbe,
        ILocalPortChecker portChecker,
        IKnownHostsManager knownHosts,
        IAppLogger logger,
        BridgeRuntimeOptions options,
        IRuntimeDelay delay,
        bool ownsHealthProbe)
    {
        _tailscale = tailscale ?? throw new ArgumentNullException(nameof(tailscale));
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        _portChecker = portChecker ?? throw new ArgumentNullException(nameof(portChecker));
        _knownHosts = knownHosts ?? throw new ArgumentNullException(nameof(knownHosts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _ownsHealthProbe = ownsHealthProbe;

        ArgumentException.ThrowIfNullOrWhiteSpace(_options.KnownHostsPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ReadinessDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.HealthCheckInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.StableConnectionReset, TimeSpan.Zero);
    }

    public event EventHandler<TunnelSnapshot>? SnapshotChanged;

    private TimeSpan? _healthIntervalOverride;

    /// <summary>
    /// Adjusts the service health-check interval without reconnecting. The
    /// supervisor loop reads the override on every iteration.
    /// </summary>
    public void SetHealthCheckInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _healthIntervalOverride = interval;
    }

    public TimeSpan EffectiveHealthCheckInterval =>
        _healthIntervalOverride ?? _options.HealthCheckInterval;

    public TunnelSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
            {
                return _snapshot;
            }
        }
    }

    public static BridgeRuntime CreateDefault(
        IAppLogger logger,
        BridgeRuntimeOptions? options = null)
    {
        var effectiveOptions = options ?? BridgeRuntimeOptions.CreateDefault();
        var healthProbe = new ServiceHealthProbe();
        return new BridgeRuntime(
            new TailscaleClient(new CommandRunner()),
            new TunnelProcessFactory(),
            healthProbe,
            new LocalPortChecker(),
            new KnownHostsManager(effectiveOptions.KnownHostsPath),
            logger,
            effectiveOptions,
            new SystemRuntimeDelay(),
            ownsHealthProbe: true);
    }

    /// <summary>
    /// Sets the desired state to connected and starts preflight in the background.
    /// Calling this again replaces any prior supervisor and applies the supplied complete config.
    /// </summary>
    public async Task StartAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancelReadinessCheck();
        var safeConfig = ValidateAndClone(config);

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopSupervisorAsync(publishStopped: false).ConfigureAwait(false);
            ThrowIfDisposed();
            _config = safeConfig;
            _desiredConnected = true;
            Publish(CreateSnapshot(
                safeConfig,
                TunnelPhase.Preflight,
                desiredConnected: true,
                "Checking Tailscale, SSH identity, and local ports."));

            _supervisorCts = new CancellationTokenSource();
            _supervisorTask = SuperviseAsync(safeConfig, _supervisorCts.Token);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancelReadinessCheck();
        await StopPublicAsync(cancellationToken, allowDisposed: false).ConfigureAwait(false);
    }

    public async Task RestartAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancelReadinessCheck();
        var safeConfig = ValidateAndClone(config);

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopSupervisorAsync(publishStopped: false).ConfigureAwait(false);
            ThrowIfDisposed();
            _config = safeConfig;
            _desiredConnected = true;
            Publish(CreateSnapshot(
                safeConfig,
                TunnelPhase.Preflight,
                desiredConnected: true,
                "Restarting the bridge and checking prerequisites."));
            _supervisorCts = new CancellationTokenSource();
            _supervisorTask = SuperviseAsync(safeConfig, _supervisorCts.Token);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Immediately re-runs preflight. When connected this is an immediate restart; when stopped it
    /// checks readiness without opening SSH or changing the desired state.
    /// </summary>
    public async Task RefreshAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var safeConfig = ValidateAndClone(config);

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_desiredConnected)
            {
                await StopSupervisorAsync(publishStopped: false).ConfigureAwait(false);
                ThrowIfDisposed();
                _config = safeConfig;
                _desiredConnected = true;
                Publish(CreateSnapshot(
                    safeConfig,
                    TunnelPhase.Preflight,
                    desiredConnected: true,
                    "Refreshing connection state."));
                _supervisorCts = new CancellationTokenSource();
                _supervisorTask = SuperviseAsync(safeConfig, _supervisorCts.Token);
                return;
            }

            _config = safeConfig;
            Publish(CreateSnapshot(
                safeConfig,
                TunnelPhase.Preflight,
                desiredConnected: false,
                "Checking readiness without starting the bridge."));
            using var readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_processGate)
            {
                _readinessCancellation = readinessCancellation;
            }

            PreparedConnection prepared;
            try
            {
                prepared = await PrepareAsync(safeConfig, readinessCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A newer connect/stop/restart command replaced this readiness-only refresh.
                return;
            }
            finally
            {
                lock (_processGate)
                {
                    if (ReferenceEquals(_readinessCancellation, readinessCancellation))
                    {
                        _readinessCancellation = null;
                    }
                }
            }
            Publish(prepared.IsReady
                ? CreateSnapshot(
                    safeConfig,
                    TunnelPhase.Stopped,
                    desiredConnected: false,
                    "Ready to connect.",
                    prepared.Preflight)
                : CreatePreparationFailureSnapshot(safeConfig, prepared, desiredConnected: false));
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelReadinessCheck();
        try
        {
            await StopPublicAsync(CancellationToken.None, allowDisposed: true).ConfigureAwait(false);
        }
        finally
        {
            if (_ownsHealthProbe && _healthProbe is IDisposable disposable)
            {
                disposable.Dispose();
            }

        }
    }

    private async Task StopPublicAsync(CancellationToken cancellationToken, bool allowDisposed)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!allowDisposed)
            {
                ThrowIfDisposed();
            }

            var config = _config;
            _desiredConnected = false;
            if (config is not null && _supervisorTask is not null)
            {
                var previous = Snapshot;
                Publish(new TunnelSnapshot(
                    TunnelPhase.Stopping,
                    DesiredConnected: false,
                    "Stopping the SSH bridge.",
                    DateTimeOffset.Now,
                    CreateServiceSnapshots(config, ServicePhase.WaitingForTunnel),
                    previous.PeerDnsName,
                    previous.TargetHost,
                    previous.UsingIpFallback,
                    previous.SshProcessId,
                    TailscaleSelfDnsName: previous.TailscaleSelfDnsName,
                    TailscaleLoginName: previous.TailscaleLoginName,
                    CurrentTailnetDnsSuffix: previous.CurrentTailnetDnsSuffix));
            }

            await StopSupervisorAsync(publishStopped: true).ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task StopSupervisorAsync(bool publishStopped)
    {
        var cts = _supervisorCts;
        var task = _supervisorTask;
        _supervisorCts = null;
        _supervisorTask = null;
        cts?.Cancel();

        ITunnelProcess? process;
        lock (_processGate)
        {
            process = _activeProcess;
        }

        if (process is not null)
        {
            try
            {
                await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "SSH",
                    "SSH process cleanup reported an error.",
                    exception.Message).ConfigureAwait(false);
            }
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts?.IsCancellationRequested == true)
            {
                // Expected stop path.
            }
            catch (Exception exception)
            {
                await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "Tunnel",
                    "Tunnel supervisor stopped unexpectedly.",
                    exception.ToString()).ConfigureAwait(false);
            }
        }

        cts?.Dispose();
        if (publishStopped)
        {
            var services = _config is null
                ? Array.Empty<ServiceSnapshot>()
                : CreateServiceSnapshots(_config, ServicePhase.WaitingForTunnel);
            var previous = Snapshot;
            Publish(new TunnelSnapshot(
                TunnelPhase.Stopped,
                DesiredConnected: false,
                "Bridge is stopped.",
                DateTimeOffset.Now,
                services,
                previous.PeerDnsName,
                previous.TargetHost,
                previous.UsingIpFallback,
                SshProcessId: null,
                TailscaleSelfDnsName: previous.TailscaleSelfDnsName,
                TailscaleLoginName: previous.TailscaleLoginName,
                CurrentTailnetDnsSuffix: previous.CurrentTailnetDnsSuffix));
        }
    }

    private void CancelReadinessCheck()
    {
        CancellationTokenSource? cancellation;
        lock (_processGate)
        {
            cancellation = _readinessCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The readiness operation completed between the lookup and cancellation.
        }
    }

    private async Task SuperviseAsync(AppConfig config, CancellationToken cancellationToken)
    {
        var retryAttempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Publish(CreateSnapshot(
                    config,
                    TunnelPhase.Preflight,
                    desiredConnected: true,
                    "Checking Tailscale, SSH identity, and local ports.",
                    retryAttempt: retryAttempt));

                var prepared = await PrepareAsync(config, cancellationToken).ConfigureAwait(false);
                if (!prepared.IsReady)
                {
                    Publish(CreatePreparationFailureSnapshot(config, prepared, desiredConnected: true));
                    await SafeLogAsync(
                        DiagnosticSeverity.Warning,
                        "Tunnel",
                        prepared.Message,
                        prepared.TechnicalDetail,
                        cancellationToken).ConfigureAwait(false);

                    if (!prepared.Retryable || !config.Behavior.AutoReconnect)
                    {
                        return;
                    }

                    await WaitBeforeRetryAsync(
                        config,
                        retryAttempt++,
                        prepared.Message,
                        prepared.Preflight,
                        failure: null,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var processOutcome = await RunOneProcessAsync(
                    config,
                    prepared,
                    retryAttempt,
                    cancellationToken).ConfigureAwait(false);
                if (processOutcome.StableConnection)
                {
                    retryAttempt = 0;
                }

                if (!processOutcome.ShouldRetry || !config.Behavior.AutoReconnect)
                {
                    return;
                }

                await WaitBeforeRetryAsync(
                    config,
                    retryAttempt++,
                    processOutcome.Failure?.UserMessage ?? "The SSH bridge disconnected.",
                    prepared.Preflight,
                    processOutcome.Failure,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Requested stop/restart.
        }
        catch (Exception exception)
        {
            Publish(CreateSnapshot(
                config,
                TunnelPhase.Faulted,
                _desiredConnected,
                "The bridge runtime encountered an unexpected error.",
                failure: new SshFailureClassification(
                    SshFailureKind.Unknown,
                    Retryable: false,
                    "The bridge runtime encountered an unexpected error.",
                    exception.Message,
                    null)));
            await SafeLogAsync(
                DiagnosticSeverity.Error,
                "Tunnel",
                "Unexpected runtime failure.",
                exception.ToString()).ConfigureAwait(false);
        }
    }

    private async Task<ProcessOutcome> RunOneProcessAsync(
        AppConfig config,
        PreparedConnection prepared,
        int retryAttempt,
        CancellationToken cancellationToken)
    {
        ITunnelProcess? process = null;
        EventHandler<string>? standardErrorHandler = null;
        var forwardingDenial = new ForwardingDenialTracker();
        var connectedAt = DateTimeOffset.MinValue;
        try
        {
            Publish(CreateSnapshot(
                config,
                TunnelPhase.Starting,
                desiredConnected: true,
                "Starting one secured SSH process for all enabled services.",
                prepared.Preflight,
                retryAttempt: retryAttempt));

            try
            {
                process = await _processFactory
                    .StartAsync(prepared.LaunchSpec!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or
                IOException or
                Win32Exception)
            {
                var failure = new SshFailureClassification(
                    SshFailureKind.Unknown,
                    Retryable: false,
                    "Windows OpenSSH could not be started.",
                    exception.Message,
                    null);
                Publish(CreateSnapshot(
                    config,
                    TunnelPhase.Faulted,
                    desiredConnected: true,
                    failure.UserMessage,
                    prepared.Preflight,
                    failure: failure));
                await SafeLogAsync(
                    DiagnosticSeverity.Error,
                    "SSH",
                    failure.UserMessage,
                    failure.TechnicalDetail,
                    cancellationToken).ConfigureAwait(false);
                return new ProcessOutcome(false, failure, StableConnection: false);
            }

            lock (_processGate)
            {
                _activeProcess = process;
            }

            standardErrorHandler = (_, line) => forwardingDenial.Observe(line);
            process.StandardErrorReceived += standardErrorHandler;
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var readinessTask = _delay.DelayAsync(_options.ReadinessDelay, cancellationToken);
            var first = await Task.WhenAny(exitTask, readinessTask).ConfigureAwait(false);
            if (first == exitTask)
            {
                return await HandleProcessExitAsync(
                    config,
                    prepared,
                    await exitTask.ConfigureAwait(false),
                    connectedAt,
                    cancellationToken).ConfigureAwait(false);
            }

            await readinessTask.ConfigureAwait(false);
            connectedAt = DateTimeOffset.UtcNow;
            await SafeLogAsync(
                DiagnosticSeverity.Information,
                "SSH",
                "SSH bridge started.",
                prepared.Preflight?.UsingIpFallback == true
                    ? "MagicDNS resolution failed; a runtime-only peer IPv4 address is in use."
                    : null,
                cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckServicesAsync(
                        config,
                        prepared.Preflight!,
                        process,
                        retryAttempt,
                        forwardingDenial,
                        cancellationToken)
                    .ConfigureAwait(false);

                var intervalTask = _delay.DelayAsync(EffectiveHealthCheckInterval, cancellationToken);
                var completed = await Task.WhenAny(exitTask, intervalTask).ConfigureAwait(false);
                if (completed == exitTask)
                {
                    return await HandleProcessExitAsync(
                        config,
                        prepared,
                        await exitTask.ConfigureAwait(false),
                        connectedAt,
                        cancellationToken).ConfigureAwait(false);
                }

                await intervalTask.ConfigureAwait(false);
            }

            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (process is not null)
            {
                if (standardErrorHandler is not null)
                {
                    process.StandardErrorReceived -= standardErrorHandler;
                }

                lock (_processGate)
                {
                    if (ReferenceEquals(_activeProcess, process))
                    {
                        _activeProcess = null;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        await SafeLogAsync(
                            DiagnosticSeverity.Warning,
                            "SSH",
                            "SSH process did not stop cleanly.",
                            exception.Message).ConfigureAwait(false);
                    }
                }

                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<ProcessOutcome> HandleProcessExitAsync(
        AppConfig config,
        PreparedConnection prepared,
        TunnelProcessExit exit,
        DateTimeOffset connectedAt,
        CancellationToken cancellationToken)
    {
        var failure = SshErrorClassifier.Classify(exit.StandardError, exit.ExitCode);
        var stable = connectedAt != DateTimeOffset.MinValue &&
                     DateTimeOffset.UtcNow - connectedAt >= _options.StableConnectionReset;
        var phase = MapSshFailurePhase(failure.Kind);
        var services = failure.Kind == SshFailureKind.ForwardingDenied
            ? CreateServiceSnapshots(config, ServicePhase.ForwardDenied, failure.UserMessage)
            : CreateServiceSnapshots(config, ServicePhase.WaitingForTunnel);

        Publish(CreateSnapshot(
            config,
            phase,
            desiredConnected: true,
            failure.UserMessage,
            prepared.Preflight,
            services: services,
            failure: failure));
        await SafeLogAsync(
            failure.Retryable ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
            "SSH",
            failure.UserMessage,
            failure.TechnicalDetail,
            cancellationToken).ConfigureAwait(false);

        return new ProcessOutcome(failure.Retryable, failure, stable);
    }

    private async Task CheckServicesAsync(
        AppConfig config,
        TailscalePreflightResult preflight,
        ITunnelProcess process,
        int retryAttempt,
        ForwardingDenialTracker forwardingDenial,
        CancellationToken cancellationToken)
    {
        var checking = config.Services.Select(service =>
            new ServiceSnapshot(
                service.Id,
                service.Name,
                service.Enabled ? ServicePhase.Checking : ServicePhase.Disabled)).ToArray();
        Publish(CreateSnapshot(
            config,
            TunnelPhase.Connected,
            desiredConnected: true,
            "SSH is connected; checking local service endpoints.",
            preflight,
            process.ProcessId,
            retryAttempt,
            services: checking));

        var checks = config.Services
            .Where(service => service.Enabled)
            .Select(service => CheckOneServiceAsync(service, cancellationToken))
            .ToArray();
        var health = await Task.WhenAll(checks).ConfigureAwait(false);
        var forwardingDenied = forwardingDenial.IsDenied;
        var healthById = health.ToDictionary(item => item.ServiceId, StringComparer.OrdinalIgnoreCase);
        var snapshots = config.Services.Select(service =>
        {
            if (!service.Enabled)
            {
                return new ServiceSnapshot(service.Id, service.Name, ServicePhase.Disabled);
            }

            var snapshot = ServiceSnapshot.FromHealth(service.Name, healthById[service.Id]);
            return forwardingDenied && snapshot.Phase == ServicePhase.RemoteUnavailable
                ? snapshot with
                {
                    Phase = ServicePhase.ForwardDenied,
                    Detail = "Linux SSH policy rejected this local forward. Check permitopen restrictions.",
                }
                : snapshot;
        }).ToArray();

        var available = snapshots.Count(item => item.Phase == ServicePhase.Available);
        var enabled = snapshots.Count(item => item.Phase != ServicePhase.Disabled);
        var allAvailable = enabled > 0 && available == enabled;
        Publish(CreateSnapshot(
            config,
            allAvailable ? TunnelPhase.Connected : TunnelPhase.Degraded,
            desiredConnected: true,
            allAvailable
                ? $"Connected. {available} service{(available == 1 ? string.Empty : "s")} available."
                : $"SSH is connected, but {enabled - available} service{(enabled - available == 1 ? string.Empty : "s")} did not answer.",
            preflight,
            process.ProcessId,
            retryAttempt,
            services: snapshots));
    }

    private async Task<ServiceHealth> CheckOneServiceAsync(
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _healthProbe.CheckAsync(service, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await SafeLogAsync(
                DiagnosticSeverity.Warning,
                "Health",
                $"Health check failed for {service.Name}.",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
            return new ServiceHealth(
                service.Id,
                ServiceHealthState.Unavailable,
                DateTimeOffset.Now,
                Detail: "The health check could not be completed.");
        }
    }

    private async Task<PreparedConnection> PrepareAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var authorization = ForwardAuthorization.Evaluate(config);
        if (authorization.Status == ForwardAuthorizationStatus.Unknown &&
            authorization.DesiredPorts.Count > 0)
        {
            return PreparedConnection.Failure(
                "No Linux authorization receipt is stored for this upgraded configuration. " +
                $"Re-pair once to confirm remote ports {string.Join(", ", authorization.DesiredPorts)}.",
                TunnelPhase.NeedsPairing,
                retryable: false,
                preflight: null);
        }

        if (authorization.Status == ForwardAuthorizationStatus.PairingRequired)
        {
            return PreparedConnection.Failure(
                $"Linux authorization is missing remote port{(authorization.MissingPorts.Count == 1 ? string.Empty : "s")} " +
                $"{string.Join(", ", authorization.MissingPorts)}. Re-pair before connecting.",
                TunnelPhase.NeedsPairing,
                retryable: false,
                preflight: null);
        }

        var preflight = await _tailscale.InspectAsync(config, cancellationToken).ConfigureAwait(false);
        if (!preflight.IsReady)
        {
            return PreparedConnection.Failure(
                preflight.Message,
                MapTailscalePhase(preflight.Kind),
                preflight.IsRetryable,
                preflight,
                preflight.TechnicalDetail);
        }

        var sshExecutable = _processFactory.FindSshExecutable();
        if (string.IsNullOrWhiteSpace(sshExecutable))
        {
            return PreparedConnection.Failure(
                "Windows OpenSSH Client is not installed or ssh.exe could not be found.",
                TunnelPhase.Faulted,
                retryable: false,
                preflight);
        }

        string identityPath;
        try
        {
            identityPath = Path.GetFullPath(_options.ExpandPath(config.Server.IdentityFile));
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return PreparedConnection.Failure(
                "The configured SSH identity path is invalid.",
                TunnelPhase.NeedsKeySetup,
                retryable: false,
                preflight,
                exception.Message);
        }

        if (!File.Exists(identityPath))
        {
            return PreparedConnection.Failure(
                "The dedicated SSH private key was not found. Complete key setup first.",
                TunnelPhase.NeedsKeySetup,
                retryable: false,
                preflight,
                identityPath);
        }

        var enabledServices = config.Services.Where(service => service.Enabled).ToArray();
        if (enabledServices.Length == 0)
        {
            return PreparedConnection.Failure(
                "Enable at least one service before connecting.",
                TunnelPhase.Faulted,
                retryable: false,
                preflight);
        }

        IReadOnlyList<int> conflicts;
        try
        {
            conflicts = await _portChecker
                .FindConflictsAsync(enabledServices.Select(service => service.LocalPort), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or InvalidOperationException)
        {
            return PreparedConnection.Failure(
                "Local port availability could not be checked.",
                TunnelPhase.Faulted,
                retryable: false,
                preflight,
                exception.Message);
        }

        if (conflicts.Count > 0)
        {
            return PreparedConnection.Failure(
                $"Local port{(conflicts.Count == 1 ? string.Empty : "s")} {string.Join(", ", conflicts)} already in use.",
                TunnelPhase.PortConflict,
                retryable: false,
                preflight);
        }

        try
        {
            await _knownHosts
                .EnsureAsync(config.Server.HostKeyAlias, config.Server.PinnedHostKey, cancellationToken)
                .ConfigureAwait(false);
            var launchSpec = _commandBuilder.Build(
                config,
                preflight.TargetHost!,
                _options.KnownHostsPath,
                sshExecutable,
                _options.ExpandPath);
            return PreparedConnection.Ready(preflight, launchSpec);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException)
        {
            return PreparedConnection.Failure(
                "The pinned SSH host-key file could not be prepared.",
                TunnelPhase.HostKeyBlocked,
                retryable: false,
                preflight,
                exception.Message);
        }
    }

    private async Task WaitBeforeRetryAsync(
        AppConfig config,
        int attempt,
        string reason,
        TailscalePreflightResult? preflight,
        SshFailureClassification? failure,
        CancellationToken cancellationToken)
    {
        var delay = _options.RetryDelay(attempt);
        if (delay < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Retry delay cannot be negative.");
        }

        var retryAt = DateTimeOffset.Now + delay;
        Publish(CreateSnapshot(
            config,
            TunnelPhase.Reconnecting,
            desiredConnected: true,
            $"{reason} Retrying shortly.",
            preflight,
            retryAttempt: attempt + 1,
            retryAt: retryAt,
            failure: failure));
        await _delay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    private TunnelSnapshot CreatePreparationFailureSnapshot(
        AppConfig config,
        PreparedConnection prepared,
        bool desiredConnected) =>
        CreateSnapshot(
            config,
            prepared.FailurePhase,
            desiredConnected,
            prepared.Message,
            prepared.Preflight,
            failure: null);

    private static TunnelSnapshot CreateSnapshot(
        AppConfig config,
        TunnelPhase phase,
        bool desiredConnected,
        string message,
        TailscalePreflightResult? preflight = null,
        int? processId = null,
        int retryAttempt = 0,
        DateTimeOffset? retryAt = null,
        IReadOnlyList<ServiceSnapshot>? services = null,
        SshFailureClassification? failure = null) =>
        new(
            phase,
            desiredConnected,
            message,
            DateTimeOffset.Now,
            services ?? CreateServiceSnapshots(config, ServicePhase.WaitingForTunnel),
            preflight?.Peer?.DnsName,
            preflight?.TargetHost,
            preflight?.UsingIpFallback ?? false,
            processId,
            retryAttempt,
            retryAt,
            failure,
            preflight?.Status?.Self?.DnsName,
            preflight?.Status?.LoginName,
            preflight?.Status?.Tailnet.MagicDnsSuffix);

    private static ServiceSnapshot[] CreateServiceSnapshots(
        AppConfig config,
        ServicePhase enabledPhase,
        string? detail = null) =>
        config.Services.Select(service =>
            new ServiceSnapshot(
                service.Id,
                service.Name,
                service.Enabled ? enabledPhase : ServicePhase.Disabled,
                Detail: service.Enabled ? detail : null)).ToArray();

    private void Publish(TunnelSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _snapshot = snapshot;
        }

        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TunnelSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch
            {
                // A UI subscriber must not terminate the background supervisor.
            }
        }
    }

    private async ValueTask SafeLogAsync(
        DiagnosticSeverity severity,
        string category,
        string message,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _logger
                .WriteAsync(severity, category, message, detail, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Runtime cancellation should not be delayed by logging.
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Diagnostics are best-effort and cannot own the connection state machine.
        }
    }

    private static AppConfig ValidateAndClone(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ConfigValidator.EnsureValid(config);
        return config with
        {
            Server = config.Server with { },
            Behavior = config.Behavior with { },
            Authorization = config.Authorization with
            {
                RemotePorts = config.Authorization.RemotePorts.ToList(),
            },
            Services = config.Services.Select(service => service with { }).ToList(),
        };
    }

    private static TunnelPhase MapTailscalePhase(TailscalePreflightKind kind) => kind switch
    {
        TailscalePreflightKind.ClientMissing or
        TailscalePreflightKind.CommandFailed or
        TailscalePreflightKind.NotRunning => TunnelPhase.NeedsTailscale,
        TailscalePreflightKind.WrongTailnet => TunnelPhase.WrongTailnet,
        TailscalePreflightKind.PeerMissing or
        TailscalePreflightKind.PeerAmbiguous or
        TailscalePreflightKind.PeerOffline or
        TailscalePreflightKind.NameResolutionFailed => TunnelPhase.PeerUnavailable,
        _ => TunnelPhase.Faulted,
    };

    private sealed class ForwardingDenialTracker
    {
        private int _denied;

        public bool IsDenied => Volatile.Read(ref _denied) != 0;

        public void Observe(string line)
        {
            if (SshErrorClassifier.Classify(line).Kind == SshFailureKind.ForwardingDenied)
            {
                Volatile.Write(ref _denied, 1);
            }
        }
    }

    private static TunnelPhase MapSshFailurePhase(SshFailureKind kind) => kind switch
    {
        SshFailureKind.HostKeyMismatch or
        SshFailureKind.HostKeyVerificationFailed => TunnelPhase.HostKeyBlocked,
        SshFailureKind.IdentityFileProblem or
        SshFailureKind.AuthenticationFailed => TunnelPhase.NeedsKeySetup,
        SshFailureKind.LocalPortConflict => TunnelPhase.PortConflict,
        SshFailureKind.NameResolutionFailed or
        SshFailureKind.ConnectionTimedOut or
        SshFailureKind.ConnectionRefused or
        SshFailureKind.NetworkUnavailable or
        SshFailureKind.ConnectionLost or
        SshFailureKind.CleanExit => TunnelPhase.PeerUnavailable,
        _ => TunnelPhase.Faulted,
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private sealed record PreparedConnection(
        bool IsReady,
        string Message,
        TunnelPhase FailurePhase,
        bool Retryable,
        TailscalePreflightResult? Preflight,
        SshLaunchSpec? LaunchSpec,
        string? TechnicalDetail)
    {
        public static PreparedConnection Ready(
            TailscalePreflightResult preflight,
            SshLaunchSpec launchSpec) =>
            new(true, string.Empty, TunnelPhase.Starting, false, preflight, launchSpec, null);

        public static PreparedConnection Failure(
            string message,
            TunnelPhase phase,
            bool retryable,
            TailscalePreflightResult? preflight,
            string? technicalDetail = null) =>
            new(false, message, phase, retryable, preflight, null, technicalDetail);
    }

    private sealed record ProcessOutcome(
        bool ShouldRetry,
        SshFailureClassification? Failure,
        bool StableConnection);
}
