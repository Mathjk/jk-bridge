using JKBridge.App.Services;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Health;
using JKBridge.Core.Models;
using JKBridge.Core.Tailscale;
using JKBridge.Core.Tunneling;

namespace JKBridge.Core.Tests.Runtime;

public sealed class BridgeRuntimeTests
{
    [Fact]
    public async Task StartAsync_StartsOneProcessForAllForwards_AndPublishesHealth()
    {
        await using var fixture = RuntimeFixture.Create();

        await fixture.Runtime.StartAsync(fixture.Config);
        var snapshot = await WaitForSnapshotAsync(
            fixture.Runtime,
            item => item.Phase == TunnelPhase.Connected &&
                    item.Services.All(service => service.Phase == ServicePhase.Available));

        Assert.Equal(1, fixture.ProcessFactory.StartCount);
        var spec = Assert.IsType<SshLaunchSpec>(fixture.ProcessFactory.LastSpec);
        Assert.Equal(TestConfigs.LinuxHost, spec.TargetHost);
        Assert.Equal([8080, 8765], spec.LocalPorts);
        Assert.Equal(2, spec.Arguments.Count(argument => argument == "-L"));
        var configIndex = Array.IndexOf(spec.Arguments.ToArray(), "-F");
        Assert.True(configIndex >= 0);
        Assert.Equal("none", spec.Arguments[configIndex + 1]);
        Assert.Contains("PermitLocalCommand=no", spec.Arguments);
        Assert.Contains("ProxyCommand=none", spec.Arguments);
        Assert.NotNull(snapshot.SshProcessId);
        Assert.Equal(2, snapshot.Services.Count);

        await fixture.Runtime.StopAsync();
        Assert.Equal(TunnelPhase.Stopped, fixture.Runtime.Snapshot.Phase);
        Assert.Equal("user@example.com", fixture.Runtime.Snapshot.TailscaleLoginName);
        Assert.Equal(TestConfigs.TailnetSuffix, fixture.Runtime.Snapshot.CurrentTailnetDnsSuffix);
        Assert.True(fixture.ProcessFactory.LastProcess!.StopCalled);
    }

    [Fact]
    public async Task StartAsync_MissingIdentityIsActionRequired_AndDoesNotStartSsh()
    {
        await using var fixture = RuntimeFixture.Create(createIdentity: false);

        await fixture.Runtime.StartAsync(fixture.Config);
        var snapshot = await WaitForSnapshotAsync(
            fixture.Runtime,
            item => item.Phase == TunnelPhase.NeedsKeySetup);

        Assert.Contains("private key", snapshot.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.ProcessFactory.StartCount);
        Assert.False(fixture.KnownHosts.Called);
    }

    [Fact]
    public async Task StartAsync_PortConflictStopsBeforeWritingKnownHostsOrStartingSsh()
    {
        await using var fixture = RuntimeFixture.Create(conflicts: [8765]);

        await fixture.Runtime.StartAsync(fixture.Config);
        var snapshot = await WaitForSnapshotAsync(
            fixture.Runtime,
            item => item.Phase == TunnelPhase.PortConflict);

        Assert.Contains("8765", snapshot.Message);
        Assert.Equal(0, fixture.ProcessFactory.StartCount);
        Assert.False(fixture.KnownHosts.Called);
    }

    [Fact]
    public async Task HostKeyMismatchIsNeverAutomaticallyRetried()
    {
        var exited = FakeTunnelProcess.Exited(
            255,
            "WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!");
        await using var fixture = RuntimeFixture.Create(processes: [exited]);

        await fixture.Runtime.StartAsync(fixture.Config);
        var snapshot = await WaitForSnapshotAsync(
            fixture.Runtime,
            item => item.Phase == TunnelPhase.HostKeyBlocked);

        Assert.Equal(SshFailureKind.HostKeyMismatch, snapshot.Failure?.Kind);
        Assert.False(snapshot.Failure?.Retryable);
        await Task.Delay(30);
        Assert.Equal(1, fixture.ProcessFactory.StartCount);
    }

    [Fact]
    public async Task TransientSshExitAutomaticallyReconnects()
    {
        var first = FakeTunnelProcess.Exited(255, "Connection reset by peer");
        var second = new FakeTunnelProcess();
        await using var fixture = RuntimeFixture.Create(processes: [first, second]);

        await fixture.Runtime.StartAsync(fixture.Config);
        var connected = await WaitForSnapshotAsync(
            fixture.Runtime,
            item => item.Phase == TunnelPhase.Connected && item.SshProcessId == second.ProcessId);

        Assert.Equal(2, fixture.ProcessFactory.StartCount);
        Assert.Equal(SshFailureKind.ConnectionLost, first.Classification?.Kind);
        Assert.Equal(second.ProcessId, connected.SshProcessId);
    }

    [Fact]
    public async Task RefreshWhileStopped_ChecksReadinessWithoutStartingProcess()
    {
        await using var fixture = RuntimeFixture.Create();

        await fixture.Runtime.RefreshAsync(fixture.Config);

        Assert.Equal(TunnelPhase.Stopped, fixture.Runtime.Snapshot.Phase);
        Assert.Equal("Ready to connect.", fixture.Runtime.Snapshot.Message);
        Assert.False(fixture.Runtime.Snapshot.DesiredConnected);
        Assert.True(fixture.KnownHosts.Called);
        Assert.Equal(0, fixture.ProcessFactory.StartCount);
    }

    [Fact]
    public async Task StreamingForwardDenialMarksUnavailableServicesWithoutSshExit()
    {
        var healthProbe = new FakeUnavailableHealthProbe();
        await using var fixture = RuntimeFixture.Create(
            healthProbe: healthProbe,
            healthCheckInterval: TimeSpan.FromSeconds(10));
        healthProbe.BeforeResult = () => fixture.ProcessFactory.LastProcess!.EmitStandardError(
            "channel 3: open failed: administratively prohibited: open failed");

        await fixture.Runtime.StartAsync(fixture.Config);
        var snapshot = await WaitForSnapshotAsync(
            fixture.Runtime,
            item => item.Services.Any(service => service.Phase == ServicePhase.ForwardDenied));

        Assert.False(fixture.ProcessFactory.LastProcess!.HasExited);
        Assert.All(
            snapshot.Services.Where(service => service.Phase != ServicePhase.Disabled),
            service => Assert.Equal(ServicePhase.ForwardDenied, service.Phase));
    }

    [Fact]
    public async Task StopCancelsAStoppedStateReadinessRefreshWithoutSurfacingCancellation()
    {
        var tailscale = new BlockingTailscaleClient();
        await using var fixture = RuntimeFixture.Create(tailscale: tailscale);

        var refresh = fixture.Runtime.RefreshAsync(fixture.Config);
        await tailscale.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stop = fixture.Runtime.StopAsync();

        await refresh.WaitAsync(TimeSpan.FromSeconds(1));
        await stop.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(TunnelPhase.Stopped, fixture.Runtime.Snapshot.Phase);
    }

    private static async Task<TunnelSnapshot> WaitForSnapshotAsync(
        BridgeRuntime runtime,
        Func<TunnelSnapshot, bool> predicate)
    {
        var current = runtime.Snapshot;
        if (predicate(current))
        {
            return current;
        }

        var completion = new TaskCompletionSource<TunnelSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, TunnelSnapshot snapshot)
        {
            if (predicate(snapshot))
            {
                completion.TrySetResult(snapshot);
            }
        }

        runtime.SnapshotChanged += Handler;
        try
        {
            current = runtime.Snapshot;
            if (predicate(current))
            {
                return current;
            }

            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            runtime.SnapshotChanged -= Handler;
        }
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private RuntimeFixture(
            string directory,
            AppConfig config,
            BridgeRuntime runtime,
            FakeTunnelProcessFactory processFactory,
            FakeKnownHostsManager knownHosts)
        {
            _directory = directory;
            Config = config;
            Runtime = runtime;
            ProcessFactory = processFactory;
            KnownHosts = knownHosts;
        }

        public AppConfig Config { get; }

        public BridgeRuntime Runtime { get; }

        public FakeTunnelProcessFactory ProcessFactory { get; }

        public FakeKnownHostsManager KnownHosts { get; }

        public static RuntimeFixture Create(
            bool createIdentity = true,
            IReadOnlyList<int>? conflicts = null,
            IReadOnlyList<FakeTunnelProcess>? processes = null,
            IServiceHealthProbe? healthProbe = null,
            TimeSpan? healthCheckInterval = null,
            ITailscaleClient? tailscale = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"jkbridge-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var identity = Path.Combine(directory, "id_ed25519");
            if (createIdentity)
            {
                File.WriteAllText(identity, "test key placeholder");
            }

            var configured = TestConfigs.CreateConfigured();
            var config = configured with
            {
                Server = configured.Server with { IdentityFile = identity },
            };
            var preflight = ReadyPreflight();
            var processFactory = new FakeTunnelProcessFactory(processes);
            var knownHosts = new FakeKnownHostsManager();
            var options = new BridgeRuntimeOptions
            {
                KnownHostsPath = Path.Combine(directory, "known_hosts"),
                ReadinessDelay = TimeSpan.FromMilliseconds(2),
                HealthCheckInterval = healthCheckInterval ?? TimeSpan.FromSeconds(10),
                StableConnectionReset = TimeSpan.FromMinutes(1),
                RetryDelay = _ => TimeSpan.FromMilliseconds(2),
                ExpandPath = value => value,
            };
            var runtime = new BridgeRuntime(
                tailscale ?? new FakeTailscaleClient(preflight),
                processFactory,
                healthProbe ?? new FakeHealthProbe(),
                new FakePortChecker(conflicts ?? Array.Empty<int>()),
                knownHosts,
                new FakeLogger(),
                options);
            return new RuntimeFixture(directory, config, runtime, processFactory, knownHosts);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A failed assertion can leave a process monitor completing; temp cleanup is best effort.
            }
        }

        private static TailscalePreflightResult ReadyPreflight()
        {
            var peer = new TailscaleNode(
                "peer-id",
                "linux-box",
                TestConfigs.LinuxHost,
                Online: true,
                ["100.64.0.10"]);
            var self = new TailscaleNode(
                "self-id",
                "windows-client",
                TestConfigs.WindowsHost,
                Online: true,
                ["100.64.0.20"]);
            var status = new TailscaleStatusSnapshot(
                "Running",
                new TailscaleTailnet("Example tailnet", TestConfigs.TailnetSuffix, MagicDnsEnabled: true),
                self,
                [peer],
                "user@example.com");
            return new TailscalePreflightResult(
                TailscalePreflightKind.Ready,
                "ready",
                Status: status,
                Peer: peer,
                TargetHost: peer.DnsName);
        }
    }

    private sealed class FakeTailscaleClient(TailscalePreflightResult result) : ITailscaleClient
    {
        public Task<TailscalePreflightResult> InspectAsync(
            AppConfig config,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class BlockingTailscaleClient : ITailscaleClient
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TailscalePreflightResult> InspectAsync(
            AppConfig config,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking preflight should only complete through cancellation.");
        }
    }

    private sealed class FakeTunnelProcessFactory : ITunnelProcessFactory
    {
        private readonly Queue<FakeTunnelProcess> _processes;

        public FakeTunnelProcessFactory(IReadOnlyList<FakeTunnelProcess>? processes)
        {
            _processes = new Queue<FakeTunnelProcess>(processes ?? [new FakeTunnelProcess()]);
        }

        public int StartCount { get; private set; }

        public SshLaunchSpec? LastSpec { get; private set; }

        public FakeTunnelProcess? LastProcess { get; private set; }

        public string? FindSshExecutable() => "ssh.exe";

        public Task<ITunnelProcess> StartAsync(
            SshLaunchSpec launchSpec,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            LastSpec = launchSpec;
            LastProcess = _processes.Count > 0 ? _processes.Dequeue() : new FakeTunnelProcess();
            return Task.FromResult<ITunnelProcess>(LastProcess);
        }
    }

    private sealed class FakeTunnelProcess : ITunnelProcess
    {
        private static int _nextId = 1000;
        private readonly TaskCompletionSource<TunnelProcessExit> _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public FakeTunnelProcess()
        {
            ProcessId = Interlocked.Increment(ref _nextId);
        }

        public int? ProcessId { get; }

        public event EventHandler<string>? StandardErrorReceived;

        public bool HasExited => _exit.Task.IsCompleted;

        public bool StopCalled { get; private set; }

        public SshFailureClassification? Classification { get; private set; }

        public static FakeTunnelProcess Exited(int exitCode, string error)
        {
            var process = new FakeTunnelProcess();
            process._exit.SetResult(new TunnelProcessExit(exitCode, error));
            process.Classification = SshErrorClassifier.Classify(error, exitCode);
            return process;
        }

        public void EmitStandardError(string line) => StandardErrorReceived?.Invoke(this, line);

        public Task<TunnelProcessExit> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exit.Task.WaitAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            _exit.TrySetResult(new TunnelProcessExit(0, string.Empty));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _exit.TrySetResult(new TunnelProcessExit(0, string.Empty));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHealthProbe : IServiceHealthProbe
    {
        public Task<ServiceHealth> CheckAsync(
            ServiceDefinition service,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServiceHealth(
                service.Id,
                ServiceHealthState.Available,
                DateTimeOffset.Now,
                TimeSpan.FromMilliseconds(1),
                200));
    }

    private sealed class FakeUnavailableHealthProbe : IServiceHealthProbe
    {
        public Action? BeforeResult { get; set; }

        public Task<ServiceHealth> CheckAsync(
            ServiceDefinition service,
            CancellationToken cancellationToken = default)
        {
            BeforeResult?.Invoke();
            return Task.FromResult(new ServiceHealth(
                service.Id,
                ServiceHealthState.Unavailable,
                DateTimeOffset.Now,
                Detail: "connection closed"));
        }
    }

    private sealed class FakePortChecker(IReadOnlyList<int> conflicts) : ILocalPortChecker
    {
        public Task<IReadOnlyList<int>> FindConflictsAsync(
            IEnumerable<int> localPorts,
            CancellationToken cancellationToken = default) => Task.FromResult(conflicts);
    }

    private sealed class FakeKnownHostsManager : IKnownHostsManager
    {
        public bool Called { get; private set; }

        public Task EnsureAsync(
            string hostKeyAlias,
            string pinnedHostKey,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLogger : IAppLogger
    {
        public event EventHandler<DiagnosticEvent>? EventWritten;

        public IReadOnlyList<DiagnosticEvent> Snapshot() => Array.Empty<DiagnosticEvent>();

        public ValueTask WriteAsync(
            DiagnosticSeverity severity,
            string category,
            string message,
            string? detail = null,
            CancellationToken cancellationToken = default)
        {
            EventWritten?.Invoke(this, new DiagnosticEvent(
                DateTimeOffset.Now,
                severity,
                category,
                message,
                detail));
            return ValueTask.CompletedTask;
        }
    }
}
