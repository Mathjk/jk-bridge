using JKBridge.Core.Models;

namespace JKBridge.Core.Agent;

public static class AgentProtocol
{
    public const int Version = 1;
    public const int MaximumServices = 64;
    public const int MaximumDevices = 64;
    public const int MaximumPermitPorts = 32;
    public const string ProtocolVersionHeader = "X-JKBridge-Protocol-Version";
    public const string EnrollmentAuthorizationScheme = "JKBridge-Enrollment";
}

public sealed record AgentHealthResponse(
    int ProtocolVersion,
    string Status,
    string AgentVersion);

public sealed record AgentBootstrapResponse(
    int ProtocolVersion,
    string AgentVersion,
    string ServerHost,
    string TailnetDnsSuffix,
    string TailscaleIpv4,
    string TailscaleNodeId,
    string SshUser,
    int SshPort,
    string HostPublicKey,
    string DisplayName,
    bool EnrollmentAvailable,
    string CatalogRevision);

public sealed record AgentServerIdentity(
    int ProtocolVersion,
    string AgentVersion,
    string ServerHost,
    string TailnetDnsSuffix,
    string TailscaleIpv4,
    string TailscaleNodeId,
    string SshUser,
    int SshPort,
    string HostPublicKey,
    string DisplayName,
    string ServicesRevision);

public sealed record AgentEnrollmentRequest(
    string DeviceLabel,
    string ClientPublicKey,
    IReadOnlyList<string> RequestedServiceIds);

public sealed record AgentEnrollmentResponse(
    int ProtocolVersion,
    AgentServerIdentity Server,
    string DeviceId,
    string DeviceLabel,
    string Role,
    AgentCredential DeviceCredential,
    string ClientKeyFingerprint,
    IReadOnlyList<int> PermitPorts,
    string ServicesRevision)
{
    public string CatalogRevision => ServicesRevision;
}

public sealed record AgentDeviceListResponse(
    int ProtocolVersion,
    IReadOnlyList<AgentDeviceInfo> Devices);

public sealed record AgentCurrentDeviceResponse(
    int ProtocolVersion,
    string ServicesRevision,
    AgentDeviceInfo Device)
{
    public string Revision => ServicesRevision;
}

public sealed record AgentDeviceInfo(
    string DeviceId,
    string DeviceLabel,
    string ClientKeyFingerprint,
    string Role,
    string TailscaleNodeId,
    string TailscaleLogin,
    IReadOnlyList<string> ServiceIds,
    IReadOnlyList<int> PermitPorts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? RevokedAt)
{
    public string DisplayName => DeviceLabel;

    public bool Revoked => RevokedAt.HasValue;
}

public sealed record AgentDeviceRevocationResponse(
    int ProtocolVersion,
    string DeviceId,
    bool Revoked,
    DateTimeOffset RevokedAt,
    string Note);

public sealed record AgentDeviceRenameResponse(
    int ProtocolVersion,
    bool Renamed,
    string PreviousDeviceLabel,
    AgentDeviceInfo Device);

public sealed record AgentServiceCatalogResponse(
    int ProtocolVersion,
    string ServicesRevision,
    IReadOnlyList<AgentServiceDefinition> Services)
{
    public string Revision => ServicesRevision;
}

/// <summary>
/// A server-owned service template. SuggestedLocalPort remains a Windows-local
/// preference; RemoteHost and RemotePort are the exact SSH permitopen target.
/// </summary>
public sealed record AgentServiceDefinition(
    string Id,
    string Name,
    string Description,
    string Icon,
    bool EnabledByDefault,
    bool FavoriteByDefault,
    int SuggestedLocalPort,
    string RemoteHost,
    int RemotePort,
    string Scheme,
    string OpenPath,
    string HealthPath,
    string HealthKind)
{
    public ServiceDefinition ToServiceDefinition(int? localPortOverride = null)
    {
        var localPort = localPortOverride ?? SuggestedLocalPort;
        if (localPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localPortOverride),
                "Windows local port must be between 1 and 65535.");
        }

        var healthKind = HealthKind switch
        {
            AgentHealthKinds.HttpStatus => HealthCheckKind.HttpStatus,
            AgentHealthKinds.JsonOk => HealthCheckKind.JsonOk,
            AgentHealthKinds.TcpConnect => HealthCheckKind.TcpConnect,
            _ => throw new InvalidOperationException("Agent service health kind is invalid."),
        };

        return new ServiceDefinition
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Icon = Icon,
            Enabled = EnabledByDefault,
            Favorite = FavoriteByDefault,
            LocalPort = localPort,
            RemoteHost = RemoteHost,
            RemotePort = RemotePort,
            OpenUrl = $"{Scheme}://{ServiceDefinition.LoopbackHost}:{localPort}{OpenPath}",
            HealthPath = HealthPath,
            HealthKind = healthKind,
        };
    }
}

public static class AgentHealthKinds
{
    public const string HttpStatus = "httpStatus";
    public const string JsonOk = "jsonOk";
    public const string TcpConnect = "tcpConnect";
}

public sealed record AgentClientOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public int MaximumRequestBytes { get; init; } = 64 * 1024;

    public int MaximumResponseBytes { get; init; } = 64 * 1024;
}
