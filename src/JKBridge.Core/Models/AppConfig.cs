namespace JKBridge.Core.Models;

public sealed record AppConfig
{
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool SetupCompleted { get; init; }

    public string ExpectedTailnetDnsSuffix { get; init; } = string.Empty;

    public ServerProfile Server { get; init; } = new();

    public BehaviorSettings Behavior { get; init; } = new();

    public ForwardAuthorizationGrant Authorization { get; init; } = new();

    public AgentRegistration Agent { get; init; } = new();

    public List<ServiceDefinition> Services { get; init; } = CreateDefaultServices();

    public List<WindowsLocalServiceDefinition> WindowsLocalServices { get; init; } = [];

    public static AppConfig CreateDefault() => new();

    private static List<ServiceDefinition> CreateDefaultServices() =>
    [
        new()
        {
            Id = "overleaf",
            Name = "Overleaf",
            Description = "论文协作与 LaTeX 编辑",
            Icon = "Document",
            Enabled = true,
            Favorite = true,
            LocalPort = 8080,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 80,
            OpenUrl = "http://127.0.0.1:8080/project",
            HealthPath = "/project",
            HealthKind = HealthCheckKind.HttpStatus,
        },
        new()
        {
            Id = "gpu-dashboard",
            Name = "GPU Dashboard",
            Description = "显卡状态与任务监控",
            Icon = "Gauge",
            Enabled = true,
            Favorite = true,
            LocalPort = 8765,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 8765,
            OpenUrl = "http://127.0.0.1:8765/",
            HealthPath = "/api/health",
            HealthKind = HealthCheckKind.JsonOk,
        },
        new()
        {
            Id = "investment-news",
            Name = "Investment News",
            Description = "A股产业链资讯与 AI 每日要点",
            Icon = "Newspaper",
            Enabled = true,
            Favorite = true,
            LocalPort = 8793,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 8793,
            OpenUrl = "http://127.0.0.1:8793/",
            HealthPath = "/",
            HealthKind = HealthCheckKind.HttpStatus,
        },
    ];
}

public sealed record ServerProfile
{
    public string DisplayName { get; init; } = "Linux workstation";

    public string Host { get; init; } = string.Empty;

    public string User { get; init; } = string.Empty;

    public int Port { get; init; } = 22;

    public string IdentityFile { get; init; } = "%USERPROFILE%\\.ssh\\jk_bridge_ed25519";

    public string HostKeyAlias { get; init; } = "jk-bridge-linux";

    public string PinnedHostKey { get; init; } = string.Empty;
}

public sealed record BehaviorSettings
{
    public bool AutoConnect { get; init; } = true;

    public bool AutoReconnect { get; init; } = true;

    public bool CloseToTray { get; init; } = true;

    public bool StartWithWindows { get; init; }

    public ThemePreference Theme { get; init; } = ThemePreference.System;
}

public sealed record ForwardAuthorizationGrant
{
    public bool IsKnown { get; init; }

    public string ClientKeyFingerprint { get; init; } = string.Empty;

    public List<int> RemotePorts { get; init; } = [];
}

/// <summary>
/// Non-secret reference to this Windows device's optional Linux Agent registration.
/// The enrollment credential itself belongs in the operating-system credential store.
/// </summary>
public sealed record AgentRegistration
{
    public bool IsConfigured { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    public string TailscaleNodeId { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public string DeviceLabel { get; init; } = string.Empty;

    public string CredentialReference { get; init; } = string.Empty;

    public string CatalogRevision { get; init; } = string.Empty;
}

public sealed record ServiceDefinition
{
    public const string LoopbackHost = "127.0.0.1";

    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Icon { get; init; } = "Globe";

    public bool Enabled { get; init; } = true;

    public bool Favorite { get; init; }

    public int LocalPort { get; init; }

    public string RemoteHost { get; init; } = LoopbackHost;

    public int RemotePort { get; init; }

    public string OpenUrl { get; init; } = string.Empty;

    public string HealthPath { get; init; } = "/";

    public HealthCheckKind HealthKind { get; init; } = HealthCheckKind.HttpStatus;
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum HealthCheckKind
{
    HttpStatus,
    JsonOk,
    TcpConnect,
}
