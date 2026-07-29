using JKBridge.Core.Agent;

namespace JKBridge.App.Services;

/// <summary>
/// Production adapter for the bounded Core Agent client. AgentClient itself enforces no proxy,
/// no redirects, exact endpoint origin, bounded JSON, and redacted credentials.
/// </summary>
public sealed class AgentProtocolClientFactory : IAgentProtocolClientFactory
{
    public IAgentProtocolClient Create(AgentEndpoint endpoint) =>
        new AgentProtocolClientAdapter(new AgentClient(endpoint));
}

internal sealed class AgentProtocolClientAdapter : IAgentProtocolClient
{
    private readonly AgentClient _client;

    public AgentProtocolClientAdapter(AgentClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<AgentHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        _client.GetHealthAsync(cancellationToken);

    public Task<AgentBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken = default) =>
        _client.GetBootstrapAsync(cancellationToken);

    public Task<AgentEnrollmentResponse> EnrollAsync(
        JKBridge.Core.Agent.AgentEnrollmentRequest request,
        AgentCredential enrollmentCredential,
        CancellationToken cancellationToken = default) =>
        _client.EnrollAsync(request, enrollmentCredential, cancellationToken);

    public Task<AgentServiceCatalogResponse> GetServiceCatalogAsync(
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default) =>
        _client.GetServiceCatalogAsync(deviceCredential, cancellationToken);

    public Task<AgentServiceCatalogResponse> UpdateServiceCatalogAsync(
        string expectedRevision,
        IReadOnlyList<AgentServiceDefinition> services,
        bool applyDefaultsToAllDevices,
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default) =>
        _client.UpdateServiceCatalogAsync(
            expectedRevision,
            services,
            applyDefaultsToAllDevices,
            deviceCredential,
            cancellationToken);

    public Task<AgentCurrentDeviceResponse> GetCurrentDeviceAsync(
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default) =>
        _client.GetCurrentDeviceAsync(deviceCredential, cancellationToken);

    public Task<AgentDeviceListResponse> GetDevicesAsync(
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default) =>
        _client.GetDevicesAsync(deviceCredential, cancellationToken);

    public Task<AgentDeviceRevocationResponse> RevokeDeviceAsync(
        string deviceId,
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default) =>
        _client.RevokeDeviceAsync(deviceId, deviceCredential, cancellationToken);

    public Task<AgentDeviceRenameResponse> RenameDeviceAsync(
        string deviceId,
        string deviceLabel,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        _client.RenameDeviceAsync(deviceId, deviceLabel, adminCredential, cancellationToken);

    public Task<AgentEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
        int ttlSeconds,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        _client.CreateEnrollmentCodeAsync(ttlSeconds, adminCredential, cancellationToken);

    public Task<AgentRelayPlanResponse> PlanRelayAsync(
        AgentRelayPlanRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        _client.PlanRelayAsync(request, adminCredential, cancellationToken);

    public Task<AgentRelayProbeResponse> ProbeRelayAsync(
        AgentRelaySpec spec,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        _client.ProbeRelayAsync(spec, adminCredential, cancellationToken);

    public Task<AgentRelayApplyResponse> ApplyRelayAsync(
        AgentRelayApplyRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        _client.ApplyRelayAsync(request, adminCredential, cancellationToken);

    public Task<AgentRelayPlanResponse> PlanRelayDeletionAsync(
        AgentRelayDeletionPlanRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        _client.PlanRelayDeletionAsync(request, adminCredential, cancellationToken);

    public Task<AgentRelayApplyResponse> DeleteRelayAsync(
        AgentRelayDeletionRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        _client.DeleteRelayAsync(request, adminCredential, cancellationToken);

    public void Dispose() => _client.Dispose();
}
