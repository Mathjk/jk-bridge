namespace JKBridge.Core.Models;

/// <summary>
/// A deliberately limited, portable description of JK Bridge services.
/// It cannot contain a server identity, private-key path, pairing token, or
/// authorization receipt.
/// </summary>
public sealed record ServiceProfile
{
    public const string ExpectedFormat = "jkbridge-service-profile";

    public const int CurrentFormatVersion = 1;

    public string Format { get; init; } = ExpectedFormat;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public string DisplayName { get; init; } = "JK Bridge services";

    public List<ServiceProfileEntry> Services { get; init; } = [];
}

/// <summary>
/// The explicit allow-list of service properties that may leave an installation.
/// Linux targets are always reconstructed as 127.0.0.1 during import.
/// </summary>
public sealed record ServiceProfileEntry
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Icon { get; init; } = "Globe";

    public bool Enabled { get; init; } = true;

    public bool Favorite { get; init; }

    public int WindowsLocalPort { get; init; }

    public int LinuxLoopbackPort { get; init; }

    public string OpenUrl { get; init; } = string.Empty;

    public string HealthPath { get; init; } = "/";

    public HealthCheckKind HealthKind { get; init; } = HealthCheckKind.HttpStatus;
}
