namespace JKBridge.Core.Models;

/// <summary>
/// A browser endpoint served directly by this Windows computer. It never
/// participates in SSH forwarding, Linux Agent catalogs, or permitopen grants.
/// </summary>
public sealed record WindowsLocalServiceDefinition
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Icon { get; init; } = "Computer";

    public bool Enabled { get; init; } = true;

    public bool Favorite { get; init; }

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; }

    public string Scheme { get; init; } = Uri.UriSchemeHttp;

    public string LaunchPath { get; init; } = "/";

    public HealthCheckKind HealthKind { get; init; } = HealthCheckKind.HttpStatus;

    /// <summary>Advisory display hint only; never an authorization identity.</summary>
    public string ProcessNameHint { get; init; } = string.Empty;
}
