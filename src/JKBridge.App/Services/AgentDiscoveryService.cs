using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using JKBridge.App.Platform;
using JKBridge.Core.Agent;
using JKBridge.Core.Processes;
using JKBridge.Core.Tailscale;

namespace JKBridge.App.Services;

public enum AgentDiscoveryOutcome
{
    None,
    Single,
    Multiple,
}

/// <summary>
/// Public, non-secret Agent identity suitable for presenting in the setup UI.
/// Every field has been checked against the local Tailscale status; none is a
/// sufficient trust anchor by itself.
/// </summary>
public sealed record AgentDiscoveryCandidate(
    string Endpoint,
    string DisplayName,
    string ServerHost,
    string TailnetDnsSuffix,
    string TailscaleNodeId,
    string AgentVersion,
    bool EnrollmentAvailable);

public sealed record AgentDiscoveryResult(
    AgentDiscoveryOutcome Outcome,
    IReadOnlyList<AgentDiscoveryCandidate> Candidates)
{
    public AgentDiscoveryCandidate? Candidate =>
        Outcome == AgentDiscoveryOutcome.Single && Candidates.Count == 1
            ? Candidates[0]
            : null;
}

public sealed record AgentDiscoveryOptions
{
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaximumConcurrency { get; init; } = 4;

    public int MaximumProbeTargets { get; init; } = 256;
}

public interface IAgentDiscoveryService
{
    Task<AgentDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Supplies a fresh, local Tailscale status snapshot when the user explicitly
/// starts discovery. Implementations must not persist or log the raw JSON.
/// </summary>
public interface IAgentDiscoveryStatusProvider
{
    Task<TailscaleStatusSnapshot> GetStatusAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Production status provider. It invokes tailscale.exe directly, without a
/// shell, and bounds both execution time and captured output.
/// </summary>
public sealed class TailscaleCliAgentDiscoveryStatusProvider : IAgentDiscoveryStatusProvider
{
    private readonly ICommandRunner _commandRunner;
    private readonly Func<string?> _tailscaleLocator;

    public TailscaleCliAgentDiscoveryStatusProvider(
        ICommandRunner? commandRunner = null,
        Func<string?>? tailscaleLocator = null)
    {
        _commandRunner = commandRunner ?? new CommandRunner();
        _tailscaleLocator = tailscaleLocator ?? ExecutableLocator.FindTailscale;
    }

    public async Task<TailscaleStatusSnapshot> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var tailscale = _tailscaleLocator();
        if (string.IsNullOrWhiteSpace(tailscale))
        {
            throw new AgentDiscoveryException("找不到 Tailscale。请先安装、启动并登录。");
        }

        CommandResult result;
        try
        {
            result = await _commandRunner.RunAsync(
                    new CommandRequest(
                        tailscale,
                        ["status", "--json"],
                        TimeSpan.FromSeconds(10),
                        MaxOutputCharacters: 256 * 1024),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            IOException or
            System.ComponentModel.Win32Exception)
        {
            throw new AgentDiscoveryException("无法读取本机 Tailscale 状态。", exception);
        }

        if (!result.IsSuccess ||
            !TailscaleStatusParser.TryParse(result.StandardOutput, out var status, out _) ||
            status is null ||
            !status.IsRunning)
        {
            throw new AgentDiscoveryException("Tailscale 尚未处于 Running 状态，请先登录正确的 tailnet。");
        }

        return status;
    }
}

/// <summary>
/// Explicit, read-only discovery of JK Bridge Agents on known online Tailscale
/// peers. It never scans an address range and never enrolls a device.
/// </summary>
public sealed class AgentDiscoveryService : IAgentDiscoveryService
{
    public const int AgentPort = 45871;

    private readonly IAgentProtocolClientFactory _clientFactory;
    private readonly IAgentDiscoveryStatusProvider _statusProvider;
    private readonly AgentDiscoveryOptions _options;

    public AgentDiscoveryService(
        IAgentProtocolClientFactory clientFactory,
        IAgentDiscoveryStatusProvider statusProvider,
        AgentDiscoveryOptions? options = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _statusProvider = statusProvider ?? throw new ArgumentNullException(nameof(statusProvider));
        _options = ValidateOptions(options ?? new AgentDiscoveryOptions());
    }

    /// <summary>
    /// Performs one discovery pass. Callers should invoke this only in response
    /// to an explicit user action. A Single result is the only unambiguous one.
    /// </summary>
    public async Task<AgentDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(_options.OverallTimeout);

        TailscaleStatusSnapshot status;
        try
        {
            status = await _statusProvider.GetStatusAsync(overallTimeout.Token)
                .WaitAsync(overallTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentDiscoveryException("读取 Tailscale 状态时发现操作超时，请重试。");
        }

        if (status is null || !status.IsRunning)
        {
            throw new AgentDiscoveryException("Tailscale 尚未处于 Running 状态，请先登录正确的 tailnet。");
        }

        var tailnetSuffix = TailscaleStatusParser.NormalizeDnsName(status.Tailnet.MagicDnsSuffix);
        if (!IsSafeDnsName(tailnetSuffix))
        {
            throw new AgentDiscoveryException("当前 Tailscale 状态没有可核验的 MagicDNS tailnet 身份。");
        }

        var targets = BuildTargets(status, tailnetSuffix);
        if (targets.Count > _options.MaximumProbeTargets)
        {
            throw new AgentDiscoveryException(
                "当前 tailnet 中可探测的在线地址过多；为避免大范围探测，发现操作已停止。");
        }

        if (targets.Count == 0)
        {
            return EmptyResult();
        }

        var candidates = new ConcurrentBag<AgentDiscoveryCandidate>();
        using var gate = new SemaphoreSlim(_options.MaximumConcurrency);

        var probes = targets.Select(target => ProbeWithGateAsync(
            target,
            tailnetSuffix,
            candidates,
            gate,
            cancellationToken,
            overallTimeout.Token));
        await Task.WhenAll(probes).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var sanitized = DeduplicateStableNodes(candidates);
        return sanitized.Count switch
        {
            0 => EmptyResult(),
            1 => new(AgentDiscoveryOutcome.Single, sanitized),
            _ => new(AgentDiscoveryOutcome.Multiple, sanitized),
        };
    }

    private async Task ProbeWithGateAsync(
        DiscoveryTarget target,
        string tailnetSuffix,
        ConcurrentBag<AgentDiscoveryCandidate> candidates,
        SemaphoreSlim gate,
        CancellationToken callerToken,
        CancellationToken boundedToken)
    {
        var entered = false;
        try
        {
            await gate.WaitAsync(boundedToken).ConfigureAwait(false);
            entered = true;
            // Cancellation and semaphore release can race. Do not start a new
            // network operation after the overall discovery deadline won.
            boundedToken.ThrowIfCancellationRequested();
            var candidate = await ProbeAsync(target, tailnetSuffix, boundedToken)
                .ConfigureAwait(false);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // A per-discovery or per-peer deadline is an ordinary failed probe.
        }
        catch (TimeoutException)
        {
            // A peer that does not answer within the fixed budget is not a candidate.
        }
        catch (Exception exception) when (exception is
            AgentProtocolException or
            HttpRequestException or
            IOException or
            InvalidOperationException or
            ArgumentException or
            ObjectDisposedException)
        {
            // Discovery is best-effort. Invalid, unreachable, or non-Agent peers are ignored.
        }
        finally
        {
            if (entered)
            {
                gate.Release();
            }
        }
    }

    private async Task<AgentDiscoveryCandidate?> ProbeAsync(
        DiscoveryTarget target,
        string tailnetSuffix,
        CancellationToken boundedToken)
    {
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(boundedToken);
        probeTimeout.CancelAfter(_options.ProbeTimeout);
        var endpoint = AgentEndpoint.Create(target.Address, AgentPort);
        using var client = _clientFactory.Create(endpoint);

        // Both endpoints are intentionally unauthenticated and receive no credential.
        var health = await client.GetHealthAsync(probeTimeout.Token)
            .ConfigureAwait(false);
        var bootstrap = await client.GetBootstrapAsync(probeTimeout.Token)
            .ConfigureAwait(false);

        return TryCreateCandidate(endpoint, target, tailnetSuffix, health, bootstrap);
    }

    private static AgentDiscoveryCandidate? TryCreateCandidate(
        AgentEndpoint endpoint,
        DiscoveryTarget target,
        string tailnetSuffix,
        AgentHealthResponse health,
        AgentBootstrapResponse bootstrap)
    {
        if (health.ProtocolVersion != AgentProtocol.Version ||
            health.Status is not ("ok" or "needsBootstrap") ||
            bootstrap.ProtocolVersion != AgentProtocol.Version ||
            !IsSafeText(health.AgentVersion, 64) ||
            !IsSafeText(bootstrap.AgentVersion, 64) ||
            !string.Equals(health.AgentVersion, bootstrap.AgentVersion, StringComparison.Ordinal) ||
            !IsSafeText(bootstrap.DisplayName, 128) ||
            !IsSafeNodeId(bootstrap.TailscaleNodeId) ||
            !string.Equals(bootstrap.TailscaleNodeId, target.StableNodeId, StringComparison.Ordinal))
        {
            return null;
        }

        if (!IPAddress.TryParse(bootstrap.TailscaleIpv4, out var claimedAddress) ||
            !endpoint.Address.Equals(claimedAddress))
        {
            return null;
        }

        var claimedSuffix = TailscaleStatusParser.NormalizeDnsName(bootstrap.TailnetDnsSuffix);
        var claimedHost = TailscaleStatusParser.NormalizeDnsName(bootstrap.ServerHost);
        if (!IsSafeDnsName(claimedSuffix) ||
            !IsSafeDnsName(claimedHost) ||
            !string.Equals(claimedSuffix, tailnetSuffix, StringComparison.Ordinal) ||
            !string.Equals(claimedHost, target.MagicDnsName, StringComparison.Ordinal) ||
            !claimedHost.EndsWith('.' + tailnetSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        return new AgentDiscoveryCandidate(
            endpoint.ToString(),
            bootstrap.DisplayName.Trim(),
            claimedHost,
            claimedSuffix,
            target.StableNodeId,
            bootstrap.AgentVersion.Trim(),
            bootstrap.EnrollmentAvailable);
    }

    private static IReadOnlyList<DiscoveryTarget> BuildTargets(
        TailscaleStatusSnapshot status,
        string tailnetSuffix)
    {
        var claims = status.Peers
            .Where(peer => peer.Online &&
                           IsSafeNodeId(peer.Id) &&
                           IsPeerDnsInTailnet(peer.DnsName, tailnetSuffix))
            .SelectMany(peer => peer.TailscaleIps.Select(value => CreatePeerClaim(peer, value)))
            .Where(claim => claim is not null)
            .Cast<PeerAddressClaim>()
            .ToArray();

        // An address associated with two stable nodes (or conflicting DNS names)
        // is not a unique local trust anchor and is therefore never contacted.
        return claims
            .GroupBy(claim => claim.Address.ToString(), StringComparer.Ordinal)
            .Where(group => group.Select(item => item.StableNodeId)
                                .Distinct(StringComparer.Ordinal).Count() == 1 &&
                            group.Select(item => item.MagicDnsName)
                                .Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => group.First())
            .DistinctBy(
                claim => $"{claim.StableNodeId}\0{claim.Address}",
                StringComparer.Ordinal)
            .OrderBy(claim => claim.Address.ToString(), StringComparer.Ordinal)
            .Select(claim => new DiscoveryTarget(
                claim.Address,
                claim.StableNodeId,
                claim.MagicDnsName))
            .ToArray();
    }

    private static PeerAddressClaim? CreatePeerClaim(TailscaleNode peer, string value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            return null;
        }

        try
        {
            _ = AgentEndpoint.Create(address, AgentPort);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new PeerAddressClaim(
            address,
            peer.Id,
            TailscaleStatusParser.NormalizeDnsName(peer.DnsName));
    }

    private static bool IsPeerDnsInTailnet(string value, string tailnetSuffix)
    {
        var dnsName = TailscaleStatusParser.NormalizeDnsName(value);
        return IsSafeDnsName(dnsName) &&
               dnsName.Length > tailnetSuffix.Length + 1 &&
               dnsName.EndsWith('.' + tailnetSuffix, StringComparison.Ordinal);
    }

    private static IReadOnlyList<AgentDiscoveryCandidate> DeduplicateStableNodes(
        IEnumerable<AgentDiscoveryCandidate> candidates)
    {
        var unique = new List<AgentDiscoveryCandidate>();
        foreach (var group in candidates.GroupBy(candidate => candidate.TailscaleNodeId, StringComparer.Ordinal))
        {
            // Multiple current IPv4s for one stable node are acceptable only when
            // all independently verified public identity fields agree.
            var identityCount = group
                .Select(candidate => new
                {
                    candidate.DisplayName,
                    candidate.ServerHost,
                    candidate.TailnetDnsSuffix,
                    candidate.AgentVersion,
                    candidate.EnrollmentAvailable,
                })
                .Distinct()
                .Count();
            if (identityCount == 1)
            {
                unique.Add(group.OrderBy(candidate => candidate.Endpoint, StringComparer.Ordinal).First());
            }
        }

        var ordered = unique
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ServerHost, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TailscaleNodeId, StringComparer.Ordinal)
            .ToArray();
        return new ReadOnlyCollection<AgentDiscoveryCandidate>(ordered);
    }

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsSafeNodeId(string? value) =>
        IsSafeText(value, 128) &&
        char.IsAsciiLetterOrDigit(value![0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsSafeDnsName(string? value)
    {
        if (!IsSafeText(value, 253))
        {
            return false;
        }

        var labels = value!.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is >= 1 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) &&
            char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static AgentDiscoveryResult EmptyResult() =>
        new(AgentDiscoveryOutcome.None, Array.Empty<AgentDiscoveryCandidate>());

    private static AgentDiscoveryOptions ValidateOptions(AgentDiscoveryOptions options)
    {
        if (options.ProbeTimeout <= TimeSpan.Zero || options.ProbeTimeout > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Probe timeout must be in (0, 10] seconds.");
        }

        if (options.OverallTimeout <= TimeSpan.Zero ||
            options.OverallTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Overall timeout must be in (0, 1] minutes.");
        }

        if (options.MaximumConcurrency is < 1 or > 16 ||
            options.MaximumProbeTargets is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Discovery limits are outside the safe range.");
        }

        return options;
    }

    private sealed record PeerAddressClaim(
        IPAddress Address,
        string StableNodeId,
        string MagicDnsName);

    private sealed record DiscoveryTarget(
        IPAddress Address,
        string StableNodeId,
        string MagicDnsName);
}

public sealed class AgentDiscoveryException : Exception
{
    public AgentDiscoveryException(string message)
        : base(message)
    {
    }

    public AgentDiscoveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
