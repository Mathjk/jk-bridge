using JKBridge.Core.Agent;
using JKBridge.Core.Models;
using JKBridge.Core.Processes;
using JKBridge.Core.Tailscale;

namespace JKBridge.App.Services;

public interface IAgentProtocolClient : IDisposable
{
    Task<AgentHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<AgentBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken = default);

    Task<AgentEnrollmentResponse> EnrollAsync(
        JKBridge.Core.Agent.AgentEnrollmentRequest request,
        AgentCredential enrollmentCredential,
        CancellationToken cancellationToken = default);

    Task<AgentServiceCatalogResponse> GetServiceCatalogAsync(
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default);

    Task<AgentServiceCatalogResponse> UpdateServiceCatalogAsync(
        string expectedRevision,
        IReadOnlyList<AgentServiceDefinition> services,
        bool applyDefaultsToAllDevices,
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentServiceCatalogResponse>(new NotSupportedException());

    Task<AgentCurrentDeviceResponse> GetCurrentDeviceAsync(
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default);

    Task<AgentDeviceListResponse> GetDevicesAsync(
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default);

    Task<AgentDeviceRevocationResponse> RevokeDeviceAsync(
        string deviceId,
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default);

    Task<AgentDeviceRenameResponse> RenameDeviceAsync(
        string deviceId,
        string deviceLabel,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentDeviceRenameResponse>(new NotSupportedException());

    Task<AgentEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
        int ttlSeconds,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentEnrollmentCodeResponse>(new NotSupportedException());

    Task<AgentRelayPlanResponse> PlanRelayAsync(
        AgentRelayPlanRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentRelayPlanResponse>(new NotSupportedException());

    Task<AgentRelayProbeResponse> ProbeRelayAsync(
        AgentRelaySpec spec,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentRelayProbeResponse>(new NotSupportedException());

    Task<AgentRelayApplyResponse> ApplyRelayAsync(
        AgentRelayApplyRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentRelayApplyResponse>(new NotSupportedException());

    Task<AgentRelayPlanResponse> PlanRelayDeletionAsync(
        AgentRelayDeletionPlanRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentRelayPlanResponse>(new NotSupportedException());

    Task<AgentRelayApplyResponse> DeleteRelayAsync(
        AgentRelayDeletionRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentRelayApplyResponse>(new NotSupportedException());
}

public interface IAgentProtocolClientFactory
{
    IAgentProtocolClient Create(AgentEndpoint endpoint);
}

/// <summary>
/// Orchestrates explicit Agent enrollment while keeping OS secret persistence in AppController.
/// It fails closed unless the endpoint maps to one online peer in the local Tailscale status and
/// that peer's MagicDNS identity matches the Agent bootstrap response.
/// </summary>
public sealed class AgentEnrollmentService : IAgentEnrollmentService
{
    private readonly IAgentProtocolClientFactory _clientFactory;
    private readonly IAgentSshIdentityProvisioner _identityProvisioner;
    private readonly IAgentTrustVerifier _trustVerifier;

    public AgentEnrollmentService(
        IAgentProtocolClientFactory clientFactory,
        IAgentSshIdentityProvisioner? identityProvisioner = null,
        ICommandRunner? commandRunner = null,
        Func<string?>? tailscaleLocator = null,
        IAgentTrustVerifier? trustVerifier = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _identityProvisioner = identityProvisioner ?? new AgentSshIdentityProvisioner(commandRunner);
        _trustVerifier = trustVerifier ?? new AgentTrustVerifier(commandRunner, tailscaleLocator);
    }

    public async Task<AgentEnrollmentResult> EnrollAsync(
        AgentEnrollmentRequest request,
        IProgress<AgentEnrollmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AgentEndpoint.TryParse(request.Endpoint, out var endpoint, out _))
        {
            throw new AgentEnrollmentException(
                "Agent 地址必须是 http:// 开头的精确 Tailscale IPv4 和端口，不能包含路径、查询参数或凭据。");
        }

        if (!AgentCredential.TryParse(request.RecoveryCode?.Trim(), out var enrollmentCredential))
        {
            throw new AgentEnrollmentException("一次性登记码格式无效，请从 Linux 初始化/恢复流程或管理员 Windows 重新生成。");
        }

        var deviceLabel = NormalizeDeviceLabel(request.DeviceLabel);
        progress?.Report(new(
            AgentEnrollmentStage.CheckingAgent,
            "正在通过本机 Tailscale 状态核对 Agent 节点身份…"));
        var trustedEndpoint = await _trustVerifier.VerifyEndpointAsync(endpoint!, cancellationToken)
            .ConfigureAwait(false);
        var peer = trustedEndpoint.Peer;

        try
        {
            using var client = _clientFactory.Create(endpoint!);
            await client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            var bootstrap = await client.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            _trustVerifier.VerifyBootstrap(bootstrap, trustedEndpoint);
            if (!bootstrap.EnrollmentAvailable)
            {
                throw new AgentEnrollmentException(
                    "Linux Agent 当前没有开放设备登记；请在 Linux 生成新的恢复码后重试。");
            }

            progress?.Report(new(
                AgentEnrollmentStage.PreparingKey,
                "正在创建或验证此 Windows 专用的 Ed25519 密钥…"));
            var identity = await _identityProvisioner.PrepareAsync(request.IdentityFile, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new(
                AgentEnrollmentStage.AwaitingAuthorization,
                "正在使用一次性恢复码明确登记此设备；Agent 将只授权默认服务端口…"));
            var receipt = await client.EnrollAsync(
                    new JKBridge.Core.Agent.AgentEnrollmentRequest(
                        deviceLabel,
                        identity.PublicKey,
                        Array.Empty<string>()),
                    enrollmentCredential!,
                    cancellationToken)
                .ConfigureAwait(false);
            VerifyEnrollmentServer(receipt, bootstrap, peer, deviceLabel);

            progress?.Report(new(
                AgentEnrollmentStage.FetchingCatalog,
                "设备已授权，正在用设备凭据读取并核对服务清单…"));
            var catalog = await client.GetServiceCatalogAsync(receipt.DeviceCredential, cancellationToken)
                .ConfigureAwait(false);
            var services = catalog.Services.Select(service => service.ToServiceDefinition()).ToArray();
            ValidateEnrollmentReceipt(receipt, catalog, services, identity.Fingerprint);

            progress?.Report(new(
                AgentEnrollmentStage.Completed,
                $"已核对 {services.Length} 个服务和 {receipt.PermitPorts.Count} 个精确 Linux 端口。"));
            return new AgentEnrollmentResult(
                endpoint!.ToString(),
                receipt.DeviceCredential,
                receipt.DeviceId,
                deviceLabel,
                catalog.Revision,
                bootstrap.DisplayName,
                bootstrap.ServerHost,
                bootstrap.TailnetDnsSuffix,
                bootstrap.SshUser,
                bootstrap.SshPort,
                bootstrap.HostPublicKey,
                identity.PortableIdentityFile,
                identity.Fingerprint,
                receipt.PermitPorts,
                services,
                peer.Id);
        }
        catch (AgentEnrollmentException)
        {
            throw;
        }
        catch (AgentProtocolException exception)
        {
            throw new AgentEnrollmentException(TranslateProtocolFailure(exception), exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AgentEnrollmentException(
                "无法通过 Tailscale 连接 Linux Agent；请确认 Agent 正在运行，或改用一次性配对。",
                exception);
        }
    }

    private static void VerifyEnrollmentServer(
        AgentEnrollmentResponse receipt,
        AgentBootstrapResponse bootstrap,
        TailscaleNode peer,
        string requestedDeviceLabel)
    {
        var server = receipt.Server;
        if (!string.Equals(server.ServerHost, bootstrap.ServerHost, StringComparison.Ordinal) ||
            !string.Equals(server.TailnetDnsSuffix, bootstrap.TailnetDnsSuffix, StringComparison.Ordinal) ||
            !string.Equals(server.TailscaleIpv4, bootstrap.TailscaleIpv4, StringComparison.Ordinal) ||
            !string.Equals(server.TailscaleNodeId, bootstrap.TailscaleNodeId, StringComparison.Ordinal) ||
            !string.Equals(server.TailscaleNodeId, peer.Id, StringComparison.Ordinal) ||
            !string.Equals(server.SshUser, bootstrap.SshUser, StringComparison.Ordinal) ||
            server.SshPort != bootstrap.SshPort ||
            !string.Equals(server.HostPublicKey, bootstrap.HostPublicKey, StringComparison.Ordinal) ||
            !string.Equals(server.ServicesRevision, receipt.ServicesRevision, StringComparison.Ordinal) ||
            !string.Equals(bootstrap.CatalogRevision, receipt.ServicesRevision, StringComparison.Ordinal) ||
            !string.Equals(receipt.DeviceLabel, requestedDeviceLabel, StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException(
                "Agent 登记回执中的服务器或设备身份与已核验引导信息不一致；Windows 不会保存该配置。");
        }
    }

    private static void ValidateEnrollmentReceipt(
        AgentEnrollmentResponse receipt,
        AgentServiceCatalogResponse catalog,
        IReadOnlyList<ServiceDefinition> services,
        string expectedClientFingerprint)
    {
        if (!string.Equals(
                receipt.ClientKeyFingerprint,
                expectedClientFingerprint,
                StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException(
                "Agent 授权回执不属于此 Windows 密钥；Windows 不会保存或启动该配置。");
        }

        if (!string.Equals(receipt.CatalogRevision, catalog.Revision, StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException("登记期间 Linux 服务清单发生变化，请重新提交登记以取得精确授权。");
        }

        var expectedPorts = services
            .Where(service => service.Enabled)
            .Select(service => service.RemotePort)
            .Distinct()
            .Order()
            .ToArray();
        var receiptPorts = receipt.PermitPorts.Distinct().Order().ToArray();
        if (expectedPorts.Length == 0 || !expectedPorts.SequenceEqual(receiptPorts))
        {
            throw new AgentEnrollmentException(
                "Agent 授权回执与服务清单的默认精确端口不一致；Windows 不会保存或启动该配置。");
        }
    }

    private static string NormalizeDeviceLabel(string value)
    {
        var label = value?.Trim() ?? string.Empty;
        if (label.Length is < 1 or > 80 || label.Any(char.IsControl))
        {
            throw new AgentEnrollmentException("此电脑名称必须是 1–80 个不含控制字符的字符。");
        }

        return label;
    }

    private static string TranslateProtocolFailure(AgentProtocolException exception) =>
        exception.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "一次性登记码无效、已过期或已经使用，请从 Linux 或管理员 Windows 重新生成。",
            System.Net.HttpStatusCode.Forbidden => "Linux Agent 拒绝登记此设备；请检查 Agent 的 Tailnet 身份策略。",
            System.Net.HttpStatusCode.Conflict => "设备登记或服务清单已变化，请刷新后重试。",
            _ => "Linux Agent 返回了无法验证的响应；登记未保存，可重试或改用一次性配对。",
        };
}
