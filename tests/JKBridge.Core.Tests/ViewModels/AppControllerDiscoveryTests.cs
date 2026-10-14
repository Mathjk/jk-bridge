using JKBridge.App.Platform;
using JKBridge.App.Services;
using JKBridge.Core.Agent;
using JKBridge.Core.Configuration;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Models;
using JKBridge.Core.Tunneling;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class AppControllerDiscoveryTests
{
    [Fact]
    public async Task None_ShowsReadOnlyEmptyResultWithoutEnrollment()
    {
        await using var fixture = ControllerFixture.Create(
            _ => Task.FromResult(new AgentDiscoveryResult(
                AgentDiscoveryOutcome.None,
                Array.Empty<AgentDiscoveryCandidate>())));

        await fixture.Controller.DiscoverAgentsAsync();

        Assert.Empty(fixture.Controller.ViewModel.AgentDiscoveryCandidates);
        Assert.Contains("没有发现", fixture.Controller.ViewModel.AgentDiscoveryFeedback);
        Assert.Equal(0, fixture.Enrollment.Calls);
    }

    [Fact]
    public async Task Single_IsPreselectedButEndpointChangesOnlyAfterExplicitUiCommand()
    {
        var candidate = Candidate("10", "Linux A");
        await using var fixture = ControllerFixture.Create(
            _ => Task.FromResult(new AgentDiscoveryResult(
                AgentDiscoveryOutcome.Single,
                [candidate])));
        fixture.Controller.ViewModel.AgentEndpoint = "http://100.64.0.99:45871/";

        await fixture.Controller.DiscoverAgentsAsync();

        Assert.Equal("http://100.64.0.99:45871/", fixture.Controller.ViewModel.AgentEndpoint);
        Assert.Equal(candidate.Endpoint, fixture.Controller.ViewModel.SelectedAgentDiscoveryCandidate?.Endpoint);
        Assert.Equal(0, fixture.Enrollment.Calls);

        fixture.Controller.ViewModel.UseDiscoveredAgentCommand.Execute(null);

        Assert.Equal(candidate.Endpoint, fixture.Controller.ViewModel.AgentEndpoint);
        Assert.Equal(0, fixture.Enrollment.Calls);
    }

    [Fact]
    public async Task Multiple_RequiresUserSelectionAndNeverChoosesOrEnrollsAutomatically()
    {
        var candidates = new[] { Candidate("10", "Linux A"), Candidate("11", "Linux B") };
        await using var fixture = ControllerFixture.Create(
            _ => Task.FromResult(new AgentDiscoveryResult(
                AgentDiscoveryOutcome.Multiple,
                candidates)));

        await fixture.Controller.DiscoverAgentsAsync();

        Assert.Equal(2, fixture.Controller.ViewModel.AgentDiscoveryCandidates.Count);
        Assert.Null(fixture.Controller.ViewModel.SelectedAgentDiscoveryCandidate);
        Assert.False(fixture.Controller.ViewModel.CanUseDiscoveredAgent);
        Assert.Empty(fixture.Controller.ViewModel.AgentEndpoint);
        Assert.Equal(0, fixture.Enrollment.Calls);
    }

    [Fact]
    public async Task RepeatedClick_DoesNotStartSecondDiscovery_AndDisposeCancelsFirst()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var fixture = ControllerFixture.Create(async cancellationToken =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });

        var first = fixture.Controller.DiscoverAgentsAsync();
        await started.Task;
        await fixture.Controller.DiscoverAgentsAsync();

        Assert.Equal(1, calls);
        await fixture.Controller.DisposeAsync();
        await first;
        Assert.False(fixture.Controller.ViewModel.IsAgentDiscovering);
        Assert.Equal(0, fixture.Enrollment.Calls);
    }

    [Fact]
    public async Task Exception_IsSanitizedAndDoesNotEnroll()
    {
        await using var fixture = ControllerFixture.Create(
            _ => Task.FromException<AgentDiscoveryResult>(
                new AgentDiscoveryException("已净化的发现失败。")));

        await fixture.Controller.DiscoverAgentsAsync();

        Assert.Equal("已净化的发现失败。", fixture.Controller.ViewModel.AgentDiscoveryFeedback);
        Assert.Empty(fixture.Controller.ViewModel.AgentDiscoveryCandidates);
        Assert.Equal(0, fixture.Enrollment.Calls);
    }

    private static AgentDiscoveryCandidate Candidate(string finalOctet, string name) => new(
        $"http://100.64.0.{finalOctet}:45871/",
        name,
        $"linux-{finalOctet}.example-tailnet.ts.net",
        "example-tailnet.ts.net",
        $"node-{finalOctet}",
        "1.0",
        true);

    private sealed class ControllerFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private ControllerFixture(
            string directory,
            AppController controller,
            FakeAgentEnrollmentService enrollment)
        {
            _directory = directory;
            Controller = controller;
            Enrollment = enrollment;
        }

        public AppController Controller { get; }

        public FakeAgentEnrollmentService Enrollment { get; }

        public static ControllerFixture Create(
            Func<CancellationToken, Task<AgentDiscoveryResult>> discover)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"jkbridge-discovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var configStore = new ConfigStore(Path.Combine(directory, "config.json"));
            var logger = new RollingFileLogger(Path.Combine(directory, "logs"));
            var enrollment = new FakeAgentEnrollmentService();
            var controller = new AppController(
                configStore,
                logger,
                new FakeRuntime(),
                new FakePairingService(),
                new StartupRegistration($"JK Bridge Test {Guid.NewGuid():N}"),
                Path.Combine(directory, "logs"),
                () => null,
                agentEnrollmentService: enrollment,
                agentDiscoveryService: new FakeDiscoveryService(discover),
                dispatchUi: action => action());
            return new ControllerFixture(directory, controller, enrollment);
        }

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class FakeDiscoveryService(
        Func<CancellationToken, Task<AgentDiscoveryResult>> discover)
        : IAgentDiscoveryService
    {
        public Task<AgentDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) => discover(cancellationToken);
    }

    private sealed class FakeAgentEnrollmentService : IAgentEnrollmentService
    {
        public int Calls { get; private set; }

        public Task<AgentEnrollmentResult> EnrollAsync(
            JKBridge.App.Services.AgentEnrollmentRequest request,
            IProgress<AgentEnrollmentProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<AgentEnrollmentResult>(new NotSupportedException());
        }
    }

    private sealed class FakePairingService : IDevicePairingService
    {
        public Task<DevicePairingResult> PairAsync(
            DevicePairingRequest request,
            IProgress<DevicePairingProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<DevicePairingResult>(new NotSupportedException());
    }

    private sealed class FakeRuntime : IBridgeRuntime
    {
        public event EventHandler<TunnelSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public TunnelSnapshot Snapshot { get; } = TunnelSnapshot.Stopped();

        public Task StartAsync(AppConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestartAsync(AppConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RefreshAsync(AppConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void SetHealthCheckInterval(TimeSpan interval)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
