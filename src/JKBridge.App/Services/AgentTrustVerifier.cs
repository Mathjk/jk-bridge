using JKBridge.App.Platform;
using JKBridge.Core.Agent;
using JKBridge.Core.Processes;
using JKBridge.Core.Tailscale;

namespace JKBridge.App.Services;

public sealed record AgentTrustedEndpoint(
    TailscaleStatusSnapshot TailscaleStatus,
    TailscaleNode Peer);

public interface IAgentTrustVerifier
{
    Task<AgentTrustedEndpoint> VerifyEndpointAsync(
        AgentEndpoint endpoint,
        CancellationToken cancellationToken = default);

    void VerifyBootstrap(
        AgentBootstrapResponse bootstrap,
        AgentTrustedEndpoint trustedEndpoint);
}

/// <summary>
/// Establishes the local trust anchor for an Agent endpoint. The Agent's own JSON is not trusted
/// until its exact IP maps to one online peer in the local Tailscale status.
/// </summary>
public sealed class AgentTrustVerifier : IAgentTrustVerifier
{
    private readonly ICommandRunner _commandRunner;
    private readonly Func<string?> _tailscaleLocator;

    public AgentTrustVerifier(
        ICommandRunner? commandRunner = null,
        Func<string?>? tailscaleLocator = null)
    {
        _commandRunner = commandRunner ?? new CommandRunner();
        _tailscaleLocator = tailscaleLocator ?? ExecutableLocator.FindTailscale;
    }

    public async Task<AgentTrustedEndpoint> VerifyEndpointAsync(
        AgentEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var status = await GetTailscaleStatusAsync(cancellationToken).ConfigureAwait(false);
        var address = endpoint.Address.ToString();
        var matches = status.Peers
            .Where(peer => peer.TailscaleIps.Contains(address, StringComparer.Ordinal))
            .ToArray();
        if (matches.Length != 1 || !matches[0].Online || string.IsNullOrWhiteSpace(matches[0].Id))
        {
            throw new AgentEnrollmentException(
                "Agent 地址未能唯一对应当前 tailnet 中的在线 Linux 节点；不会信任服务器自报身份。");
        }

        return new AgentTrustedEndpoint(status, matches[0]);
    }

    public void VerifyBootstrap(
        AgentBootstrapResponse bootstrap,
        AgentTrustedEndpoint trustedEndpoint)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(trustedEndpoint);
        var currentSuffix = TailscaleStatusParser.NormalizeDnsName(
            trustedEndpoint.TailscaleStatus.Tailnet.MagicDnsSuffix);
        var bootstrapSuffix = TailscaleStatusParser.NormalizeDnsName(bootstrap.TailnetDnsSuffix);
        var peerDns = TailscaleStatusParser.NormalizeDnsName(trustedEndpoint.Peer.DnsName);
        var bootstrapDns = TailscaleStatusParser.NormalizeDnsName(bootstrap.ServerHost);
        if (currentSuffix.Length == 0 ||
            !string.Equals(currentSuffix, bootstrapSuffix, StringComparison.Ordinal) ||
            peerDns.Length == 0 ||
            !string.Equals(peerDns, bootstrapDns, StringComparison.Ordinal) ||
            !string.Equals(trustedEndpoint.Peer.Id, bootstrap.TailscaleNodeId, StringComparison.Ordinal))
        {
            throw new AgentEnrollmentException(
                "Agent 返回的 MagicDNS 身份与本机 Tailscale 节点记录不一致；操作已停止。请勿绕过此检查。");
        }
    }

    private async Task<TailscaleStatusSnapshot> GetTailscaleStatusAsync(
        CancellationToken cancellationToken)
    {
        var tailscale = _tailscaleLocator()
                        ?? throw new AgentEnrollmentException("找不到 Tailscale。请先安装、启动并登录。");
        var result = await _commandRunner.RunAsync(
                new CommandRequest(
                    tailscale,
                    ["status", "--json"],
                    TimeSpan.FromSeconds(10),
                    MaxOutputCharacters: 256 * 1024),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess ||
            !TailscaleStatusParser.TryParse(result.StandardOutput, out var status, out _) ||
            status is null ||
            !status.IsRunning)
        {
            throw new AgentEnrollmentException("Tailscale 尚未处于 Running 状态，请先登录正确的 tailnet。");
        }

        return status;
    }
}
