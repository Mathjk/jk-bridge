using JKBridge.App.Platform;
using JKBridge.App.Services;
using JKBridge.App.ViewModels;
using JKBridge.Core.Agent;
using JKBridge.Core.Configuration;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Models;
using JKBridge.Core.Security;
using JKBridge.Core.Tunneling;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class AppControllerCatalogUpdateTests
{
    private const string DeviceId = "0123456789abcdef0123456789abcdef";
    private static readonly string Fingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey);

    [Fact]
    public async Task PreviewIsReadOnly_ThenExplicitConfirmationPutsRefreshesAndPersists()
    {
        await using var fixture = await ControllerFixture.CreateAsync();

        await fixture.Controller.PreviewAgentCatalogUpdateAsync(applyDefaultsToAllDevices: true);

        Assert.Equal(1, fixture.Management.PreviewCalls);
        Assert.Equal(0, fixture.Management.ApplyCalls);
        Assert.Equal("1", fixture.Controller.CurrentConfig.Agent.CatalogRevision);

        await fixture.Controller.ViewModel.ApplyAgentCatalogUpdateCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Management.ApplyCalls);

        fixture.Controller.ViewModel.AgentCatalogUpdateConfirmed = true;
        await fixture.Controller.ViewModel.ApplyAgentCatalogUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Management.ApplyCalls);
        Assert.Equal(1, fixture.Management.StateCalls);
        Assert.Equal("2", fixture.Controller.CurrentConfig.Agent.CatalogRevision);
        Assert.True(fixture.Controller.CurrentConfig.Authorization.IsKnown);
        Assert.Equal([80, 8765, 8793], fixture.Controller.CurrentConfig.Authorization.RemotePorts);
        var durable = await fixture.ConfigStore.LoadAsync();
        Assert.Equal("2", durable.Agent.CatalogRevision);
    }

    [Fact]
    public async Task PutFailure_DoesNotChangeLocalPersistentConfiguration()
    {
        await using var fixture = await ControllerFixture.CreateAsync(applyFailure: true);
        await fixture.Controller.PreviewAgentCatalogUpdateAsync(applyDefaultsToAllDevices: true);
        fixture.Controller.ViewModel.AgentCatalogUpdateConfirmed = true;

        await fixture.Controller.ViewModel.ApplyAgentCatalogUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Management.ApplyCalls);
        Assert.Equal(0, fixture.Management.StateCalls);
        Assert.Equal("1", fixture.Controller.CurrentConfig.Agent.CatalogRevision);
        var durable = await fixture.ConfigStore.LoadAsync();
        Assert.Equal("1", durable.Agent.CatalogRevision);
        Assert.False(fixture.Controller.ViewModel.HasAgentCatalogUpdatePreview);
    }

    [Fact]
    public async Task EndpointRebind_RequiresStableNodeMatchAndPreservesDeviceIdentity()
    {
        await using var fixture = await ControllerFixture.CreateAsync();
        var original = fixture.Controller.CurrentConfig;
        var mismatched = new AgentDiscoveryCandidateUiState(
            "http://100.64.0.20:45871/",
            "Other Linux",
            "other.tail.example.ts.net",
            "2.2.0",
            true,
            "node-other");

        await fixture.Controller.RebindAgentEndpointAsync(mismatched, confirmed: true);

        Assert.Equal(0, fixture.Management.RebindCalls);
        Assert.Equal(original.Agent.Endpoint, fixture.Controller.CurrentConfig.Agent.Endpoint);

        var matching = mismatched with
        {
            DisplayName = "Current Linux",
            ServerHost = "current.tail.example.ts.net",
            TailscaleNodeId = original.Agent.TailscaleNodeId,
        };
        await fixture.Controller.RebindAgentEndpointAsync(matching, confirmed: true);

        Assert.Equal(1, fixture.Management.RebindCalls);
        var rebound = fixture.Controller.CurrentConfig;
        Assert.Equal(matching.Endpoint, rebound.Agent.Endpoint);
        Assert.Equal("2", rebound.Agent.CatalogRevision);
        Assert.Equal(original.Agent.DeviceId, rebound.Agent.DeviceId);
        Assert.Equal(original.Agent.CredentialReference, rebound.Agent.CredentialReference);
        Assert.Equal(original.Server.IdentityFile, rebound.Server.IdentityFile);
        Assert.Equal(original.Server.PinnedHostKey, rebound.Server.PinnedHostKey);

        var durable = await fixture.ConfigStore.LoadAsync();
        Assert.Equal(matching.Endpoint, durable.Agent.Endpoint);
        Assert.Equal(original.Agent.DeviceId, durable.Agent.DeviceId);
        Assert.Equal(original.Agent.CredentialReference, durable.Agent.CredentialReference);
    }

    [Fact]
    public async Task EndpointRebind_SaveFailureKeepsOldEndpointAndCredential()
    {
        await using var fixture = await ControllerFixture.CreateAsync();
        var original = fixture.Controller.CurrentConfig;
        var preservedPath = fixture.ConfigStore.ConfigPath + ".preserved";
        File.Move(fixture.ConfigStore.ConfigPath, preservedPath);
        Directory.CreateDirectory(fixture.ConfigStore.ConfigPath);
        var matching = new AgentDiscoveryCandidateUiState(
            "http://100.64.0.20:45871/",
            "Current Linux",
            "current.tail.example.ts.net",
            "2.2.0",
            true,
            original.Agent.TailscaleNodeId);

        await fixture.Controller.RebindAgentEndpointAsync(matching, confirmed: true);

        Assert.Equal(1, fixture.Management.RebindCalls);
        Assert.Equal(original.Agent.Endpoint, fixture.Controller.CurrentConfig.Agent.Endpoint);
        Assert.Equal(
            original.Agent.CredentialReference,
            fixture.Controller.CurrentConfig.Agent.CredentialReference);
        using var preservedStore = new ConfigStore(preservedPath);
        var durable = await preservedStore.LoadAsync();
        Assert.Equal(original.Agent.Endpoint, durable.Agent.Endpoint);
        Assert.Equal(original.Agent.CredentialReference, durable.Agent.CredentialReference);
    }

    private static AppConfig AgentConfig()
    {
        var config = TestConfigs.CreateConfigured();
        return config with
        {
            Behavior = config.Behavior with { AutoConnect = false },
            Authorization = config.Authorization with
            {
                ClientKeyFingerprint = Fingerprint,
            },
            Agent = new AgentRegistration
            {
                IsConfigured = true,
                Endpoint = "http://100.64.0.10:45871/",
                TailscaleNodeId = "node-linux",
                DeviceId = DeviceId,
                DeviceLabel = "Current Windows",
                CredentialReference = "agent-" + new string('a', 64),
                CatalogRevision = "1",
            },
        };
    }

    private sealed class ControllerFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private ControllerFixture(
            string directory,
            ConfigStore configStore,
            AppController controller,
            FakeManagementService management)
        {
            _directory = directory;
            ConfigStore = configStore;
            Controller = controller;
            Management = management;
        }

        public ConfigStore ConfigStore { get; }
        public AppController Controller { get; }
        public FakeManagementService Management { get; }

        public static async Task<ControllerFixture> CreateAsync(bool applyFailure = false)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"jkbridge-catalog-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var configStore = new ConfigStore(Path.Combine(directory, "config.json"));
            await configStore.SaveAsync(AgentConfig());
            var management = new FakeManagementService(applyFailure);
            var controller = new AppController(
                configStore,
                new RollingFileLogger(Path.Combine(directory, "logs")),
                new FakeRuntime(),
                new FakePairingService(),
                new StartupRegistration($"JK Bridge Test {Guid.NewGuid():N}"),
                Path.Combine(directory, "logs"),
                () => null,
                agentManagementService: management,
                dispatchUi: action => action());
            await controller.InitializeAsync();
            return new ControllerFixture(directory, configStore, controller, management);
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

    private sealed class FakeManagementService(bool applyFailure) : IAgentManagementService
    {
        private AgentCatalogUpdatePreview? _preview;

        public int PreviewCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int StateCalls { get; private set; }
        public int RebindCalls { get; private set; }

        public Task<AgentCatalogUpdatePreview> PreviewServiceCatalogUpdateAsync(
            AgentManagementRequest request,
            IReadOnlyList<AgentServiceDefinition> proposedServices,
            bool applyDefaultsToAllDevices,
            CancellationToken cancellationToken = default)
        {
            PreviewCalls++;
            _preview = new AgentCatalogUpdatePreview(
                "1",
                proposedServices,
                applyDefaultsToAllDevices,
                ["overleaf"],
                [],
                [],
                [],
                [],
                [DeviceId],
                true,
                string.Empty);
            return Task.FromResult(_preview);
        }

        public Task<AgentServiceCatalogResponse> ApplyServiceCatalogUpdateAsync(
            AgentManagementRequest request,
            AgentCatalogUpdatePreview preview,
            CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            if (applyFailure)
            {
                return Task.FromException<AgentServiceCatalogResponse>(
                    new AgentEnrollmentException("Linux 拒绝了测试更新。"));
            }
            return Task.FromResult(new AgentServiceCatalogResponse(
                AgentProtocol.Version,
                "2",
                preview.ProposedServices));
        }

        public Task<AgentManagementState> GetStateAsync(
            AgentManagementRequest request,
            CancellationToken cancellationToken = default)
        {
            StateCalls++;
            var services = (_preview?.ProposedServices ?? [])
                .Select(service => service.ToServiceDefinition())
                .ToArray();
            return Task.FromResult(new AgentManagementState(
                "2",
                services,
                services.Where(service => service.Enabled)
                    .Select(service => service.RemotePort).Distinct().Order().ToArray(),
                [new AgentDeviceInfo(
                    DeviceId,
                    "Current Windows",
                    Fingerprint,
                    "admin",
                    "node-windows",
                    "owner@example.com",
                    services.Where(service => service.Enabled).Select(service => service.Id).ToArray(),
                    services.Where(service => service.Enabled).Select(service => service.RemotePort).Distinct().Order().ToArray(),
                    DateTimeOffset.Parse("2026-07-28T12:00:00Z"),
                    DateTimeOffset.Parse("2026-07-29T11:00:00Z"),
                    null)],
                "admin"));
        }

        public Task<AgentDeviceRevocationResponse> RevokeDeviceAsync(
            AgentManagementRequest request,
            string deviceId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentDeviceRevocationResponse>(new NotSupportedException());

        public Task<AgentDeviceRenameResponse> RenameDeviceAsync(
            AgentManagementRequest request,
            string deviceId,
            string deviceLabel,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentDeviceRenameResponse>(new NotSupportedException());

        public Task<AgentEndpointRebindVerification> VerifyEndpointRebindAsync(
            AgentManagementRequest request,
            string endpoint,
            CancellationToken cancellationToken = default)
        {
            RebindCalls++;
            return Task.FromResult(new AgentEndpointRebindVerification(endpoint, "2"));
        }

        public Task<AgentEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
            AgentManagementRequest request,
            int ttlSeconds,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentEnrollmentCodeResponse>(new NotSupportedException());

        public Task<AgentRelayPlanningState> PlanRelayAsync(
            AgentManagementRequest request,
            AgentRelayPlanRequest relay,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentRelayPlanningState>(new NotSupportedException());

        public Task<AgentRelayPlanResponse> PlanRelayDeletionAsync(
            AgentManagementRequest request,
            string slug,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentRelayPlanResponse>(new NotSupportedException());

        public Task<AgentRelayApplyResponse> ApplyRelayAsync(
            AgentManagementRequest request,
            AgentRelayPlanRequest relay,
            AgentRelayPlanToken planToken,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentRelayApplyResponse>(new NotSupportedException());

        public Task<AgentRelayApplyResponse> DeleteRelayAsync(
            AgentManagementRequest request,
            string slug,
            AgentRelayPlanToken planToken,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentRelayApplyResponse>(new NotSupportedException());
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
        public Task StartAsync(AppConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(AppConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(AppConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetHealthCheckInterval(TimeSpan interval)
        {
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
