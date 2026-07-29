using JKBridge.App.Services;
using JKBridge.Core.Agent;
using JKBridge.Core.Models;
using JKBridge.Core.Security;
using JKBridge.Core.Tailscale;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class AgentManagementServiceTests
{
    private const string Endpoint = "http://100.64.0.10:45871/";
    private const string LinuxNodeId = "node-linux";
    private const string WindowsNodeId = "node-windows";
    private const string DeviceId = "0123456789abcdef0123456789abcdef";
    private const string OtherDeviceId = "fedcba9876543210fedcba9876543210";
    private static readonly string Fingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey);

    [Fact]
    public async Task GetState_LoadsProtectedCredentialAndUsesDeviceServiceSelection()
    {
        var client = new FakeProtocolClient();
        var credentials = new FakeCredentialStore(Credential(1));
        var service = CreateService(client, credentials);

        var state = await service.GetStateAsync(CreateRequest());

        Assert.Equal(1, credentials.LoadCalls);
        Assert.Equal([80], state.PermitPorts);
        Assert.True(state.Services.Single(item => item.Id == "overleaf").Enabled);
        Assert.False(state.Services.Single(item => item.Id == "sub2api").Enabled);
        Assert.Equal("admin", state.CurrentDeviceRole);
        Assert.Equal(1, client.CurrentDeviceCalls);
        Assert.Equal(1, client.DeviceListCalls);
    }

    [Fact]
    public async Task GetState_AcceptsTailscaleLoginThatDiffersOnlyByCase()
    {
        var client = new FakeProtocolClient { CurrentTailscaleLogin = "mathjk@github" };
        var service = CreateService(
            client,
            new FakeCredentialStore(Credential(1)),
            localTailscaleLogin: "Mathjk@github");

        var state = await service.GetStateAsync(CreateRequest());

        Assert.Equal("admin", state.CurrentDeviceRole);
        Assert.Equal(1, client.DeviceListCalls);
    }

    [Fact]
    public async Task NonAdmin_GetStateUsesSelfReceiptWithoutRequestingAdminDeviceList()
    {
        var client = new FakeProtocolClient { CurrentRole = "device" };
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));

        var state = await service.GetStateAsync(CreateRequest());

        Assert.Equal("device", state.CurrentDeviceRole);
        Assert.Equal(DeviceId, Assert.Single(state.Devices).DeviceId);
        Assert.Equal([80], state.PermitPorts);
        Assert.Equal(1, client.CurrentDeviceCalls);
        Assert.Equal(0, client.DeviceListCalls);
    }

    [Fact]
    public async Task LinuxNodeChange_FailsBeforeCredentialIsLoaded()
    {
        var client = new FakeProtocolClient();
        var credentials = new FakeCredentialStore(Credential(1));
        var service = CreateService(
            client,
            credentials,
            trustedLinuxNodeId: "different-linux-node");

        var exception = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            service.GetStateAsync(CreateRequest()));

        Assert.Contains("登记时不同", exception.Message);
        Assert.Equal(0, credentials.LoadCalls);
        Assert.Equal(0, client.DeviceListCalls);
    }

    [Fact]
    public async Task MissingProtectedCredential_FailsBeforeAuthenticatedRequest()
    {
        var client = new FakeProtocolClient();
        var credentials = new FakeCredentialStore(null);
        var service = CreateService(client, credentials);

        var exception = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            service.GetStateAsync(CreateRequest()));

        Assert.Contains("凭据管理器", exception.Message);
        Assert.Equal(0, client.DeviceListCalls);
    }

    [Fact]
    public async Task RevokeCurrentDevice_IsRefusedWithoutCallingAgentRevoke()
    {
        var client = new FakeProtocolClient();
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));

        var exception = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            service.RevokeDeviceAsync(CreateRequest(), DeviceId));

        Assert.Contains("不允许撤销当前设备", exception.Message);
        Assert.Equal(0, client.RevokeCalls);
    }

    [Fact]
    public async Task RevokeOtherDevice_RequiresAdminStateAndReturnsReceipt()
    {
        var client = new FakeProtocolClient();
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));

        var receipt = await service.RevokeDeviceAsync(CreateRequest(), OtherDeviceId);

        Assert.True(receipt.Revoked);
        Assert.Equal(OtherDeviceId, receipt.DeviceId);
        Assert.Equal(1, client.RevokeCalls);
    }

    [Fact]
    public async Task RelayPlan_AdminRunsPlanAndProbeWithoutApplying()
    {
        var client = new FakeProtocolClient();
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));

        var state = await service.PlanRelayAsync(CreateRequest(), RelayRequest());

        Assert.Equal("create", state.Plan.RelayPlan.Action);
        Assert.True(state.Probe.ReadyToInstall);
        Assert.Equal(1, client.RelayPlanCalls);
        Assert.Equal(1, client.RelayProbeCalls);
        Assert.Equal(0, client.RelayApplyCalls);
    }

    [Fact]
    public async Task AdvancedActions_RejectNonAdminBeforeCallingPrivilegedApi()
    {
        var client = new FakeProtocolClient { CurrentRole = "device" };
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));

        var exception = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            service.CreateEnrollmentCodeAsync(CreateRequest(), 600));

        Assert.Contains("不是 Agent 管理员", exception.Message);
        Assert.Equal(0, client.EnrollmentCodeCalls);
        Assert.Equal(0, client.RelayPlanCalls);
    }

    [Fact]
    public async Task RelayApplyAndDelete_SendExplicitConfirmedRequests()
    {
        var client = new FakeProtocolClient();
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));
        var planToken = AgentRelayPlanToken.Parse(new string('a', 64));

        await service.ApplyRelayAsync(CreateRequest(), RelayRequest(), planToken);
        await service.DeleteRelayAsync(CreateRequest(), "campus-auth", planToken);

        Assert.True(client.LastRelayApplyRequest?.Confirmed);
        Assert.True(client.LastRelayDeletionRequest?.Confirmed);
        Assert.Equal(1, client.RelayApplyCalls);
        Assert.Equal(1, client.RelayDeleteCalls);
    }

    [Fact]
    public async Task EnrollmentCode_AdminGetsTransientSecretReceipt()
    {
        var client = new FakeProtocolClient();
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));

        var receipt = await service.CreateEnrollmentCodeAsync(CreateRequest(), 600);

        Assert.Equal(new string('E', 43), receipt.EnrollmentCode.Export());
        Assert.Equal(1, client.EnrollmentCodeCalls);
    }

    [Fact]
    public async Task CatalogPreview_ComputesDiffAndAffectedDevicesWithoutPut()
    {
        var client = new FakeProtocolClient();
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));
        var proposed = new[]
        {
            AgentService("overleaf", 81, enabledByDefault: true),
            AgentService("campus", 19000, enabledByDefault: true),
        };

        var preview = await service.PreviewServiceCatalogUpdateAsync(
            CreateRequest(), proposed, applyDefaultsToAllDevices: true);

        Assert.Equal(["campus"], preview.AddedServiceIds);
        Assert.Equal(["overleaf"], preview.ModifiedServiceIds);
        Assert.Equal(["sub2api"], preview.RemovedServiceIds);
        Assert.Equal([81, 19000], preview.AddedRemotePorts);
        Assert.Equal([80, 8081], preview.RemovedRemotePorts);
        Assert.Equal(2, preview.AffectedDeviceIds.Count);
        Assert.True(preview.CanApply);
        Assert.Equal(0, client.CatalogUpdateCalls);
    }

    [Fact]
    public async Task CatalogApply_UsesPreviewRevisionAndExplicitDefaultScope()
    {
        var client = new FakeProtocolClient();
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));
        var proposed = new[]
        {
            AgentService("overleaf", 80, enabledByDefault: true),
            AgentService("sub2api", 8081, enabledByDefault: true),
        };
        var preview = await service.PreviewServiceCatalogUpdateAsync(
            CreateRequest(), proposed, applyDefaultsToAllDevices: true);

        var receipt = await service.ApplyServiceCatalogUpdateAsync(CreateRequest(), preview);

        Assert.Equal("2", receipt.ServicesRevision);
        Assert.Equal(1, client.CatalogUpdateCalls);
        Assert.Equal("1", client.LastCatalogExpectedRevision);
        Assert.True(client.LastCatalogApplyDefaults);
        Assert.Equal(proposed, client.LastCatalogServices);
    }

    [Fact]
    public async Task RenameDevice_RequiresAdminAndReturnsServerReceipt()
    {
        var client = new FakeProtocolClient();
        var service = CreateService(client, new FakeCredentialStore(Credential(1)));

        var receipt = await service.RenameDeviceAsync(
            CreateRequest(), OtherDeviceId, "Travel laptop");

        Assert.True(receipt.Renamed);
        Assert.Equal("Travel laptop", receipt.Device.DeviceLabel);
        Assert.Equal(1, client.RenameCalls);
    }

    [Fact]
    public async Task EndpointRebind_ReverifiesAuthenticatedCurrentDeviceWithoutChangingIdentity()
    {
        var client = new FakeProtocolClient();
        var credentials = new FakeCredentialStore(Credential(1));
        var service = CreateService(client, credentials);

        var verification = await service.VerifyEndpointRebindAsync(
            CreateRequest(), "http://100.64.0.11:45871/");

        Assert.Equal("http://100.64.0.11:45871/", verification.Endpoint);
        Assert.Equal("1", verification.CatalogRevision);
        Assert.Equal(1, credentials.LoadCalls);
        Assert.Equal(1, client.CurrentDeviceCalls);
    }

    private static AgentServiceDefinition AgentService(
        string id,
        int remotePort,
        bool enabledByDefault) => new(
        id,
        id,
        string.Empty,
        "Globe",
        enabledByDefault,
        false,
        remotePort,
        "127.0.0.1",
        remotePort,
        "http",
        "/",
        "/",
        AgentHealthKinds.HttpStatus);

    private static AgentRelayPlanRequest RelayRequest() => new(
        new AgentRelaySpec(
            "campus-auth", 19000, "auth.example.test", 443, "https", "/#/login"),
        new AgentRelayServicePresentation(
            "Campus Auth", "Campus target", "Globe", 19000, "/",
            AgentHealthKinds.TcpConnect, true),
        GrantToAllDevices: true);

    private static AgentManagementService CreateService(
        FakeProtocolClient client,
        FakeCredentialStore credentials,
        string trustedLinuxNodeId = LinuxNodeId,
        string localTailscaleLogin = "owner@example.com") => new(
        new FakeProtocolClientFactory(client),
        credentials,
        new FakeTrustVerifier(trustedLinuxNodeId, localTailscaleLogin));

    private static AgentManagementRequest CreateRequest() => new(
        new AgentRegistration
        {
            IsConfigured = true,
            Endpoint = Endpoint,
            TailscaleNodeId = LinuxNodeId,
            DeviceId = DeviceId,
            DeviceLabel = "Current Windows",
            CredentialReference = "agent-fixture",
            CatalogRevision = "1",
        },
        new ServerProfile
        {
            DisplayName = "Linux",
            Host = TestConfigs.LinuxHost,
            User = "alice",
            Port = 22,
            PinnedHostKey = TestConfigs.TestHostKey,
        },
        new ForwardAuthorizationGrant
        {
            IsKnown = true,
            ClientKeyFingerprint = Fingerprint,
            RemotePorts = [80],
        },
        TestConfigs.TailnetSuffix);

    private static AgentBootstrapResponse Bootstrap() => new(
        AgentProtocol.Version,
        "test-agent",
        TestConfigs.LinuxHost,
        TestConfigs.TailnetSuffix,
        "100.64.0.10",
        LinuxNodeId,
        "alice",
        22,
        TestConfigs.TestHostKey,
        "Linux",
        true,
        "1");

    private static AgentCredential Credential(byte fill) =>
        AgentCredential.Parse(Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));

    private sealed class FakeTrustVerifier(
        string linuxNodeId,
        string localTailscaleLogin) : IAgentTrustVerifier
    {
        public Task<AgentTrustedEndpoint> VerifyEndpointAsync(
            AgentEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            var peer = new TailscaleNode(
                linuxNodeId,
                "linux-box",
                TestConfigs.LinuxHost,
                true,
                ["100.64.0.10"]);
            var self = new TailscaleNode(
                WindowsNodeId,
                "windows",
                TestConfigs.WindowsHost,
                true,
                ["100.64.0.20"]);
            var status = new TailscaleStatusSnapshot(
                "Running",
                new TailscaleTailnet("example", TestConfigs.TailnetSuffix, true),
                self,
                [peer],
                localTailscaleLogin);
            return Task.FromResult(new AgentTrustedEndpoint(status, peer));
        }

        public void VerifyBootstrap(
            AgentBootstrapResponse bootstrap,
            AgentTrustedEndpoint trustedEndpoint)
        {
        }
    }

    private sealed class FakeCredentialStore(AgentCredential? credential) : IAgentCredentialStore
    {
        public int LoadCalls { get; private set; }

        public Task<string> SaveAsync(
            string endpoint,
            string deviceId,
            AgentCredential value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentCredential?> LoadAsync(
            string credentialReference,
            CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.FromResult(credential);
        }

        public Task DeleteAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeProtocolClientFactory(FakeProtocolClient client)
        : IAgentProtocolClientFactory
    {
        public IAgentProtocolClient Create(AgentEndpoint endpoint) => client;
    }

    private sealed class FakeProtocolClient : IAgentProtocolClient
    {
        public string CurrentRole { get; init; } = "admin";
        public string CurrentTailscaleLogin { get; init; } = "owner@example.com";
        public int CurrentDeviceCalls { get; private set; }
        public int DeviceListCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public int EnrollmentCodeCalls { get; private set; }
        public int RelayPlanCalls { get; private set; }
        public int RelayProbeCalls { get; private set; }
        public int RelayApplyCalls { get; private set; }
        public int RelayDeleteCalls { get; private set; }
        public int CatalogUpdateCalls { get; private set; }
        public int RenameCalls { get; private set; }
        public string? LastCatalogExpectedRevision { get; private set; }
        public bool LastCatalogApplyDefaults { get; private set; }
        public IReadOnlyList<AgentServiceDefinition>? LastCatalogServices { get; private set; }
        public AgentRelayApplyRequest? LastRelayApplyRequest { get; private set; }
        public AgentRelayDeletionRequest? LastRelayDeletionRequest { get; private set; }

        public Task<AgentHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentHealthResponse(AgentProtocol.Version, "ok", "test-agent"));

        public Task<AgentBootstrapResponse> GetBootstrapAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Bootstrap());

        public Task<AgentEnrollmentResponse> EnrollAsync(
            JKBridge.Core.Agent.AgentEnrollmentRequest request,
            AgentCredential enrollmentCredential,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AgentServiceCatalogResponse> GetServiceCatalogAsync(
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new AgentServiceCatalogResponse(
                AgentProtocol.Version,
                "1",
                [
                    new AgentServiceDefinition(
                        "overleaf", "Overleaf", "LaTeX", "Document",
                        true, true, 8080, "127.0.0.1", 80,
                        "http", "/project", "/project", AgentHealthKinds.HttpStatus),
                    new AgentServiceDefinition(
                        "sub2api", "Sub2API", "API", "Globe",
                        true, true, 8081, "127.0.0.1", 8081,
                        "http", "/", "/", AgentHealthKinds.HttpStatus),
                ]));

        public Task<AgentServiceCatalogResponse> UpdateServiceCatalogAsync(
            string expectedRevision,
            IReadOnlyList<AgentServiceDefinition> services,
            bool applyDefaultsToAllDevices,
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default)
        {
            CatalogUpdateCalls++;
            LastCatalogExpectedRevision = expectedRevision;
            LastCatalogApplyDefaults = applyDefaultsToAllDevices;
            LastCatalogServices = services;
            return Task.FromResult(new AgentServiceCatalogResponse(
                AgentProtocol.Version,
                "2",
                services));
        }

        public Task<AgentCurrentDeviceResponse> GetCurrentDeviceAsync(
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default)
        {
            CurrentDeviceCalls++;
            return Task.FromResult(new AgentCurrentDeviceResponse(
                AgentProtocol.Version,
                "1",
                Device(
                    DeviceId,
                    "Current Windows",
                    WindowsNodeId,
                    CurrentRole,
                    ["overleaf"],
                    [80])));
        }

        public Task<AgentDeviceListResponse> GetDevicesAsync(
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default)
        {
            DeviceListCalls++;
            return Task.FromResult(new AgentDeviceListResponse(
                AgentProtocol.Version,
                [
                    Device(DeviceId, "Current Windows", WindowsNodeId, CurrentRole, ["overleaf"], [80]),
                    Device(OtherDeviceId, "Other Windows", "node-other", "device", ["overleaf"], [80]),
                ]));
        }

        public Task<AgentDeviceRevocationResponse> RevokeDeviceAsync(
            string deviceId,
            AgentCredential deviceCredential,
            CancellationToken cancellationToken = default)
        {
            RevokeCalls++;
            return Task.FromResult(new AgentDeviceRevocationResponse(
                AgentProtocol.Version,
                deviceId,
                true,
                DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
                "revoked"));
        }

        public Task<AgentDeviceRenameResponse> RenameDeviceAsync(
            string deviceId,
            string deviceLabel,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            RenameCalls++;
            return Task.FromResult(new AgentDeviceRenameResponse(
                AgentProtocol.Version,
                true,
                "Other Windows",
                Device(
                    deviceId,
                    deviceLabel,
                    "node-other",
                    "device",
                    ["overleaf"],
                    [80])));
        }

        public Task<AgentEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
            int ttlSeconds,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            EnrollmentCodeCalls++;
            return Task.FromResult(new AgentEnrollmentCodeResponse(
                AgentProtocol.Version,
                AgentCredential.Parse(new string('E', 43)),
                DateTimeOffset.Parse("2026-07-29T12:10:00Z")));
        }

        public Task<AgentRelayPlanResponse> PlanRelayAsync(
            AgentRelayPlanRequest request,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            RelayPlanCalls++;
            return Task.FromResult(RelayPlan("install", "create", request.Spec));
        }

        public Task<AgentRelayProbeResponse> ProbeRelayAsync(
            AgentRelaySpec spec,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            RelayProbeCalls++;
            return Task.FromResult(new AgentRelayProbeResponse(
                AgentProtocol.Version,
                spec,
                true,
                false,
                true,
                true,
                string.Empty));
        }

        public Task<AgentRelayApplyResponse> ApplyRelayAsync(
            AgentRelayApplyRequest request,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            RelayApplyCalls++;
            LastRelayApplyRequest = request;
            return Task.FromResult(new AgentRelayApplyResponse(
                RelayPlan("install", "create", request.Plan.Spec, request.PlanToken),
                Applied: true,
                BackupCreated: false));
        }

        public Task<AgentRelayPlanResponse> PlanRelayDeletionAsync(
            AgentRelayDeletionPlanRequest request,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RelayPlan(
                "delete",
                "delete",
                RelayRequest().Spec with { Slug = request.Slug }));

        public Task<AgentRelayApplyResponse> DeleteRelayAsync(
            AgentRelayDeletionRequest request,
            AgentCredential adminCredential,
            CancellationToken cancellationToken = default)
        {
            RelayDeleteCalls++;
            LastRelayDeletionRequest = request;
            return Task.FromResult(new AgentRelayApplyResponse(
                RelayPlan(
                    "delete",
                    "delete",
                    RelayRequest().Spec with { Slug = request.Slug },
                    request.PlanToken),
                Applied: true,
                BackupCreated: true));
        }

        public void Dispose()
        {
        }

        private AgentDeviceInfo Device(
            string id,
            string label,
            string nodeId,
            string role,
            IReadOnlyList<string> services,
            IReadOnlyList<int> ports) => new(
            id,
            label,
            Fingerprint,
            role,
            nodeId,
            CurrentTailscaleLogin,
            services,
            ports,
            DateTimeOffset.Parse("2026-07-28T12:00:00Z"),
            DateTimeOffset.Parse("2026-07-29T11:00:00Z"),
            null);

        private static AgentRelayPlanResponse RelayPlan(
            string operation,
            string action,
            AgentRelaySpec spec,
            AgentRelayPlanToken? token = null) => new(
            AgentProtocol.Version,
            token ?? AgentRelayPlanToken.Parse(new string('a', 64)),
            new AgentRelayPlan(
                operation,
                action,
                spec.Slug,
                $"jkbridge-relay-{spec.Slug}.service",
                spec,
                null,
                "ready",
                true,
                true),
            new AgentRelayServiceChange([spec.RelayPort], [], [OtherDeviceId]),
            "2");
    }
}
