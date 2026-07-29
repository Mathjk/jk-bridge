using JKBridge.Core.Models;

namespace JKBridge.App.ViewModels;

public sealed record BridgeUiState(
    string Status,
    string Summary,
    string ActionText,
    bool IsConnected,
    bool IsConnecting,
    bool IsDegraded,
    string PeerName,
    string PeerDnsName,
    string AvailableServicesText,
    string LastUpdatedText,
    bool HasIssue,
    string IssueTitle,
    string IssueDetail);

public sealed record ServiceUiState(
    string Id,
    string ShortCode,
    string Name,
    string Description,
    string LocalUrl,
    int LocalPort,
    int RemotePort,
    string Status,
    string HealthDetail,
    bool IsEnabled,
    bool IsAvailable,
    bool HasWarning,
    bool CanOpen,
    HealthCheckKind HealthKind,
    string HealthPath = "/");

public sealed record DiagnosticUiState(
    string Time,
    string Level,
    string Title,
    string Detail,
    string Kind);

public sealed record SettingsDraft(
    string LinuxHost,
    string LinuxUser,
    string SshPort,
    string IdentityFile,
    string PinnedHostKey,
    bool AutoConnect,
    bool AutoReconnect,
    bool CloseToTray,
    bool StartWithWindows,
    int ThemeIndex);

public sealed record PairingDraft(
    string PairingCode,
    string IdentityFile);

public sealed record AgentEnrollmentDraft(
    string Endpoint,
    string RecoveryCode,
    string DeviceLabel,
    string IdentityFile);

public sealed record AgentDeviceUiState(
    string DeviceId,
    string DeviceLabel,
    string Role,
    string TailscaleLogin,
    string ClientKeyFingerprint,
    string LastSeenText,
    bool IsCurrent,
    bool IsRevoked)
{
    public string DisplayText =>
        $"{DeviceLabel} · {(Role == "admin" ? "管理员" : "设备")}" +
        (IsCurrent ? " · 当前 Windows" : string.Empty) +
        (IsRevoked ? " · 已撤销" : string.Empty);
}

public sealed record AgentRelayDraft(
    string Slug,
    string ServiceName,
    string Description,
    string LinuxRelayPort,
    string TargetHost,
    string TargetPort,
    bool UseHttps,
    string OpenPath,
    string WindowsLocalPort,
    bool GrantToAllDevices);

public sealed record AgentDiscoveryCandidateUiState(
    string Endpoint,
    string DisplayName,
    string ServerHost,
    string AgentVersion,
    bool EnrollmentAvailable,
    string TailscaleNodeId = "")
{
    public string DisplayText =>
        $"{DisplayName} · {ServerHost} · Agent {AgentVersion}" +
        (EnrollmentAvailable ? " · 可登记" : " · 未开放登记");
}

/// <summary>
/// Runtime adapter boundary for the desktop presentation layer.
/// Implementations own process lifecycle, persistence, browser launch and clipboard access.
/// </summary>
public interface IBridgeUiActions
{
    Task ConnectAsync();
    Task DisconnectAsync();
    Task RefreshAsync();
    Task RetryAsync();
    Task OpenServiceAsync(string serviceId, string localUrl);
    Task RetryServiceAsync(string serviceId);
    Task CopyAddressAsync(string localUrl);
    Task SetServiceEnabledAsync(string serviceId, bool enabled);
    Task AddServiceAsync();
    Task SaveServiceAsync(ServiceUiState service);
    Task RemoveServiceAsync(string serviceId);
    Task RunDiagnosticsAsync();
    Task CopyDiagnosticSummaryAsync();
    Task OpenLogFolderAsync();
    Task ExportDiagnosticBundleAsync() => Task.CompletedTask;
    Task CheckForUpdatesAsync() => Task.CompletedTask;
    Task InstallCheckedReleaseAsync() => Task.CompletedTask;
    Task SaveSettingsAsync(SettingsDraft settings);
    Task PairDeviceAsync(PairingDraft pairing);

    /// <summary>
    /// Explicitly enrolls this Windows device with an optional Linux Agent.
    /// Implementations must treat <see cref="AgentEnrollmentDraft.RecoveryCode"/> as a secret.
    /// </summary>
    Task EnrollWithAgentAsync(AgentEnrollmentDraft enrollment) => Task.CompletedTask;
    Task SyncAgentCatalogAsync() => Task.CompletedTask;
    Task RefreshAgentDevicesAsync() => Task.CompletedTask;
    Task RevokeAgentDeviceAsync(string deviceId, bool confirmed) => Task.CompletedTask;
    Task ExportServiceProfileAsync() => Task.CompletedTask;
    Task PreviewServiceProfileImportAsync() => Task.CompletedTask;
    Task ApplyServiceProfileImportAsync(bool confirmed) => Task.CompletedTask;
    Task PlanAgentRelayAsync(AgentRelayDraft draft) => Task.CompletedTask;
    Task PlanAgentRelayDeletionAsync(string slug) => Task.CompletedTask;
    Task ApplyAgentRelayPlanAsync(bool confirmed) => Task.CompletedTask;
    Task GenerateAgentEnrollmentCodeAsync() => Task.CompletedTask;
    Task CopyAgentEnrollmentCodeAsync(string enrollmentCode) => Task.CompletedTask;
    Task DiscoverAgentsAsync() => Task.CompletedTask;
    Task PreviewAgentCatalogUpdateAsync(bool applyDefaultsToAllDevices) => Task.CompletedTask;
    Task ApplyAgentCatalogUpdateAsync(bool confirmed) => Task.CompletedTask;
    Task RenameAgentDeviceAsync(string deviceId, string deviceLabel) => Task.CompletedTask;
    Task RebindAgentEndpointAsync(
        AgentDiscoveryCandidateUiState candidate,
        bool confirmed) => Task.CompletedTask;
    void InvalidateTransientSetupState()
    {
    }
}

internal sealed class NullBridgeUiActions : IBridgeUiActions
{
    public static NullBridgeUiActions Instance { get; } = new();

    private NullBridgeUiActions()
    {
    }

    public Task ConnectAsync() => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task RefreshAsync() => Task.CompletedTask;
    public Task RetryAsync() => Task.CompletedTask;
    public Task OpenServiceAsync(string serviceId, string localUrl) => Task.CompletedTask;
    public Task RetryServiceAsync(string serviceId) => Task.CompletedTask;
    public Task CopyAddressAsync(string localUrl) => Task.CompletedTask;
    public Task SetServiceEnabledAsync(string serviceId, bool enabled) => Task.CompletedTask;
    public Task AddServiceAsync() => Task.CompletedTask;
    public Task SaveServiceAsync(ServiceUiState service) => Task.CompletedTask;
    public Task RemoveServiceAsync(string serviceId) => Task.CompletedTask;
    public Task RunDiagnosticsAsync() => Task.CompletedTask;
    public Task CopyDiagnosticSummaryAsync() => Task.CompletedTask;
    public Task OpenLogFolderAsync() => Task.CompletedTask;
    public Task SaveSettingsAsync(SettingsDraft settings) => Task.CompletedTask;
    public Task PairDeviceAsync(PairingDraft pairing) => Task.CompletedTask;
}
