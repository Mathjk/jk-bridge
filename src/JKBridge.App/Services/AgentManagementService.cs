using JKBridge.Core.Agent;
using JKBridge.Core.Models;

namespace JKBridge.App.Services;

public sealed record AgentManagementRequest(
    AgentRegistration Registration,
    ServerProfile Server,
    ForwardAuthorizationGrant Authorization,
    string ExpectedTailnetDnsSuffix);

public sealed record AgentManagementState(
    string CatalogRevision,
    IReadOnlyList<ServiceDefinition> Services,
    IReadOnlyList<int> PermitPorts,
    IReadOnlyList<AgentDeviceInfo> Devices,
    string CurrentDeviceRole);

public sealed record AgentRelayPlanningState(
    AgentRelayPlanRequest Request,
    AgentRelayPlanResponse Plan,
    AgentRelayProbeResponse Probe);

public sealed record AgentCatalogUpdatePreview(
    string ExpectedRevision,
    IReadOnlyList<AgentServiceDefinition> ProposedServices,
    bool ApplyDefaultsToAllDevices,
    IReadOnlyList<string> AddedServiceIds,
    IReadOnlyList<string> ModifiedServiceIds,
    IReadOnlyList<string> RemovedServiceIds,
    IReadOnlyList<int> AddedRemotePorts,
    IReadOnlyList<int> RemovedRemotePorts,
    IReadOnlyList<string> AffectedDeviceIds,
    bool CanApply,
    string BlockingReason);

public sealed record AgentEndpointRebindVerification(
    string Endpoint,
    string CatalogRevision);

public interface IAgentManagementService
{
    Task<AgentManagementState> GetStateAsync(
        AgentManagementRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentLoopbackDiscoveryResponse> DiscoverLoopbackServicesAsync(
        AgentManagementRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentLoopbackDiscoveryResponse>(new NotSupportedException());

    Task<AgentDeviceRevocationResponse> RevokeDeviceAsync(
        AgentManagementRequest request,
        string deviceId,
        CancellationToken cancellationToken = default);

    Task<AgentDeviceRenameResponse> RenameDeviceAsync(
        AgentManagementRequest request,
        string deviceId,
        string deviceLabel,
        CancellationToken cancellationToken = default);

    Task<AgentEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
        AgentManagementRequest request,
        int ttlSeconds,
        CancellationToken cancellationToken = default);

    Task<AgentRelayPlanningState> PlanRelayAsync(
        AgentManagementRequest request,
        AgentRelayPlanRequest relay,
        CancellationToken cancellationToken = default);

    Task<AgentRelayPlanResponse> PlanRelayDeletionAsync(
        AgentManagementRequest request,
        string slug,
        CancellationToken cancellationToken = default);

    Task<AgentRelayApplyResponse> ApplyRelayAsync(
        AgentManagementRequest request,
        AgentRelayPlanRequest relay,
        AgentRelayPlanToken planToken,
        CancellationToken cancellationToken = default);

    Task<AgentRelayApplyResponse> DeleteRelayAsync(
        AgentManagementRequest request,
        string slug,
        AgentRelayPlanToken planToken,
        CancellationToken cancellationToken = default);

    Task<AgentCatalogUpdatePreview> PreviewServiceCatalogUpdateAsync(
        AgentManagementRequest request,
        IReadOnlyList<AgentServiceDefinition> proposedServices,
        bool applyDefaultsToAllDevices,
        CancellationToken cancellationToken = default);

    Task<AgentServiceCatalogResponse> ApplyServiceCatalogUpdateAsync(
        AgentManagementRequest request,
        AgentCatalogUpdatePreview preview,
        CancellationToken cancellationToken = default);

    Task<AgentEndpointRebindVerification> VerifyEndpointRebindAsync(
        AgentManagementRequest request,
        string endpoint,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authenticated Agent management for an already enrolled Windows device. Every operation repeats
/// the local Tailscale node check before loading and sending the OS-protected device credential.
/// </summary>
public sealed class AgentManagementService : IAgentManagementService
{
    private readonly IAgentProtocolClientFactory _clientFactory;
    private readonly IAgentCredentialStore _credentialStore;
    private readonly IAgentTrustVerifier _trustVerifier;

    public AgentManagementService(
        IAgentProtocolClientFactory clientFactory,
        IAgentCredentialStore credentialStore,
        IAgentTrustVerifier? trustVerifier = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _trustVerifier = trustVerifier ?? new AgentTrustVerifier();
    }

    public async Task<AgentManagementState> GetStateAsync(
        AgentManagementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = await OpenVerifiedAsync(request, cancellationToken).ConfigureAwait(false);
        using (context.Client)
        {
            try
            {
                var current = await context.Client.GetCurrentDeviceAsync(
                        context.Credential,
                        cancellationToken)
                    .ConfigureAwait(false);
                var catalog = await context.Client.GetServiceCatalogAsync(context.Credential, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        context.Bootstrap.CatalogRevision,
                        catalog.ServicesRevision,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        current.ServicesRevision,
                        catalog.ServicesRevision,
                        StringComparison.Ordinal))
                {
                    throw new AgentEnrollmentException(
                        "读取期间 Linux 服务清单发生变化，请重新同步。");
                }

                var currentDevice = current.Device;
                VerifyCurrentDevice(request, context, currentDevice);

                var enabledIds = currentDevice.ServiceIds.ToHashSet(StringComparer.Ordinal);
                var services = catalog.Services
                    .Select(service => service.ToServiceDefinition() with
                    {
                        Enabled = enabledIds.Contains(service.Id),
                    })
                    .ToArray();
                if (enabledIds.Any(id => services.All(service =>
                        !string.Equals(service.Id, id, StringComparison.Ordinal))))
                {
                    throw new AgentEnrollmentException(
                        "Agent 设备授权引用了服务清单中不存在的服务；同步已停止。");
                }

                var expectedPorts = services
                    .Where(service => service.Enabled)
                    .Select(service => service.RemotePort)
                    .Distinct()
                    .Order()
                    .ToArray();
                var permitPorts = currentDevice.PermitPorts.Distinct().Order().ToArray();
                if (expectedPorts.Length == 0 || !expectedPorts.SequenceEqual(permitPorts))
                {
                    throw new AgentEnrollmentException(
                        "Agent 设备授权与服务清单的精确端口不一致；同步已停止。");
                }

                IReadOnlyList<AgentDeviceInfo> visibleDevices = [currentDevice];
                if (string.Equals(currentDevice.Role, "admin", StringComparison.Ordinal))
                {
                    var deviceList = await context.Client.GetDevicesAsync(
                            context.Credential,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var listedCurrent = deviceList.Devices.FirstOrDefault(device =>
                        string.Equals(device.DeviceId, currentDevice.DeviceId, StringComparison.Ordinal));
                    if (listedCurrent is null || !HasSameCurrentGrant(listedCurrent, currentDevice))
                    {
                        throw new AgentEnrollmentException(
                            "Agent 的自身授权回执与管理员设备列表不一致；同步已停止。");
                    }

                    visibleDevices = deviceList.Devices;
                }

                return new AgentManagementState(
                    catalog.ServicesRevision,
                    services,
                    permitPorts,
                    visibleDevices,
                    currentDevice.Role);
            }
            catch (AgentProtocolException exception)
            {
                throw TranslateProtocolFailure(exception);
            }
        }
    }

    private static void VerifyCurrentDevice(
        AgentManagementRequest request,
        VerifiedManagementContext context,
        AgentDeviceInfo currentDevice)
    {
        var localStatus = context.TrustedEndpoint.TailscaleStatus;
        var localNodeId = localStatus.Self?.Id;
        var localLogin = localStatus.LoginName?.Trim();
        if (currentDevice.Revoked ||
            !string.Equals(
                currentDevice.DeviceId,
                request.Registration.DeviceId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(localNodeId) ||
            !string.Equals(currentDevice.TailscaleNodeId, localNodeId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(localLogin) ||
            !string.Equals(
                currentDevice.TailscaleLogin,
                localLogin,
                StringComparison.OrdinalIgnoreCase) ||
            !request.Authorization.IsKnown ||
            !string.Equals(
                currentDevice.ClientKeyFingerprint,
                request.Authorization.ClientKeyFingerprint,
                StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException(
                "Agent 中的当前设备身份与本机 Tailscale/SSH 身份不一致；同步已停止。");
        }
    }

    private static bool HasSameCurrentGrant(AgentDeviceInfo listed, AgentDeviceInfo current) =>
        !listed.Revoked &&
        string.Equals(listed.DeviceId, current.DeviceId, StringComparison.Ordinal) &&
        string.Equals(listed.ClientKeyFingerprint, current.ClientKeyFingerprint, StringComparison.Ordinal) &&
        string.Equals(listed.Role, current.Role, StringComparison.Ordinal) &&
        string.Equals(listed.TailscaleNodeId, current.TailscaleNodeId, StringComparison.Ordinal) &&
        string.Equals(listed.TailscaleLogin, current.TailscaleLogin, StringComparison.Ordinal) &&
        listed.ServiceIds.SequenceEqual(current.ServiceIds, StringComparer.Ordinal) &&
        listed.PermitPorts.Order().SequenceEqual(current.PermitPorts.Order());

    public Task<AgentLoopbackDiscoveryResponse> DiscoverLoopbackServicesAsync(
        AgentManagementRequest request,
        CancellationToken cancellationToken = default) =>
        RunAdminOperationAsync(
            request,
            (client, credential, token) =>
                client.GetLoopbackDiscoveryAsync(credential, token),
            cancellationToken);

    public async Task<AgentDeviceRevocationResponse> RevokeDeviceAsync(
        AgentManagementRequest request,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new AgentEnrollmentException("请选择要撤销的设备。");
        }

        var state = await GetStateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(state.CurrentDeviceRole, "admin", StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException("当前设备不是 Agent 管理员，不能撤销其他设备。");
        }
        if (string.Equals(deviceId, request.Registration.DeviceId, StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException("为避免失去管理入口，Windows 界面不允许撤销当前设备。");
        }
        var target = state.Devices.FirstOrDefault(device =>
            string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
        if (target is null || target.Revoked)
        {
            throw new AgentEnrollmentException("所选设备已经撤销或不存在。");
        }

        var context = await OpenVerifiedAsync(request, cancellationToken).ConfigureAwait(false);
        using (context.Client)
        {
            try
            {
                return await context.Client.RevokeDeviceAsync(deviceId, context.Credential, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AgentProtocolException exception)
            {
                throw TranslateProtocolFailure(exception);
            }
        }
    }

    public Task<AgentDeviceRenameResponse> RenameDeviceAsync(
        AgentManagementRequest request,
        string deviceId,
        string deviceLabel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new AgentEnrollmentException("请选择要重命名的有效设备。");
        }
        if (string.IsNullOrWhiteSpace(deviceLabel) || deviceLabel != deviceLabel.Trim() ||
            deviceLabel.Length > 80 || deviceLabel.Any(char.IsControl))
        {
            throw new AgentEnrollmentException("设备名称必须是 1–80 个不含首尾空白或控制字符的字符。");
        }

        return RunAdminOperationAsync(
            request,
            (client, credential, token) => client.RenameDeviceAsync(
                deviceId,
                deviceLabel,
                credential,
                token),
            cancellationToken);
    }

    public Task<AgentEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
        AgentManagementRequest request,
        int ttlSeconds,
        CancellationToken cancellationToken = default) =>
        RunAdminOperationAsync(
            request,
            (client, credential, token) => client.CreateEnrollmentCodeAsync(
                ttlSeconds,
                credential,
                token),
            cancellationToken);

    public Task<AgentRelayPlanningState> PlanRelayAsync(
        AgentManagementRequest request,
        AgentRelayPlanRequest relay,
        CancellationToken cancellationToken = default) =>
        RunAdminOperationAsync(
            request,
            async (client, credential, token) =>
            {
                var plan = await client.PlanRelayAsync(relay, credential, token)
                    .ConfigureAwait(false);
                var probe = await client.ProbeRelayAsync(relay.Spec, credential, token)
                    .ConfigureAwait(false);
                return new AgentRelayPlanningState(relay, plan, probe);
            },
            cancellationToken);

    public Task<AgentRelayPlanResponse> PlanRelayDeletionAsync(
        AgentManagementRequest request,
        string slug,
        CancellationToken cancellationToken = default) =>
        RunAdminOperationAsync(
            request,
            (client, credential, token) => client.PlanRelayDeletionAsync(
                new AgentRelayDeletionPlanRequest(slug),
                credential,
                token),
            cancellationToken);

    public Task<AgentRelayApplyResponse> ApplyRelayAsync(
        AgentManagementRequest request,
        AgentRelayPlanRequest relay,
        AgentRelayPlanToken planToken,
        CancellationToken cancellationToken = default) =>
        RunAdminOperationAsync(
            request,
            (client, credential, token) => client.ApplyRelayAsync(
                new AgentRelayApplyRequest(relay, planToken, Confirmed: true),
                credential,
                token),
            cancellationToken);

    public Task<AgentRelayApplyResponse> DeleteRelayAsync(
        AgentManagementRequest request,
        string slug,
        AgentRelayPlanToken planToken,
        CancellationToken cancellationToken = default) =>
        RunAdminOperationAsync(
            request,
            (client, credential, token) => client.DeleteRelayAsync(
                new AgentRelayDeletionRequest(slug, planToken, Confirmed: true),
                credential,
                token),
            cancellationToken);

    public Task<AgentCatalogUpdatePreview> PreviewServiceCatalogUpdateAsync(
        AgentManagementRequest request,
        IReadOnlyList<AgentServiceDefinition> proposedServices,
        bool applyDefaultsToAllDevices,
        CancellationToken cancellationToken = default) =>
        RunAdminOperationAsync(
            request,
            async (client, credential, token) =>
            {
                var catalog = await client.GetServiceCatalogAsync(credential, token)
                    .ConfigureAwait(false);
                var devices = await client.GetDevicesAsync(credential, token)
                    .ConfigureAwait(false);
                return CreateCatalogUpdatePreview(
                    catalog,
                    devices.Devices,
                    proposedServices,
                    applyDefaultsToAllDevices);
            },
            cancellationToken);

    public Task<AgentServiceCatalogResponse> ApplyServiceCatalogUpdateAsync(
        AgentManagementRequest request,
        AgentCatalogUpdatePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.CanApply)
        {
            throw new AgentEnrollmentException(
                string.IsNullOrWhiteSpace(preview.BlockingReason)
                    ? "当前服务清单计划不能安全应用。"
                    : preview.BlockingReason);
        }

        return RunAdminOperationAsync(
            request,
            (client, credential, token) => client.UpdateServiceCatalogAsync(
                preview.ExpectedRevision,
                preview.ProposedServices,
                preview.ApplyDefaultsToAllDevices,
                credential,
                token),
            cancellationToken);
    }

    public async Task<AgentEndpointRebindVerification> VerifyEndpointRebindAsync(
        AgentManagementRequest request,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AgentEndpoint.TryParse(endpoint, out var parsed, out _))
        {
            throw new AgentEnrollmentException(
                "重新绑定地址必须是精确的 Tailscale IPv4:端口，不能包含路径、查询或凭据。");
        }

        var candidateRequest = request with
        {
            Registration = request.Registration with { Endpoint = parsed!.ToString() },
        };
        var context = await OpenVerifiedAsync(candidateRequest, cancellationToken)
            .ConfigureAwait(false);
        using (context.Client)
        {
            try
            {
                var current = await context.Client.GetCurrentDeviceAsync(
                        context.Credential,
                        cancellationToken)
                    .ConfigureAwait(false);
                VerifyCurrentDevice(candidateRequest, context, current.Device);
                if (!string.Equals(
                        current.ServicesRevision,
                        context.Bootstrap.CatalogRevision,
                        StringComparison.Ordinal))
                {
                    throw new AgentEnrollmentException(
                        "重新绑定核验期间 Linux 服务清单发生变化，请重新发现并确认。");
                }

                return new AgentEndpointRebindVerification(
                    parsed.ToString(),
                    current.ServicesRevision);
            }
            catch (AgentProtocolException exception)
            {
                throw TranslateProtocolFailure(exception);
            }
        }
    }

    private static AgentCatalogUpdatePreview CreateCatalogUpdatePreview(
        AgentServiceCatalogResponse current,
        IReadOnlyList<AgentDeviceInfo> devices,
        IReadOnlyList<AgentServiceDefinition> proposedServices,
        bool applyDefaultsToAllDevices)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(proposedServices);
        var proposed = proposedServices.ToArray();
        var currentById = current.Services.ToDictionary(service => service.Id, StringComparer.Ordinal);
        var proposedById = proposed.ToDictionary(service => service.Id, StringComparer.Ordinal);
        var addedIds = proposedById.Keys.Except(currentById.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var removedIds = currentById.Keys.Except(proposedById.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var modifiedIds = currentById.Keys.Intersect(proposedById.Keys, StringComparer.Ordinal)
            .Where(id => !Equals(currentById[id], proposedById[id]))
            .Order(StringComparer.Ordinal).ToArray();
        var changedIds = addedIds.Concat(removedIds).Concat(modifiedIds)
            .ToHashSet(StringComparer.Ordinal);
        var currentPorts = current.Services.Select(service => service.RemotePort).ToHashSet();
        var proposedPorts = proposed.Select(service => service.RemotePort).ToHashSet();
        var affected = new List<string>();
        string? blockingReason = null;
        var validIds = proposedById.Keys.ToHashSet(StringComparer.Ordinal);
        var defaults = proposed.Where(service => service.EnabledByDefault)
            .Select(service => service.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var device in devices.Where(device => !device.Revoked))
        {
            var previousIds = device.ServiceIds.ToHashSet(StringComparer.Ordinal);
            var nextIds = previousIds.Where(validIds.Contains).ToHashSet(StringComparer.Ordinal);
            if (applyDefaultsToAllDevices)
            {
                nextIds.UnionWith(defaults);
            }
            if (nextIds.Count == 0)
            {
                blockingReason =
                    $"设备 {device.DeviceLabel} 将失去全部服务授权；请保留至少一个服务后重新预览。";
                break;
            }

            var nextPorts = nextIds.Select(id => proposedById[id].RemotePort).ToHashSet();
            if (!previousIds.SetEquals(nextIds) ||
                !device.PermitPorts.ToHashSet().SetEquals(nextPorts) ||
                previousIds.Overlaps(changedIds))
            {
                affected.Add(device.DeviceId);
            }
        }

        var hasChange = addedIds.Length > 0 || removedIds.Length > 0 ||
                        modifiedIds.Length > 0 || affected.Count > 0;
        if (!hasChange && blockingReason is null)
        {
            blockingReason = "本机服务清单与 Linux Agent 已一致，无需提交。";
        }

        return new AgentCatalogUpdatePreview(
            current.ServicesRevision,
            proposed,
            applyDefaultsToAllDevices,
            addedIds,
            modifiedIds,
            removedIds,
            proposedPorts.Except(currentPorts).Order().ToArray(),
            currentPorts.Except(proposedPorts).Order().ToArray(),
            affected.Order(StringComparer.Ordinal).ToArray(),
            blockingReason is null,
            blockingReason ?? string.Empty);
    }

    private async Task<TResult> RunAdminOperationAsync<TResult>(
        AgentManagementRequest request,
        Func<IAgentProtocolClient, AgentCredential, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        var context = await OpenVerifiedAsync(request, cancellationToken).ConfigureAwait(false);
        using (context.Client)
        {
            try
            {
                var current = await context.Client.GetCurrentDeviceAsync(
                        context.Credential,
                        cancellationToken)
                    .ConfigureAwait(false);
                VerifyCurrentDevice(request, context, current.Device);
                if (!string.Equals(
                        current.ServicesRevision,
                        context.Bootstrap.CatalogRevision,
                        StringComparison.Ordinal))
                {
                    throw new AgentEnrollmentException(
                        "核验管理员身份期间服务清单发生变化，请重新操作。");
                }
                if (!string.Equals(current.Device.Role, "admin", StringComparison.Ordinal))
                {
                    throw new AgentEnrollmentException("当前设备不是 Agent 管理员，不能执行此高级操作。");
                }

                return await operation(context.Client, context.Credential, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AgentProtocolException exception)
            {
                throw TranslateProtocolFailure(exception);
            }
        }
    }

    private async Task<VerifiedManagementContext> OpenVerifiedAsync(
        AgentManagementRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Registration.IsConfigured ||
            !AgentEndpoint.TryParse(request.Registration.Endpoint, out var endpoint, out _))
        {
            throw new AgentEnrollmentException("当前配置没有有效的 Linux Agent 登记信息。");
        }

        var trustedEndpoint = await _trustVerifier.VerifyEndpointAsync(endpoint!, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                trustedEndpoint.Peer.Id,
                request.Registration.TailscaleNodeId,
                StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException(
                "Agent 地址当前对应的 Tailscale 节点与登记时不同；管理操作已停止。");
        }

        var credential = await _credentialStore.LoadAsync(
                request.Registration.CredentialReference,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AgentEnrollmentException(
                "Windows 凭据管理器中找不到此设备的 Agent 凭据；现有 SSH 隧道仍可使用，可重新登记恢复管理功能。");
        var client = _clientFactory.Create(endpoint!);
        try
        {
            await client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            var bootstrap = await client.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            _trustVerifier.VerifyBootstrap(bootstrap, trustedEndpoint);
            VerifyConfiguredServer(request, bootstrap);
            return new VerifiedManagementContext(
                client,
                credential,
                bootstrap,
                trustedEndpoint);
        }
        catch (AgentProtocolException exception)
        {
            client.Dispose();
            throw TranslateProtocolFailure(exception);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static void VerifyConfiguredServer(
        AgentManagementRequest request,
        AgentBootstrapResponse bootstrap)
    {
        if (!string.Equals(bootstrap.ServerHost, request.Server.Host, StringComparison.Ordinal) ||
            !string.Equals(bootstrap.TailnetDnsSuffix,
                request.ExpectedTailnetDnsSuffix,
                StringComparison.Ordinal) ||
            !string.Equals(bootstrap.TailscaleNodeId,
                request.Registration.TailscaleNodeId,
                StringComparison.Ordinal) ||
            !string.Equals(bootstrap.SshUser, request.Server.User, StringComparison.Ordinal) ||
            bootstrap.SshPort != request.Server.Port ||
            !string.Equals(bootstrap.HostPublicKey, request.Server.PinnedHostKey, StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException(
                "Agent 当前服务器身份与 Windows 固定配置不一致；管理操作已停止。");
        }
    }

    private static AgentEnrollmentException TranslateProtocolFailure(AgentProtocolException exception) =>
        exception.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => new AgentEnrollmentException(
                "Agent 设备凭据已失效；现有 SSH 隧道可能仍可使用，请重新登记恢复管理功能。",
                exception),
            System.Net.HttpStatusCode.Forbidden => new AgentEnrollmentException(
                "Agent 拒绝了当前操作；完整设备列表与撤销仅管理员可用。若自身同步也失败，请重新登记此设备。",
                exception),
            System.Net.HttpStatusCode.Conflict => new AgentEnrollmentException(
                "Agent 状态已经变化，请刷新设备列表后重试。",
                exception),
            _ => new AgentEnrollmentException(
                "Linux Agent 返回了无法验证的管理响应；本地配置没有改变。",
                exception),
        };

    private sealed record VerifiedManagementContext(
        IAgentProtocolClient Client,
        AgentCredential Credential,
        AgentBootstrapResponse Bootstrap,
        AgentTrustedEndpoint TrustedEndpoint);
}
