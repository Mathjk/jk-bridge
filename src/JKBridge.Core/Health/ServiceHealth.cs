namespace JKBridge.Core.Health;

public enum ServiceHealthState
{
    Waiting,
    Checking,
    Available,
    Unavailable,
}

public sealed record ServiceHealth(
    string ServiceId,
    ServiceHealthState State,
    DateTimeOffset CheckedAt,
    TimeSpan? Latency = null,
    int? HttpStatus = null,
    string? Detail = null);
