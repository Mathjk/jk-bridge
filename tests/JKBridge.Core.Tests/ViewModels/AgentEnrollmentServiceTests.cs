using System.Net;
using System.Text.Json;
using JKBridge.App.Services;
using JKBridge.Core.Agent;
using JKBridge.Core.Processes;
using JKBridge.Core.Security;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class AgentEnrollmentServiceTests
{
    private const string Endpoint = "http://100.64.0.10:45871/";
    private const string Host = "linux-box.example-tailnet.ts.net";
    private const string Suffix = "example-tailnet.ts.net";
    private const string PublicKey = TestConfigs.TestHostKey;
    private static readonly string Fingerprint = Ed25519PublicKey.ComputeFingerprint(PublicKey);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public async Task EndpointWithoutExactlyOneOnlinePeer_FailsBeforeAgentContact(
        bool peerOnline,
        int peerCount)
    {
        var protocol = new FakeProtocolClient();
        var service = CreateService(
            protocol,
            CreateTailscaleStatus(peerOnline, peerCount));

        var exception = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            service.EnrollAsync(CreateRequest()));

        Assert.Contains("唯一对应", exception.Message);
        Assert.Equal(0, protocol.HealthCalls);
        Assert.Equal(0, protocol.EnrollCalls);
    }

    [Theory]
    [InlineData("other-box.example-tailnet.ts.net", Suffix)]
    [InlineData(Host, "other-tailnet.ts.net")]
    public async Task BootstrapIdentityMismatch_FailsBeforeKeyOrAuthorization(
        string serverHost,
        string suffix)
    {
        var protocol = new FakeProtocolClient
        {
            Bootstrap = CreateBootstrap(serverHost, suffix),
        };
        var identity = new FakeIdentityProvisioner();
        var service = CreateService(protocol, CreateTailscaleStatus(), identity);

        var exception = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            service.EnrollAsync(CreateRequest()));

        Assert.Contains("MagicDNS", exception.Message);
        Assert.Equal(0, identity.Calls);
        Assert.Equal(0, protocol.EnrollCalls);
    }

    [Fact]
    public async Task ReceiptPortsDifferentFromDefaultCatalog_FailsClosed()
    {
        var protocol = new FakeProtocolClient
        {
            Enrollment = CreateEnrollment([80, 8081]),
            Catalog = CreateCatalog(remotePort: 80),
        };
        var service = CreateService(protocol, CreateTailscaleStatus());

        var exception = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            service.EnrollAsync(CreateRequest()));

        Assert.Contains("精确端口不一致", exception.Message);
        Assert.Equal(1, protocol.EnrollCalls);
        Assert.Equal(1, protocol.CatalogCalls);
    }

    [Fact]
    public async Task IdentityFailure_DoesNotRequestAuthorizationOrProduceCredential()
    {
        var protocol = new FakeProtocolClient();
        var identity = new FakeIdentityProvisioner
        {
            Failure = new AgentEnrollmentException("identity rejected"),
        };
        var service = CreateService(protocol, CreateTailscaleStatus(), identity);

        var exception = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            service.EnrollAsync(CreateRequest()));

        Assert.Equal("identity rejected", exception.Message);
        Assert.Equal(0, protocol.EnrollCalls);
        Assert.Equal(0, protocol.CatalogCalls);
    }

    [Fact]
    public async Task AuthorizationFailure_DoesNotReturnDeviceCredential()
    {
        var protocol = new FakeProtocolClient
        {
            EnrollmentFailure = new AgentProtocolException(
                "recovery code rejected",
                "unauthorized",
                HttpStatusCode.Unauthorized),
        };
        var service = CreateService(protocol, CreateTailscaleStatus());

        var exception = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            service.EnrollAsync(CreateRequest()));

        Assert.Contains("登记码无效", exception.Message);
        Assert.Equal(1, protocol.EnrollCalls);
        Assert.Equal(0, protocol.CatalogCalls);
    }

    private static AgentEnrollmentService CreateService(
        FakeProtocolClient protocol,
        string tailscaleStatus,
        IAgentSshIdentityProvisioner? identity = null) =>
        new(
            new FakeProtocolClientFactory(protocol),
            identity ?? new FakeIdentityProvisioner(),
            new FakeCommandRunner(tailscaleStatus),
            () => "tailscale-fixture");

    private static JKBridge.App.Services.AgentEnrollmentRequest CreateRequest() => new(
        Endpoint,
        Credential(0).Export(),
        "Test Windows",
        @"C:\fixture\jk_bridge_ed25519");

    private static AgentBootstrapResponse CreateBootstrap(
        string host = Host,
        string suffix = Suffix) => new(
        AgentProtocol.Version,
        "test-agent",
        host,
        suffix,
        "100.64.0.10",
        "node-0",
        "alice",
        22,
        PublicKey,
        "Test Linux",
        true,
        "revision-1");

    private static AgentServerIdentity CreateServerIdentity() => new(
        AgentProtocol.Version,
        "test-agent",
        Host,
        Suffix,
        "100.64.0.10",
        "node-0",
        "alice",
        22,
        PublicKey,
        "Test Linux",
        "revision-1");

    private static AgentEnrollmentResponse CreateEnrollment(IReadOnlyList<int>? ports = null) => new(
        AgentProtocol.Version,
        CreateServerIdentity(),
        "0123456789abcdef0123456789abcdef",
        "Test Windows",
        "device",
        Credential(1),
        Fingerprint,
        ports ?? [80],
        "revision-1");

    private static AgentServiceCatalogResponse CreateCatalog(int remotePort) => new(
        AgentProtocol.Version,
        "revision-1",
        [
            new AgentServiceDefinition(
                "overleaf",
                "Overleaf",
                "LaTeX",
                "Document",
                true,
                true,
                8080,
                "127.0.0.1",
                remotePort,
                "http",
                "/project",
                "/project",
                AgentHealthKinds.HttpStatus),
        ]);

    private static AgentCredential Credential(byte fill) =>
        AgentCredential.Parse(Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));

    private static string CreateTailscaleStatus(bool online = true, int peerCount = 1)
    {
        var peers = Enumerable.Range(0, peerCount).ToDictionary(
            index => $"peer-{index}",
            index => (object)new
            {
                ID = $"node-{index}",
                HostName = "linux-box",
                DNSName = Host,
                Online = online,
                TailscaleIPs = new[] { "100.64.0.10" },
            });
        return JsonSerializer.Serialize(new
        {
            BackendState = "Running",
            CurrentTailnet = new
            {
                Name = "example",
                MagicDNSSuffix = Suffix,
                MagicDNSEnabled = true,
            },
            Self = new
            {
                ID = "windows-node",
                HostName = "windows",
                DNSName = "windows.example-tailnet.ts.net",
                Online = true,
                TailscaleIPs = new[] { "100.64.0.20" },
            },
            Peer = peers,
        });
    }

    private sealed class FakeProtocolClientFactory(FakeProtocolClient client)
        : IAgentProtocolClientFactory
    {
        public IAgentProtocolClient Create(AgentEndpoint endpoint) => client;
    }

    private sealed class FakeProtocolClient : IAgentProtocolClient
    {
        public AgentBootstrapResponse Bootstrap { get; init; } = CreateBootstrap();
        public AgentEnrollmentResponse Enrollment { get; init; } = CreateEnrollment();
        public AgentServiceCatalogResponse Catalog { get; init; } = CreateCatalog(80);
        public Exception? EnrollmentFailure { get; init; }
        public int HealthCalls { get; private set; }
        public int EnrollCalls { get; private set; }
        public int CatalogCalls { get; private set; }

        public Task<AgentHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            HealthCalls++;
            return Task.FromResult(new AgentHealthResponse(
                AgentProtocol.Version,
                "ok",
                "test-agent"));
        }

        public Task<AgentBootstrapResponse> GetBootstrapAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Bootstrap);

        public Task<AgentEnrollmentResponse> EnrollAsync(
            JKBridge.Core.Agent.AgentEnrollmentRequest request,
            AgentCredential enrollmentCredential,
            CancellationToken cancellationToken = default)
        {
            EnrollCalls++;
            return EnrollmentFailure is null
                ? Task.FromResult(Enrollment)
                : Task.FromException<AgentEnrollmentResponse>(EnrollmentFailure);
        }

        public Task<AgentServiceCatalogResponse> GetServiceCatalogAsync(
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default)
        {
            CatalogCalls++;
            return Task.FromResult(Catalog);
        }

        public Task<AgentCurrentDeviceResponse> GetCurrentDeviceAsync(
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentDeviceListResponse> GetDevicesAsync(
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentDeviceRevocationResponse> RevokeDeviceAsync(
            string deviceId,
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class FakeIdentityProvisioner : IAgentSshIdentityProvisioner
    {
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }

        public Task<AgentSshIdentity> PrepareAsync(
            string identityFile,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Failure is null
                ? Task.FromResult(new AgentSshIdentity(PublicKey, Fingerprint, identityFile))
                : Task.FromException<AgentSshIdentity>(Failure);
        }
    }

    private sealed class FakeCommandRunner(string statusJson) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new CommandResult(0, statusJson, string.Empty, false, TimeSpan.Zero));
    }
}
