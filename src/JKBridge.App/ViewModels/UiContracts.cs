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
    HealthCheckKind HealthKind);

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
    Task SaveSettingsAsync(SettingsDraft settings);
    Task PairDeviceAsync(PairingDraft pairing);
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
