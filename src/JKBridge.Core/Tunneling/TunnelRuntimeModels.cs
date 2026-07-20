using JKBridge.Core.Health;

namespace JKBridge.Core.Tunneling;

/// <summary>
/// User-facing lifecycle of the single supervised SSH tunnel.
/// </summary>
public enum TunnelPhase
{
    Stopped,
    Preflight,
    NeedsTailscale,
    WrongTailnet,
    PeerUnavailable,
    NeedsKeySetup,
    NeedsPairing,
    HostKeyBlocked,
    PortConflict,
    Starting,
    Connected,
    Degraded,
    Reconnecting,
    PausedForSleep,
    Stopping,
    Faulted,
}

public enum ServicePhase
{
    Disabled,
    WaitingForTunnel,
    Checking,
    Available,
    RemoteUnavailable,
    ForwardDenied,
}

public sealed record ServiceSnapshot(
    string ServiceId,
    string Name,
    ServicePhase Phase,
    DateTimeOffset? LastCheckedAt = null,
    TimeSpan? Latency = null,
    int? HttpStatus = null,
    string? Detail = null)
{
    public bool CanOpen => Phase is
        ServicePhase.Checking or
        ServicePhase.Available or
        ServicePhase.RemoteUnavailable;

    public static ServiceSnapshot FromHealth(string name, ServiceHealth health) =>
        new(
            health.ServiceId,
            name,
            health.State switch
            {
                ServiceHealthState.Waiting => ServicePhase.WaitingForTunnel,
                ServiceHealthState.Checking => ServicePhase.Checking,
                ServiceHealthState.Available => ServicePhase.Available,
                _ => ServicePhase.RemoteUnavailable,
            },
            health.CheckedAt,
            health.Latency,
            health.HttpStatus,
            health.Detail);
}

/// <summary>
/// Immutable presentation model. The runtime publishes a new instance for every material transition.
/// </summary>
public sealed record TunnelSnapshot(
    TunnelPhase Phase,
    bool DesiredConnected,
    string Message,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ServiceSnapshot> Services,
    string? PeerDnsName = null,
    string? TargetHost = null,
    bool UsingIpFallback = false,
    int? SshProcessId = null,
    int RetryAttempt = 0,
    DateTimeOffset? RetryAt = null,
    SshFailureClassification? Failure = null,
    string? TailscaleSelfDnsName = null,
    string? TailscaleLoginName = null,
    string? CurrentTailnetDnsSuffix = null)
{
    public bool IsConnected => Phase is TunnelPhase.Connected or TunnelPhase.Degraded;

    public static TunnelSnapshot Stopped(IReadOnlyList<ServiceSnapshot>? services = null) =>
        new(
            TunnelPhase.Stopped,
            DesiredConnected: false,
            "Bridge is stopped.",
            DateTimeOffset.Now,
            services ?? Array.Empty<ServiceSnapshot>());
}

/// <summary>
/// The documented retry sequence with bounded random jitter.
/// </summary>
public static class TunnelRetrySchedule
{
    private static readonly double[] Seconds = [1, 2, 5, 10, 20, 30];

    public static TimeSpan GetDelay(int attempt, double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        if (double.IsNaN(jitterSample) || jitterSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterSample));
        }

        var baseSeconds = Seconds[Math.Min(attempt, Seconds.Length - 1)];
        var factor = 0.8 + (jitterSample * 0.4);
        return TimeSpan.FromSeconds(baseSeconds * factor);
    }
}
