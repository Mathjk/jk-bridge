using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using JKBridge.App.Services;
using JKBridge.Core.Agent;
using JKBridge.Core.Processes;
using JKBridge.Core.Tailscale;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class AgentDiscoveryServiceTests
{
    private const string Suffix = "example-tailnet.ts.net";

    [Fact]
    public async Task DiscoverAsync_UsesOnlyOnlinePeerTailscaleIpv4AtFixedPort()
    {
        var status = Status(
            Peer("linux", "node-linux", "100.64.0.10", online: true),
            Peer("offline", "node-offline", "100.64.0.11", online: false),
            Peer("public", "node-public", "192.0.2.20", online: true),
            new TailscaleNode(
                "node-v6",
                "ipv6",
                $"ipv6.{Suffix}",
                true,
                ["fd7a:115c:a1e0::1234"]));
        var factory = new FakeClientFactory(endpoint =>
            FakeClient.Valid(endpoint, "node-linux", $"linux.{Suffix}"));
        var service = CreateService(status, factory);

        var result = await service.DiscoverAsync();

        Assert.Equal(AgentDiscoveryOutcome.Single, result.Outcome);
        Assert.NotNull(result.Candidate);
        Assert.Equal("http://100.64.0.10:45871/", result.Candidate.Endpoint);
        var contacted = Assert.Single(factory.Endpoints);
        Assert.Equal(IPAddress.Parse("100.64.0.10"), contacted.Address);
        Assert.Equal(AgentDiscoveryService.AgentPort, contacted.Port);
        Assert.Equal(1, factory.HealthCalls);
        Assert.Equal(1, factory.BootstrapCalls);
        Assert.Equal(0, factory.AuthenticatedCalls);
    }

    [Theory]
    [InlineData("ip")]
    [InlineData("node")]
    [InlineData("dns")]
    [InlineData("suffix")]
    [InlineData("agent-version")]
    public async Task DiscoverAsync_RejectsAgentSelfClaimThatDoesNotMatchTailscale(
        string mismatch)
    {
        var status = Status(Peer("linux", "node-linux", "100.64.0.10"));
        var factory = new FakeClientFactory(endpoint =>
        {
            var bootstrap = Bootstrap(endpoint, "node-linux", $"linux.{Suffix}") with
            {
                TailscaleIpv4 = mismatch == "ip" ? "100.64.0.99" : endpoint.Address.ToString(),
                TailscaleNodeId = mismatch == "node" ? "node-attacker" : "node-linux",
                ServerHost = mismatch == "dns" ? $"other.{Suffix}" : $"linux.{Suffix}",
                TailnetDnsSuffix = mismatch == "suffix" ? "other-tailnet.ts.net" : Suffix,
                AgentVersion = mismatch == "agent-version" ? "different-version" : "test-agent",
            };
            return new FakeClient(bootstrap: bootstrap);
        });
        var service = CreateService(status, factory);

        var result = await service.DiscoverAsync();

        Assert.Equal(AgentDiscoveryOutcome.None, result.Outcome);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task DiscoverAsync_DeduplicatesMultipleIpv4sForSameStableNode()
    {
        var status = Status(new TailscaleNode(
            "node-linux",
            "linux",
            $"linux.{Suffix}",
            true,
            ["100.64.0.12", "100.64.0.10", "100.64.0.12"]));
        var factory = new FakeClientFactory(endpoint =>
            FakeClient.Valid(endpoint, "node-linux", $"linux.{Suffix}"));
        var service = CreateService(status, factory);

        var result = await service.DiscoverAsync();

        Assert.Equal(AgentDiscoveryOutcome.Single, result.Outcome);
        Assert.Single(result.Candidates);
        Assert.Equal("http://100.64.0.10:45871/", result.Candidate!.Endpoint);
        Assert.Equal(2, factory.Endpoints.Count);
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsMultipleInsteadOfChoosingAnAgent()
    {
        var status = Status(
            Peer("linux-a", "node-a", "100.64.0.10"),
            Peer("linux-b", "node-b", "100.64.0.11"));
        var factory = new FakeClientFactory(endpoint =>
        {
            var suffix = endpoint.Address.ToString().EndsWith(".10", StringComparison.Ordinal)
                ? (Node: "node-a", Host: $"linux-a.{Suffix}")
                : (Node: "node-b", Host: $"linux-b.{Suffix}");
            return FakeClient.Valid(endpoint, suffix.Node, suffix.Host);
        });
        var service = CreateService(status, factory);

        var result = await service.DiscoverAsync();

        Assert.Equal(AgentDiscoveryOutcome.Multiple, result.Outcome);
        Assert.Null(result.Candidate);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task DiscoverAsync_DoesNotContactIpv4ClaimedByTwoStableNodes()
    {
        var status = Status(
            Peer("linux-a", "node-a", "100.64.0.10"),
            Peer("linux-b", "node-b", "100.64.0.10"));
        var factory = new FakeClientFactory(_ => throw new InvalidOperationException("must not probe"));
        var service = CreateService(status, factory);

        var result = await service.DiscoverAsync();

        Assert.Equal(AgentDiscoveryOutcome.None, result.Outcome);
        Assert.Empty(factory.Endpoints);
    }

    [Fact]
    public async Task DiscoverAsync_DoesNotReturnUnsanitizedDisplayText()
    {
        var status = Status(Peer("linux", "node-linux", "100.64.0.10"));
        var factory = new FakeClientFactory(endpoint => new FakeClient(
            Bootstrap(endpoint, "node-linux", $"linux.{Suffix}") with
            {
                DisplayName = "Linux\r\nspoofed",
            }));
        var service = CreateService(status, factory);

        var result = await service.DiscoverAsync();

        Assert.Equal(AgentDiscoveryOutcome.None, result.Outcome);
    }

    [Fact]
    public async Task DiscoverAsync_BoundsStatusProviderTime()
    {
        var provider = new DelegateStatusProvider(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var factory = new FakeClientFactory(_ => throw new InvalidOperationException("must not probe"));
        var service = new AgentDiscoveryService(factory, provider, new AgentDiscoveryOptions
        {
            ProbeTimeout = TimeSpan.FromSeconds(1),
            OverallTimeout = TimeSpan.FromMilliseconds(50),
        });

        var exception = await Assert.ThrowsAsync<AgentDiscoveryException>(() =>
            service.DiscoverAsync());

        Assert.Contains("超时", exception.Message);
        Assert.Empty(factory.Endpoints);
    }

    [Fact]
    public async Task DiscoverAsync_BoundsConcurrencyAndOverallDuration()
    {
        var status = Status(Enumerable.Range(10, 12)
            .Select(index => Peer($"linux-{index}", $"node-{index}", $"100.64.0.{index}"))
            .ToArray());
        var concurrency = new ConcurrencyCounter();
        var factory = new FakeClientFactory(endpoint => new FakeClient(
            health: async cancellationToken =>
            {
                concurrency.Enter();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
                finally
                {
                    concurrency.Exit();
                }
            },
            bootstrap: Bootstrap(endpoint, "unused", $"unused.{Suffix}")));
        var service = CreateService(status, factory, new AgentDiscoveryOptions
        {
            MaximumConcurrency = 2,
            ProbeTimeout = TimeSpan.FromSeconds(1),
            OverallTimeout = TimeSpan.FromMilliseconds(100),
        });
        var stopwatch = Stopwatch.StartNew();

        var result = await service.DiscoverAsync();

        stopwatch.Stop();
        Assert.Equal(AgentDiscoveryOutcome.None, result.Outcome);
        Assert.InRange(concurrency.Maximum, 1, 2);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
        Assert.InRange(factory.HealthCalls, 1, 2);
        Assert.Equal(0, factory.BootstrapCalls);
    }

    [Fact]
    public async Task DiscoverAsync_PropagatesCallerCancellation()
    {
        var status = Status(Peer("linux", "node-linux", "100.64.0.10"));
        var factory = new FakeClientFactory(endpoint => new FakeClient(
            health: async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Health();
            },
            bootstrap: Bootstrap(endpoint, "node-linux", $"linux.{Suffix}")));
        var service = CreateService(status, factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DiscoverAsync(cancellation.Token));
    }

    [Fact]
    public async Task CliStatusProvider_UsesBoundedDirectTailscaleStatusCommand()
    {
        var commandRunner = new FakeCommandRunner(new CommandResult(
            0,
            """
            {
              "BackendState": "Running",
              "CurrentTailnet": {
                "Name": "example",
                "MagicDNSSuffix": "example-tailnet.ts.net",
                "MagicDNSEnabled": true
              },
              "Peer": {}
            }
            """,
            string.Empty,
            TimedOut: false,
            TimeSpan.FromMilliseconds(1)));
        var provider = new TailscaleCliAgentDiscoveryStatusProvider(
            commandRunner,
            () => @"C:\Program Files\Tailscale\tailscale.exe");

        var status = await provider.GetStatusAsync();

        Assert.True(status.IsRunning);
        Assert.NotNull(commandRunner.Request);
        Assert.Equal(@"C:\Program Files\Tailscale\tailscale.exe", commandRunner.Request.FileName);
        Assert.Equal(["status", "--json"], commandRunner.Request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(10), commandRunner.Request.Timeout);
        Assert.Equal(256 * 1024, commandRunner.Request.MaxOutputCharacters);
    }

    private static AgentDiscoveryService CreateService(
        TailscaleStatusSnapshot status,
        FakeClientFactory factory,
        AgentDiscoveryOptions? options = null) =>
        new(factory, new FakeStatusProvider(status), options);

    private static TailscaleStatusSnapshot Status(params TailscaleNode[] peers) =>
        new(
            "Running",
            new TailscaleTailnet("example", Suffix, true),
            new TailscaleNode(
                "windows-node",
                "windows",
                $"windows.{Suffix}",
                true,
                ["100.64.0.20"]),
            peers,
            "owner@example.com");

    private static TailscaleNode Peer(
        string host,
        string nodeId,
        string address,
        bool online = true) =>
        new(nodeId, host, $"{host}.{Suffix}", online, [address]);

    private static AgentHealthResponse Health() =>
        new(AgentProtocol.Version, "ok", "test-agent");

    private static AgentBootstrapResponse Bootstrap(
        AgentEndpoint endpoint,
        string nodeId,
        string host) =>
        new(
            AgentProtocol.Version,
            "test-agent",
            host,
            Suffix,
            endpoint.Address.ToString(),
            nodeId,
            "alice",
            22,
            TestConfigs.TestHostKey,
            $"Server {nodeId}",
            true,
            "1");

    private sealed class FakeStatusProvider(TailscaleStatusSnapshot status)
        : IAgentDiscoveryStatusProvider
    {
        public Task<TailscaleStatusSnapshot> GetStatusAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(status);
    }

    private sealed class DelegateStatusProvider(
        Func<CancellationToken, Task<TailscaleStatusSnapshot>> getStatus)
        : IAgentDiscoveryStatusProvider
    {
        public Task<TailscaleStatusSnapshot> GetStatusAsync(
            CancellationToken cancellationToken = default) => getStatus(cancellationToken);
    }

    private sealed class FakeClientFactory(Func<AgentEndpoint, FakeClient> create)
        : IAgentProtocolClientFactory
    {
        public ConcurrentQueue<AgentEndpoint> Endpoints { get; } = new();
        public int HealthCalls;
        public int BootstrapCalls;
        public int AuthenticatedCalls;

        public IAgentProtocolClient Create(AgentEndpoint endpoint)
        {
            Endpoints.Enqueue(endpoint);
            var client = create(endpoint);
            client.Factory = this;
            return client;
        }
    }

    private sealed class FakeClient : IAgentProtocolClient
    {
        private readonly Func<CancellationToken, Task<AgentHealthResponse>> _health;
        private readonly Func<CancellationToken, Task<AgentBootstrapResponse>> _bootstrap;

        public FakeClient(
            AgentBootstrapResponse bootstrap,
            Func<CancellationToken, Task<AgentHealthResponse>>? health = null)
        {
            _health = health ?? (_ => Task.FromResult(Health()));
            _bootstrap = _ => Task.FromResult(bootstrap);
        }

        public FakeClientFactory? Factory { get; set; }

        public static FakeClient Valid(AgentEndpoint endpoint, string nodeId, string host) =>
            new(Bootstrap(endpoint, nodeId, host));

        public Task<AgentHealthResponse> GetHealthAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.HealthCalls);
            return _health(cancellationToken);
        }

        public Task<AgentBootstrapResponse> GetBootstrapAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.BootstrapCalls);
            return _bootstrap(cancellationToken);
        }

        public Task<AgentEnrollmentResponse> EnrollAsync(
            JKBridge.Core.Agent.AgentEnrollmentRequest request,
            AgentCredential enrollmentCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentServiceCatalogResponse> GetServiceCatalogAsync(
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentCurrentDeviceResponse> GetCurrentDeviceAsync(
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentDeviceListResponse> GetDevicesAsync(
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentDeviceRevocationResponse> RevokeDeviceAsync(
            string deviceId,
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
            int ttlSeconds,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentRelayPlanResponse> PlanRelayAsync(
            AgentRelayPlanRequest request,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentRelayProbeResponse> ProbeRelayAsync(
            AgentRelaySpec spec,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentRelayApplyResponse> ApplyRelayAsync(
            AgentRelayApplyRequest request,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentRelayPlanResponse> PlanRelayDeletionAsync(
            AgentRelayDeletionPlanRequest request,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public Task<AgentRelayApplyResponse> DeleteRelayAsync(
            AgentRelayDeletionRequest request,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Factory!.AuthenticatedCalls);
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeCommandRunner(CommandResult result) : ICommandRunner
    {
        public CommandRequest? Request { get; private set; }

        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }

    private sealed class ConcurrencyCounter
    {
        private int _active;
        private int _maximum;

        public int Maximum => Volatile.Read(ref _maximum);

        public void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            int current;
            do
            {
                current = Volatile.Read(ref _maximum);
                if (current >= active)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maximum, active, current) != current);
        }

        public void Exit() => Interlocked.Decrement(ref _active);
    }
}
