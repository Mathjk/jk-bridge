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
    string HealthPath = "/",
    string RestartPath = "",
    string RestartToken = "",
    int RestartLocalPort = 0,
    string LastRestartText = "");

/// <summary>
/// Request for the reusable modal confirmation dialog.
/// </summary>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string ConfirmText = "确认",
    bool IsDangerous = true);

/// <summary>
/// A service that just transitioned into a broken phase.
/// </summary>
public sealed record ServiceAlert(
    string ServiceId,
    string ServiceName,
    string Title,
    string Detail);

public sealed record WindowsLocalServiceUiState(
    string Id,
    string Name,
    string Description,
    string Host,
    int Port,
    string Scheme,
    string LaunchPath,
    string ProcessName,
    string Status,
    string Detail,
    bool IsEnabled,
    bool IsAvailable,
    bool HasWarning,
    bool CanOpen,
    HealthCheckKind HealthKind)
{
    public string LocalUrl =>
        JKBridge.Core.Routing.WindowsLocalServiceUrl.Build(Host, Scheme, Port, LaunchPath);
}

public sealed record DiscoveredWindowsLocalServiceUiState(
    string Key,
    string Host,
    int Port,
    string ProcessName,
    string Protocol,
    string OpenUrl,
    string Status,
    string Detail,
    bool IsWeb,
    bool CanSave,
    bool IsExistingTunnel,
    bool CertificateUnverified)
{
    public string Address => Host == "::1" ? $"[::1]:{Port}" : $"{Host}:{Port}";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ProcessName) ? $"本机端口 {Port}" : ProcessName;
}

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
    int ThemeIndex,
    int UpdateNetworkModeIndex,
    string UpdateManualProxyUrl,
    int HealthCheckIntervalSeconds = 20);

public sealed record PairingDraft(
    string PairingCode,
    string IdentityFile);
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
    Task RestartServiceAsync(string serviceId);
    Task OpenServiceLogAsync(string serviceId);
    Task<bool> ConfirmAsync(ConfirmRequest request);
    Task CopyAddressAsync(string localUrl);
    Task SetServiceEnabledAsync(string serviceId, bool enabled);
    Task AddServiceAsync();
    Task SaveServiceAsync(ServiceUiState service);
    Task RemoveServiceAsync(string serviceId);
    Task ScanWindowsLocalServicesAsync() => Task.CompletedTask;
    void CancelWindowsLocalServiceScan()
    {
    }
    Task AddWindowsLocalServiceAsync() => Task.CompletedTask;
    Task SaveDiscoveredWindowsLocalServiceAsync(
        DiscoveredWindowsLocalServiceUiState service) => Task.CompletedTask;
    Task OpenDiscoveredWindowsLocalServiceAsync(
        DiscoveredWindowsLocalServiceUiState service) => Task.CompletedTask;
    Task SaveWindowsLocalServiceAsync(
        WindowsLocalServiceUiState service) => Task.CompletedTask;
    Task RemoveWindowsLocalServiceAsync(string serviceId) => Task.CompletedTask;
    Task OpenWindowsLocalServiceAsync(string serviceId) => Task.CompletedTask;
    Task RetryWindowsLocalServiceAsync(string serviceId) => Task.CompletedTask;
    Task RunDiagnosticsAsync();
    Task CopyDiagnosticSummaryAsync();
    Task OpenLogFolderAsync();
    Task ExportDiagnosticBundleAsync() => Task.CompletedTask;
    Task CheckForUpdatesAsync() => Task.CompletedTask;
    Task InstallCheckedReleaseAsync() => Task.CompletedTask;
    Task OpenReleasePageAsync() => Task.CompletedTask;
    void CancelReleaseUpdate()
    {
    }
    Task LaunchDownloadedReleaseInstallerAsync() => Task.CompletedTask;
    void PreviewTheme(ThemePreference theme)
    {
    }
    Task SaveSettingsAsync(SettingsDraft settings);
    Task CompleteWelcomeTutorialAsync() => Task.CompletedTask;
    Task CopyLinuxQuickPairCommandAsync() => Task.CompletedTask;
    Task PairDeviceAsync(PairingDraft pairing);

    Task ExportServiceProfileAsync() => Task.CompletedTask;
    Task PreviewServiceProfileImportAsync() => Task.CompletedTask;
    Task ApplyServiceProfileImportAsync(bool confirmed) => Task.CompletedTask;

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
    public Task RestartServiceAsync(string serviceId) => Task.CompletedTask;
    public Task OpenServiceLogAsync(string serviceId) => Task.CompletedTask;
    public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(false);
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
