using JKBridge.Core.Models;

namespace JKBridge.Core.Configuration;

/// <summary>
/// Exact persisted shape of the public JK Bridge v2.1/schema-v3 configuration.
/// This type is private to the import boundary so device-specific fields cannot
/// accidentally become part of the portable Service Profile contract.
/// </summary>
internal sealed record LegacyConfigV3Document
{
    public required int SchemaVersion { get; init; }

    public required bool SetupCompleted { get; init; }

    public required string ExpectedTailnetDnsSuffix { get; init; }

    public required LegacyServerProfileV3 Server { get; init; }

    public required LegacyBehaviorSettingsV3 Behavior { get; init; }

    public required LegacyForwardAuthorizationGrantV3 Authorization { get; init; }

    public required List<LegacyServiceDefinitionV3> Services { get; init; }

    public AppConfig ToCurrentConfig() => new()
    {
        SchemaVersion = AppConfig.CurrentSchemaVersion,
        SetupCompleted = SetupCompleted,
        ExpectedTailnetDnsSuffix = ExpectedTailnetDnsSuffix,
        Server = Server.ToCurrent(),
        Behavior = Behavior.ToCurrent(),
        Authorization = Authorization.ToCurrent(),
        Agent = new AgentRegistration(),
        Services = Services.Select(service => service.ToCurrent()).ToList(),
    };
}

internal sealed record LegacyServerProfileV3
{
    public required string DisplayName { get; init; }

    public required string Host { get; init; }

    public required string User { get; init; }

    public required int Port { get; init; }

    public required string IdentityFile { get; init; }

    public required string HostKeyAlias { get; init; }

    public required string PinnedHostKey { get; init; }

    public ServerProfile ToCurrent() => new()
    {
        DisplayName = DisplayName,
        Host = Host,
        User = User,
        Port = Port,
        IdentityFile = IdentityFile,
        HostKeyAlias = HostKeyAlias,
        PinnedHostKey = PinnedHostKey,
    };
}

internal sealed record LegacyBehaviorSettingsV3
{
    public required bool AutoConnect { get; init; }

    public required bool AutoReconnect { get; init; }

    public required bool CloseToTray { get; init; }

    public required bool StartWithWindows { get; init; }

    public required ThemePreference Theme { get; init; }

    public BehaviorSettings ToCurrent() => new()
    {
        AutoConnect = AutoConnect,
        AutoReconnect = AutoReconnect,
        CloseToTray = CloseToTray,
        StartWithWindows = StartWithWindows,
        Theme = Theme,
    };
}

internal sealed record LegacyForwardAuthorizationGrantV3
{
    public required bool IsKnown { get; init; }

    public required string ClientKeyFingerprint { get; init; }

    public required List<int> RemotePorts { get; init; }

    public ForwardAuthorizationGrant ToCurrent() => new()
    {
        IsKnown = IsKnown,
        ClientKeyFingerprint = ClientKeyFingerprint,
        RemotePorts = RemotePorts.ToList(),
    };
}

internal sealed record LegacyServiceDefinitionV3
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Icon { get; init; }

    public required bool Enabled { get; init; }

    public required bool Favorite { get; init; }

    public required int LocalPort { get; init; }

    public required string RemoteHost { get; init; }

    public required int RemotePort { get; init; }

    public required string OpenUrl { get; init; }

    public required string HealthPath { get; init; }

    public required HealthCheckKind HealthKind { get; init; }

    public ServiceDefinition ToCurrent() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Icon = Icon,
        Enabled = Enabled,
        Favorite = Favorite,
        LocalPort = LocalPort,
        RemoteHost = RemoteHost,
        RemotePort = RemotePort,
        OpenUrl = OpenUrl,
        HealthPath = HealthPath,
        HealthKind = HealthKind,
    };
}
