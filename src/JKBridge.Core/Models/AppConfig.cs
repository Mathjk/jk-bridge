using System.Text.Json.Serialization;

namespace JKBridge.Core.Models;

public sealed record AppConfig
{
    public const int CurrentSchemaVersion = 7;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool SetupCompleted { get; init; }

    public string ExpectedTailnetDnsSuffix { get; init; } = string.Empty;

    public ServerProfile Server { get; init; } = new();

    public BehaviorSettings Behavior { get; init; } = new();

    public ForwardAuthorizationGrant Authorization { get; init; } = new();

    public UpdateNetworkSettings UpdateNetwork { get; init; } = new();


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
            RestartPath = "/api/restart",
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WelcomeTutorialCompleted { get; init; }

    /// <summary>
    /// Service health-check interval in seconds. Applied to the running
    /// supervisor without reconnecting.
    /// </summary>
    public int HealthCheckIntervalSeconds { get; init; } = 20;
}

public sealed record UpdateNetworkSettings
{
    public UpdateNetworkMode Mode { get; init; } = UpdateNetworkMode.System;

    public string ManualProxyUrl { get; init; } = string.Empty;
}

public enum UpdateNetworkMode
{
    System,
    ManualProxy,
    Direct,
}

public sealed record ForwardAuthorizationGrant
{
    public bool IsKnown { get; init; }

    public string ClientKeyFingerprint { get; init; } = string.Empty;

    public List<int> RemotePorts { get; init; } = [];
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

    /// <summary>
    /// Optional loopback-only restart action. When set, the desktop shows a restart
    /// button that POSTs to this path on the Windows-local tunnel port. The path is
    /// not a secret and may travel in a portable service profile; the token never does.
    /// </summary>
    public string RestartPath { get; init; } = string.Empty;

    /// <summary>
    /// Optional Windows-local port for the restart request. Zero inherits the
    /// service's own local port. A non-zero value must match another configured
    /// service's local port so the request travels through an existing tunnel —
    /// this lets one shared control endpoint restart several services.
    /// </summary>
    public int RestartLocalPort { get; init; }

    /// <summary>
    /// Shared secret sent as the X-JKBridge-Restart-Token header. Stays in this
    /// installation's config only.
    /// </summary>
    public string RestartToken { get; init; } = string.Empty;

    /// <summary>
    /// Opaque reference to the restart token inside the operating-system
    /// credential store. When set, <see cref="RestartToken"/> stays empty and
    /// config.json never carries the secret.
    /// </summary>
    public string RestartTokenReference { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public bool HasRestartAction => !string.IsNullOrWhiteSpace(RestartPath);
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
