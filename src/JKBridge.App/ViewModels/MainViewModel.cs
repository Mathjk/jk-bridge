using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JKBridge.App.Services;
using JKBridge.Core.Models;

namespace JKBridge.App.ViewModels;

public enum AppPage
{
    Setup,
    Overview,
    Services,
    Diagnostics,
    Settings,
}

/// <summary>
/// Window-level presentation state. Runtime code supplies actions through
/// <see cref="IBridgeUiActions"/> and pushes sanitized state through the Apply/Set methods.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IBridgeUiActions _actions;

    [ObservableProperty]
    private AppPage _selectedPage = AppPage.Overview;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isDegraded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _connectionStatus = "未连接";

    [ObservableProperty]
    private string _connectionSummary = "正在检查运行环境…";

    [ObservableProperty]
    private string _connectionActionText = "连接全部";

    [ObservableProperty]
    private string _linuxPeerName = "尚未配对";

    [ObservableProperty]
    private string _linuxPeerDnsName = "运行 Linux 配对脚本后即可一键配置";

    [ObservableProperty]
    private string _availableServicesText = "0 / 2 可用";

    [ObservableProperty]
    private string _lastUpdatedText = "等待首次检查";

    [ObservableProperty]
    private bool _hasIssue;

    [ObservableProperty]
    private string _issueTitle = "等待环境检查";

    [ObservableProperty]
    private string _issueDetail = "JK Bridge 正在确认 Tailscale、OpenSSH 与本地端口。";

    [ObservableProperty]
    private ServiceCardViewModel? _selectedService;

    // Diagnostics presentation fields.
    [ObservableProperty] private string _tailscaleExecutable = "正在查找 tailscale.exe…";
    [ObservableProperty] private string _tailscaleIdentity = "尚未检查";
    [ObservableProperty] private string _tailnetName = "尚未检查";
    [ObservableProperty] private string _peerRoute = "等待 Tailscale 状态";
    [ObservableProperty] private string _sshExecutable = "正在查找 ssh.exe…";
    [ObservableProperty] private string _hostFingerprint = "尚未固定";
    [ObservableProperty] private string _sshProcess = "未启动";
    [ObservableProperty] private string _tailscaleStatusText = "待检查";
    [ObservableProperty] private string _linuxStatusText = "待检查";
    [ObservableProperty] private string _sshStatusText = "待检查";
    [ObservableProperty] private bool _isTailscaleHealthy;
    [ObservableProperty] private bool _hasTailscaleIssue;
    [ObservableProperty] private bool _isLinuxHealthy;
    [ObservableProperty] private bool _hasLinuxIssue;
    [ObservableProperty] private bool _isSshHealthy;
    [ObservableProperty] private bool _hasSshIssue;
    [ObservableProperty] private bool _isDiagnosticBundleBusy;
    [ObservableProperty] private string _diagnosticBundleFeedback = "诊断包仅包含允许列出的匿名状态和脱敏日志；导出前由你选择保存位置。";

    // Editable settings draft. Persistence belongs to the injected adapter.
    [ObservableProperty] private string _linuxHost = string.Empty;
    [ObservableProperty] private string _linuxUser = string.Empty;
    [ObservableProperty] private string _sshPort = "22";
    [ObservableProperty] private string _identityFile = @"%USERPROFILE%\.ssh\jk_bridge_ed25519";
    [ObservableProperty] private string _pinnedHostKey = string.Empty;
    [ObservableProperty] private bool _autoConnect = true;
    [ObservableProperty] private bool _autoReconnect = true;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private string _settingsFeedback = "设置保存在当前 Windows 用户的应用数据目录。";
    [ObservableProperty] private bool _isUpdateChecking;
    [ObservableProperty] private bool _hasUpdateReleaseInfo;
    [ObservableProperty] private string _updateCheckFeedback = "仅在你点击后查询 JK Bridge 官方 GitHub Releases；不会下载或安装任何内容。";
    [ObservableProperty] private string _updateReleaseUrl = string.Empty;
    [ObservableProperty] private string _serviceFeedback = "远端主机固定为 127.0.0.1，不会暴露给局域网。";

    // One-shot Windows ↔ Linux pairing state.
    [ObservableProperty] private string _pairingCode = string.Empty;
    [ObservableProperty] private string _pairingIdentityFile = @"%USERPROFILE%\.ssh\jk_bridge_ed25519";
    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private bool _setupCompleted;
    [ObservableProperty] private string _setupFeedback = "先在 Linux 运行配对脚本，再粘贴一次性配对码。";
    [ObservableProperty] private string _pairingPrerequisiteStatus = "等待开始";
    [ObservableProperty] private string _pairingKeyStatus = "等待开始";
    [ObservableProperty] private string _pairingLinuxStatus = "等待开始";
    [ObservableProperty] private string _pairingFinishStatus = "等待开始";
    [ObservableProperty] private string _configuredDeviceText = "尚未配置 Linux 设备";

    // Optional Linux Agent enrollment. The recovery code stays only in this transient UI draft.
    [ObservableProperty] private string _agentEndpoint = string.Empty;
    [ObservableProperty] private string _agentRecoveryCode = string.Empty;
    [ObservableProperty] private string _agentDeviceLabel = Environment.MachineName;
    [ObservableProperty] private bool _isAgentEnrolling;
    [ObservableProperty] private string _agentEnrollmentFeedback = "Agent 是可选项；不可用时仍可使用下方的一次性配对。";
    [ObservableProperty] private string _agentConnectionStatus = "等待开始";
    [ObservableProperty] private string _agentKeyStatus = "等待开始";
    [ObservableProperty] private string _agentAuthorizationStatus = "等待开始";
    [ObservableProperty] private string _agentCatalogStatus = "等待开始";
    [ObservableProperty] private bool _isAgentDiscovering;
    [ObservableProperty] private string _agentDiscoveryFeedback = "可只读发现当前 tailnet 在线设备上已运行的 JK Bridge Agent。";
    [ObservableProperty] private AgentDiscoveryCandidateUiState? _selectedAgentDiscoveryCandidate;
    [ObservableProperty] private bool _agentEndpointRebindConfirmed;
    [ObservableProperty] private bool _isAgentConfigured;
    [ObservableProperty] private bool _isAgentManaging;
    [ObservableProperty] private bool _currentAgentDeviceIsAdmin;
    [ObservableProperty] private string _agentManagementFeedback = "完成 Agent 登记后，可在这里同步服务和管理其他设备。";
    [ObservableProperty] private AgentDeviceUiState? _selectedAgentDevice;
    [ObservableProperty] private bool _agentRevokeConfirmed;
    [ObservableProperty] private string _agentRenameDeviceLabel = string.Empty;
    [ObservableProperty] private string _agentEnrollmentCode = string.Empty;
    [ObservableProperty] private string _agentEnrollmentCodeExpiry = string.Empty;
    [ObservableProperty] private bool _hasAgentEnrollmentCode;
    [ObservableProperty] private string _agentEnrollmentCodeFeedback = "管理员可生成 10 分钟内有效、仅可使用一次的普通设备添加码。";
    [ObservableProperty] private string _agentRelaySlug = string.Empty;
    [ObservableProperty] private string _agentRelayServiceName = string.Empty;
    [ObservableProperty] private string _agentRelayDescription = string.Empty;
    [ObservableProperty] private string _agentRelayLinuxPort = string.Empty;
    [ObservableProperty] private string _agentRelayTargetHost = string.Empty;
    [ObservableProperty] private string _agentRelayTargetPort = "443";
    [ObservableProperty] private bool _agentRelayUseHttps = true;
    [ObservableProperty] private string _agentRelayOpenPath = "/";
    [ObservableProperty] private string _agentRelayWindowsPort = string.Empty;
    [ObservableProperty] private bool _agentRelayGrantToAllDevices = true;
    [ObservableProperty] private bool _hasAgentRelayPreview;
    [ObservableProperty] private bool _agentRelayPlanCanApply;
    [ObservableProperty] private bool _agentRelayConfirmed;
    [ObservableProperty] private string _agentRelayPreviewSummary = "尚未生成只读计划。";
    [ObservableProperty] private string _agentRelayFeedback = "高级功能：由 Linux 建立一个仅监听 127.0.0.1 的显式 TCP 中继。仅 Agent 管理员可操作。";
    [ObservableProperty] private bool _agentCatalogApplyDefaultsToAllDevices = true;
    [ObservableProperty] private bool _hasAgentCatalogUpdatePreview;
    [ObservableProperty] private bool _agentCatalogUpdateCanApply;
    [ObservableProperty] private bool _agentCatalogUpdateConfirmed;
    [ObservableProperty] private string _agentCatalogUpdateSummary = "尚未预览本机与 Linux Agent 的服务清单差异。";
    [ObservableProperty] private string _agentCatalogUpdateFeedback = "管理员可先只读预览，再把本机直接 Linux 回环服务清单提交给 Agent。";
    [ObservableProperty] private bool _isServiceProfileBusy;
    [ObservableProperty] private bool _hasServiceProfileImportPreview;
    [ObservableProperty] private bool _serviceProfileImportConfirmed;
    [ObservableProperty] private string _serviceProfileFeedback = "服务配置文件只包含名称、Windows/Linux 端口、打开路径和健康检查；不会包含账号、密钥、Agent 凭据或服务器身份。";
    [ObservableProperty] private string _serviceProfileImportSummary = "尚未选择导入文件。";

    public MainViewModel()
        : this(NullBridgeUiActions.Instance)
    {
        LoadDesignData();
    }

    public MainViewModel(IBridgeUiActions actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        SetServices(
        [
            new ServiceUiState(
                "overleaf", "OL", "Overleaf", "论文协作与 LaTeX 编辑",
                "http://127.0.0.1:8080/project", 8080, 80,
                "等待隧道", "尚未检查", true, false, false, false, HealthCheckKind.HttpStatus),
            new ServiceUiState(
                "gpu-dashboard", "GPU", "GPU Dashboard", "显卡状态与任务监控",
                "http://127.0.0.1:8765/", 8765, 8765,
                "等待隧道", "尚未检查", true, false, false, false, HealthCheckKind.JsonOk),
            new ServiceUiState(
                "investment-news", "NEWS", "Investment News", "A股产业链资讯与 AI 每日要点",
                "http://127.0.0.1:8793/", 8793, 8793,
                "等待隧道", "尚未检查", true, false, false, false, HealthCheckKind.HttpStatus),
        ]);
    }

    public ObservableCollection<ServiceCardViewModel> Services { get; } = [];

    public ObservableCollection<DiagnosticItemViewModel> Diagnostics { get; } = [];

    public ObservableCollection<AgentDeviceUiState> AgentDevices { get; } = [];
    public ObservableCollection<AgentDiscoveryCandidateUiState> AgentDiscoveryCandidates { get; } = [];

    public bool IsOverview => SelectedPage == AppPage.Overview;
    public bool IsSetup => SelectedPage == AppPage.Setup;
    public bool IsServices => SelectedPage == AppPage.Services;
    public bool IsDiagnostics => SelectedPage == AppPage.Diagnostics;
    public bool IsSettings => SelectedPage == AppPage.Settings;
    public bool IsStopped => !IsConnected && !IsConnecting && !IsDegraded;
    public bool HasNoIssue => !HasIssue;
    public bool CanExportDiagnosticBundle => !IsDiagnosticBundleBusy;
    public bool CanCheckForUpdates => !IsUpdateChecking;
    public bool CanStartPairing =>
        !IsPairing && !IsAgentEnrolling && !IsAgentDiscovering && !IsAgentManaging && !IsServiceProfileBusy;
    public bool CanStartAgentEnrollment =>
        !IsAgentEnrolling && !IsAgentDiscovering && !IsPairing && !IsAgentManaging && !IsServiceProfileBusy;
    public bool CanManageAgent =>
        IsAgentConfigured && !IsAgentManaging && !IsAgentEnrolling && !IsAgentDiscovering && !IsPairing && !IsServiceProfileBusy;
    public bool CanRevokeAgentDevice =>
        CanManageAgent &&
        CurrentAgentDeviceIsAdmin &&
        AgentRevokeConfirmed &&
        SelectedAgentDevice is { IsCurrent: false, IsRevoked: false };
    public bool CanRenameAgentDevice =>
        CanManageAgent && CurrentAgentDeviceIsAdmin &&
        SelectedAgentDevice is { IsRevoked: false } selected &&
        !string.IsNullOrWhiteSpace(AgentRenameDeviceLabel) &&
        !string.Equals(selected.DeviceLabel, AgentRenameDeviceLabel, StringComparison.Ordinal);
    public bool CanTransferServiceProfile =>
        !IsServiceProfileBusy && !IsPairing && !IsAgentEnrolling && !IsAgentDiscovering && !IsAgentManaging;
    public bool CanApplyServiceProfileImport =>
        CanTransferServiceProfile && HasServiceProfileImportPreview && ServiceProfileImportConfirmed;
    public bool CanApplyAgentRelayPlan =>
        CanManageAgent && HasAgentRelayPreview && AgentRelayPlanCanApply && AgentRelayConfirmed;
    public bool CanCopyAgentEnrollmentCode => HasAgentEnrollmentCode && !string.IsNullOrWhiteSpace(AgentEnrollmentCode);
    public bool CanDiscoverAgents =>
        !IsAgentDiscovering && !IsPairing && !IsAgentEnrolling && !IsAgentManaging && !IsServiceProfileBusy;
    public bool CanUseDiscoveredAgent =>
        CanDiscoverAgents && SelectedAgentDiscoveryCandidate is not null;
    public bool CanRebindAgentEndpoint =>
        IsAgentConfigured && CanUseDiscoveredAgent && AgentEndpointRebindConfirmed;
    public bool CanApplyAgentCatalogUpdate =>
        CanManageAgent && HasAgentCatalogUpdatePreview &&
        AgentCatalogUpdateCanApply && AgentCatalogUpdateConfirmed;

    partial void OnSelectedPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsSetup));
        OnPropertyChanged(nameof(IsServices));
        OnPropertyChanged(nameof(IsDiagnostics));
        OnPropertyChanged(nameof(IsSettings));
        if (value != AppPage.Setup && HasAgentEnrollmentCode)
        {
            ClearAgentEnrollmentCode("添加码已从界面清除；如仍需添加设备，请重新生成。");
        }
        if (value != AppPage.Setup)
        {
            _actions.InvalidateTransientSetupState();
            ClearAgentRelayPreview();
            ClearAgentCatalogUpdatePreview();
            ClearServiceProfilePreview();
            AgentEndpointRebindConfirmed = false;
        }
    }

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnIsDegradedChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnHasIssueChanged(bool value) => OnPropertyChanged(nameof(HasNoIssue));
    partial void OnIsDiagnosticBundleBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanExportDiagnosticBundle));
    partial void OnIsUpdateCheckingChanged(bool value) =>
        OnPropertyChanged(nameof(CanCheckForUpdates));
    partial void OnIsPairingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartPairing));
        OnPropertyChanged(nameof(CanStartAgentEnrollment));
        OnPropertyChanged(nameof(CanManageAgent));
        OnPropertyChanged(nameof(CanRevokeAgentDevice));
        OnPropertyChanged(nameof(CanTransferServiceProfile));
        OnPropertyChanged(nameof(CanApplyServiceProfileImport));
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));
        OnPropertyChanged(nameof(CanDiscoverAgents));
        OnPropertyChanged(nameof(CanUseDiscoveredAgent));
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));
    }

    partial void OnIsAgentEnrollingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartPairing));
        OnPropertyChanged(nameof(CanStartAgentEnrollment));
        OnPropertyChanged(nameof(CanManageAgent));
        OnPropertyChanged(nameof(CanRevokeAgentDevice));
        OnPropertyChanged(nameof(CanTransferServiceProfile));
        OnPropertyChanged(nameof(CanApplyServiceProfileImport));
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));
        OnPropertyChanged(nameof(CanDiscoverAgents));
        OnPropertyChanged(nameof(CanUseDiscoveredAgent));
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));
    }

    partial void OnIsAgentConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(CanManageAgent));
        OnPropertyChanged(nameof(CanRevokeAgentDevice));
        OnPropertyChanged(nameof(CanRenameAgentDevice));
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));
        OnPropertyChanged(nameof(CanDiscoverAgents));
        OnPropertyChanged(nameof(CanUseDiscoveredAgent));
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));
        OnPropertyChanged(nameof(CanRebindAgentEndpoint));
    }

    partial void OnIsAgentManagingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartPairing));
        OnPropertyChanged(nameof(CanStartAgentEnrollment));
        OnPropertyChanged(nameof(CanManageAgent));
        OnPropertyChanged(nameof(CanRevokeAgentDevice));
        OnPropertyChanged(nameof(CanRenameAgentDevice));
        OnPropertyChanged(nameof(CanTransferServiceProfile));
        OnPropertyChanged(nameof(CanApplyServiceProfileImport));
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));
        OnPropertyChanged(nameof(CanDiscoverAgents));
        OnPropertyChanged(nameof(CanUseDiscoveredAgent));
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));
        OnPropertyChanged(nameof(CanRebindAgentEndpoint));
    }

    partial void OnCurrentAgentDeviceIsAdminChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRevokeAgentDevice));
        OnPropertyChanged(nameof(CanRenameAgentDevice));
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));
        OnPropertyChanged(nameof(CanDiscoverAgents));
        OnPropertyChanged(nameof(CanUseDiscoveredAgent));
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));
    }

    partial void OnSelectedAgentDeviceChanged(AgentDeviceUiState? value)
    {
        AgentRevokeConfirmed = false;
        AgentRenameDeviceLabel = value?.DeviceLabel ?? string.Empty;
        OnPropertyChanged(nameof(CanRevokeAgentDevice));
        OnPropertyChanged(nameof(CanRenameAgentDevice));
    }

    partial void OnAgentRevokeConfirmedChanged(bool value) =>
        OnPropertyChanged(nameof(CanRevokeAgentDevice));

    partial void OnAgentRenameDeviceLabelChanged(string value) =>
        OnPropertyChanged(nameof(CanRenameAgentDevice));

    partial void OnIsServiceProfileBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartPairing));
        OnPropertyChanged(nameof(CanStartAgentEnrollment));
        OnPropertyChanged(nameof(CanTransferServiceProfile));
        OnPropertyChanged(nameof(CanApplyServiceProfileImport));
        OnPropertyChanged(nameof(CanManageAgent));
        OnPropertyChanged(nameof(CanRevokeAgentDevice));
        OnPropertyChanged(nameof(CanRenameAgentDevice));
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));
        OnPropertyChanged(nameof(CanDiscoverAgents));
        OnPropertyChanged(nameof(CanUseDiscoveredAgent));
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));
    }

    partial void OnHasServiceProfileImportPreviewChanged(bool value) =>
        OnPropertyChanged(nameof(CanApplyServiceProfileImport));

    partial void OnServiceProfileImportConfirmedChanged(bool value) =>
        OnPropertyChanged(nameof(CanApplyServiceProfileImport));

    partial void OnHasAgentRelayPreviewChanged(bool value) =>
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));

    partial void OnAgentRelayPlanCanApplyChanged(bool value) =>
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));

    partial void OnAgentRelayConfirmedChanged(bool value) =>
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));

    partial void OnHasAgentCatalogUpdatePreviewChanged(bool value) =>
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));

    partial void OnAgentCatalogUpdateCanApplyChanged(bool value) =>
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));

    partial void OnAgentCatalogUpdateConfirmedChanged(bool value) =>
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));

    partial void OnAgentCatalogApplyDefaultsToAllDevicesChanged(bool value)
    {
        if (HasAgentCatalogUpdatePreview)
        {
            _actions.InvalidateTransientSetupState();
            ClearAgentRelayPreview();
            ClearAgentCatalogUpdatePreview();
            ClearServiceProfilePreview();
            AgentCatalogUpdateFeedback = "授权范围已改变，请重新生成只读差异预览。";
        }
    }

    partial void OnHasAgentEnrollmentCodeChanged(bool value) =>
        OnPropertyChanged(nameof(CanCopyAgentEnrollmentCode));

    partial void OnAgentEnrollmentCodeChanged(string value) =>
        OnPropertyChanged(nameof(CanCopyAgentEnrollmentCode));

    partial void OnIsAgentDiscoveringChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartPairing));
        OnPropertyChanged(nameof(CanStartAgentEnrollment));
        OnPropertyChanged(nameof(CanManageAgent));
        OnPropertyChanged(nameof(CanTransferServiceProfile));
        OnPropertyChanged(nameof(CanDiscoverAgents));
        OnPropertyChanged(nameof(CanUseDiscoveredAgent));
        OnPropertyChanged(nameof(CanRebindAgentEndpoint));
    }

    partial void OnSelectedAgentDiscoveryCandidateChanged(AgentDiscoveryCandidateUiState? value) =>
        UpdateDiscoveredAgentSelection();

    partial void OnAgentEndpointRebindConfirmedChanged(bool value) =>
        OnPropertyChanged(nameof(CanRebindAgentEndpoint));

    partial void OnAgentRelaySlugChanged(string value) => InvalidateAgentRelayPreview();
    partial void OnAgentRelayServiceNameChanged(string value) => InvalidateAgentRelayPreview();
    partial void OnAgentRelayDescriptionChanged(string value) => InvalidateAgentRelayPreview();
    partial void OnAgentRelayLinuxPortChanged(string value) => InvalidateAgentRelayPreview();
    partial void OnAgentRelayTargetHostChanged(string value) => InvalidateAgentRelayPreview();
    partial void OnAgentRelayTargetPortChanged(string value) => InvalidateAgentRelayPreview();
    partial void OnAgentRelayUseHttpsChanged(bool value) => InvalidateAgentRelayPreview();
    partial void OnAgentRelayOpenPathChanged(string value) => InvalidateAgentRelayPreview();
    partial void OnAgentRelayWindowsPortChanged(string value) => InvalidateAgentRelayPreview();
    partial void OnAgentRelayGrantToAllDevicesChanged(bool value) => InvalidateAgentRelayPreview();

    private void UpdateDiscoveredAgentSelection()
    {
        AgentEndpointRebindConfirmed = false;
        OnPropertyChanged(nameof(CanUseDiscoveredAgent));
        OnPropertyChanged(nameof(CanRebindAgentEndpoint));
    }

    [RelayCommand]
    private void ShowSetup() => SelectedPage = AppPage.Setup;

    [RelayCommand]
    private void ShowOverview() => SelectedPage = AppPage.Overview;

    [RelayCommand]
    private void ShowServices() => SelectedPage = AppPage.Services;

    [RelayCommand]
    private void ShowDiagnostics() => SelectedPage = AppPage.Diagnostics;

    [RelayCommand]
    private void ShowSettings() => SelectedPage = AppPage.Settings;

    [RelayCommand]
    private async Task ToggleBridgeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (IsConnected || IsDegraded || IsConnecting)
            {
                await _actions.DisconnectAsync();
            }
            else
            {
                await _actions.ConnectAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => _actions.RefreshAsync();

    [RelayCommand]
    private Task RetryNowAsync() => _actions.RetryAsync();

    [RelayCommand]
    private Task RunDiagnosticsAsync() => _actions.RunDiagnosticsAsync();

    [RelayCommand]
    private Task ExportDiagnosticBundleAsync() =>
        CanExportDiagnosticBundle ? _actions.ExportDiagnosticBundleAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task CheckForUpdatesAsync() =>
        CanCheckForUpdates ? _actions.CheckForUpdatesAsync() : Task.CompletedTask;

    [RelayCommand]
    private async Task PairDeviceAsync()
    {
        if (!CanStartPairing)
        {
            return;
        }

        await _actions.PairDeviceAsync(new PairingDraft(PairingCode, PairingIdentityFile));
    }

    [RelayCommand]
    private async Task EnrollWithAgentAsync()
    {
        if (!CanStartAgentEnrollment)
        {
            return;
        }

        await _actions.EnrollWithAgentAsync(new AgentEnrollmentDraft(
            AgentEndpoint,
            AgentRecoveryCode,
            AgentDeviceLabel,
            PairingIdentityFile));
    }

    [RelayCommand]
    private Task SyncAgentCatalogAsync() =>
        CanManageAgent ? _actions.SyncAgentCatalogAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task RefreshAgentDevicesAsync() =>
        CanManageAgent ? _actions.RefreshAgentDevicesAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task RevokeAgentDeviceAsync() =>
        CanRevokeAgentDevice && SelectedAgentDevice is not null
            ? _actions.RevokeAgentDeviceAsync(SelectedAgentDevice.DeviceId, AgentRevokeConfirmed)
            : Task.CompletedTask;

    [RelayCommand]
    private Task RenameAgentDeviceAsync() =>
        CanRenameAgentDevice && SelectedAgentDevice is not null
            ? _actions.RenameAgentDeviceAsync(
                SelectedAgentDevice.DeviceId,
                AgentRenameDeviceLabel)
            : Task.CompletedTask;

    [RelayCommand]
    private Task ExportServiceProfileAsync() =>
        CanTransferServiceProfile ? _actions.ExportServiceProfileAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task PreviewServiceProfileImportAsync() =>
        CanTransferServiceProfile ? _actions.PreviewServiceProfileImportAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task ApplyServiceProfileImportAsync() =>
        CanApplyServiceProfileImport
            ? _actions.ApplyServiceProfileImportAsync(ServiceProfileImportConfirmed)
            : Task.CompletedTask;

    [RelayCommand]
    private Task PlanAgentRelayAsync() =>
        CanManageAgent
            ? _actions.PlanAgentRelayAsync(new AgentRelayDraft(
                AgentRelaySlug,
                AgentRelayServiceName,
                AgentRelayDescription,
                AgentRelayLinuxPort,
                AgentRelayTargetHost,
                AgentRelayTargetPort,
                AgentRelayUseHttps,
                AgentRelayOpenPath,
                AgentRelayWindowsPort,
                AgentRelayGrantToAllDevices))
            : Task.CompletedTask;

    [RelayCommand]
    private Task PlanAgentRelayDeletionAsync() =>
        CanManageAgent
            ? _actions.PlanAgentRelayDeletionAsync(AgentRelaySlug)
            : Task.CompletedTask;

    [RelayCommand]
    private Task ApplyAgentRelayPlanAsync() =>
        CanApplyAgentRelayPlan
            ? _actions.ApplyAgentRelayPlanAsync(AgentRelayConfirmed)
            : Task.CompletedTask;

    [RelayCommand]
    private Task GenerateAgentEnrollmentCodeAsync() =>
        CanManageAgent ? _actions.GenerateAgentEnrollmentCodeAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task CopyAgentEnrollmentCodeAsync() =>
        CanCopyAgentEnrollmentCode
            ? _actions.CopyAgentEnrollmentCodeAsync(AgentEnrollmentCode)
            : Task.CompletedTask;

    [RelayCommand]
    private Task DiscoverAgentsAsync() =>
        CanDiscoverAgents ? _actions.DiscoverAgentsAsync() : Task.CompletedTask;

    [RelayCommand]
    private void UseDiscoveredAgent()
    {
        if (!CanUseDiscoveredAgent || SelectedAgentDiscoveryCandidate is null)
        {
            return;
        }

        AgentEndpoint = SelectedAgentDiscoveryCandidate.Endpoint;
        AgentDiscoveryFeedback = "已明确填入所选 Agent 地址；仍需提供一次性添加码并点击登记，发现操作本身不会登记设备。";
    }

    [RelayCommand]
    private Task RebindAgentEndpointAsync() =>
        CanRebindAgentEndpoint && SelectedAgentDiscoveryCandidate is not null
            ? _actions.RebindAgentEndpointAsync(
                SelectedAgentDiscoveryCandidate,
                AgentEndpointRebindConfirmed)
            : Task.CompletedTask;

    [RelayCommand]
    private Task PreviewAgentCatalogUpdateAsync() =>
        CanManageAgent
            ? _actions.PreviewAgentCatalogUpdateAsync(AgentCatalogApplyDefaultsToAllDevices)
            : Task.CompletedTask;

    [RelayCommand]
    private Task ApplyAgentCatalogUpdateAsync() =>
        CanApplyAgentCatalogUpdate
            ? _actions.ApplyAgentCatalogUpdateAsync(AgentCatalogUpdateConfirmed)
            : Task.CompletedTask;

    [RelayCommand]
    private Task CopyDiagnosticSummaryAsync() => _actions.CopyDiagnosticSummaryAsync();

    [RelayCommand]
    private Task OpenLogFolderAsync() => _actions.OpenLogFolderAsync();

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var draft = new SettingsDraft(
            LinuxHost, LinuxUser, SshPort, IdentityFile, PinnedHostKey,
            AutoConnect, AutoReconnect, CloseToTray, StartWithWindows,
            ThemeIndex);

        await _actions.SaveSettingsAsync(draft);
    }

    [RelayCommand]
    private Task AddServiceAsync() => _actions.AddServiceAsync();

    [RelayCommand]
    private Task SaveSelectedServiceAsync() =>
        SelectedService is null ? Task.CompletedTask : SaveServiceDraftAsync(SelectedService);

    [RelayCommand]
    private Task RemoveSelectedServiceAsync() =>
        SelectedService is null ? Task.CompletedTask : _actions.RemoveServiceAsync(SelectedService.Id);

    public void ApplyBridgeState(BridgeUiState state)
    {
        ConnectionStatus = state.Status;
        ConnectionSummary = state.Summary;
        ConnectionActionText = state.ActionText;
        IsConnected = state.IsConnected;
        IsConnecting = state.IsConnecting;
        IsDegraded = state.IsDegraded;
        LinuxPeerName = state.PeerName;
        LinuxPeerDnsName = state.PeerDnsName;
        AvailableServicesText = state.AvailableServicesText;
        LastUpdatedText = state.LastUpdatedText;
        HasIssue = state.HasIssue;
        IssueTitle = state.IssueTitle;
        IssueDetail = state.IssueDetail;
    }

    public void SetServices(IEnumerable<ServiceUiState> services)
    {
        var selectedId = SelectedService?.Id;
        Services.Clear();
        foreach (var state in services)
        {
            Services.Add(new ServiceCardViewModel(
                state,
                service => _actions.OpenServiceAsync(service.Id, service.LocalUrl),
                service => _actions.RetryServiceAsync(service.Id),
                service => _actions.CopyAddressAsync(service.LocalUrl),
                (service, enabled) => SaveServiceDraftAsync(service),
                SaveServiceDraftAsync,
                service => _actions.RemoveServiceAsync(service.Id)));
        }

        SelectedService = Services.FirstOrDefault(service => service.Id == selectedId)
                          ?? Services.FirstOrDefault();
    }

    public void UpdateServices(IEnumerable<ServiceUiState> services)
    {
        var states = services.ToArray();
        foreach (var state in states)
        {
            var existing = Services.FirstOrDefault(service => service.Id == state.Id);
            if (existing is null)
            {
                SetServices(states);
                return;
            }

            existing.ApplyRuntimeState(state);
        }
    }

    public void SetDiagnostics(IEnumerable<DiagnosticUiState> diagnostics)
    {
        Diagnostics.Clear();
        foreach (var state in diagnostics)
        {
            Diagnostics.Add(new DiagnosticItemViewModel(state));
        }
    }

    public void ShowNotice(string title, string detail)
    {
        HasIssue = true;
        IssueTitle = title;
        IssueDetail = detail;
        LastUpdatedText = $"{DateTime.Now:HH:mm:ss} 更新";
    }

    public void ApplySetupState(bool completed, string deviceText, string identityFile)
    {
        SetupCompleted = completed;
        ConfiguredDeviceText = completed ? deviceText : "尚未配置 Linux 设备";
        if (!string.IsNullOrWhiteSpace(identityFile))
        {
            PairingIdentityFile = identityFile;
        }
    }

    public void BeginPairing()
    {
        IsPairing = true;
        PairingPrerequisiteStatus = "检查中";
        PairingKeyStatus = "等待";
        PairingLinuxStatus = "等待";
        PairingFinishStatus = "等待";
        SetupFeedback = "请保持 Linux 配对脚本窗口开启。";
    }

    public void ApplyPairingProgress(DevicePairingStage stage, string detail)
    {
        SetupFeedback = detail;
        switch (stage)
        {
            case DevicePairingStage.CheckingPrerequisites:
                PairingPrerequisiteStatus = "进行中";
                break;
            case DevicePairingStage.PreparingKey:
                PairingPrerequisiteStatus = "完成";
                PairingKeyStatus = "进行中";
                break;
            case DevicePairingStage.AuthorizingLinux:
                PairingPrerequisiteStatus = "完成";
                PairingKeyStatus = "完成";
                PairingLinuxStatus = "进行中";
                break;
            case DevicePairingStage.Completed:
                PairingPrerequisiteStatus = "完成";
                PairingKeyStatus = "完成";
                PairingLinuxStatus = "完成";
                PairingFinishStatus = "进行中";
                break;
        }
    }

    public void CompletePairing(string deviceText)
    {
        IsPairing = false;
        SetupCompleted = true;
        ConfiguredDeviceText = deviceText;
        PairingPrerequisiteStatus = "完成";
        PairingKeyStatus = "完成";
        PairingLinuxStatus = "完成";
        PairingFinishStatus = "完成";
        PairingCode = string.Empty;
        SetupFeedback = "配对与配置已完成，正在建立安全隧道。";
    }

    public void FailPairing(string detail)
    {
        IsPairing = false;
        PairingFinishStatus = "需要处理";
        SetupFeedback = detail;
    }

    public void BeginAgentEnrollment()
    {
        IsAgentEnrolling = true;
        AgentConnectionStatus = "检查中";
        AgentKeyStatus = "等待";
        AgentAuthorizationStatus = "等待";
        AgentCatalogStatus = "等待";
        AgentEnrollmentFeedback = "正在验证 Agent 地址与 Tailscale 连接…";
    }

    public void ApplyAgentEnrollmentProgress(AgentEnrollmentStage stage, string detail)
    {
        AgentEnrollmentFeedback = detail;
        switch (stage)
        {
            case AgentEnrollmentStage.CheckingAgent:
                AgentConnectionStatus = "进行中";
                break;
            case AgentEnrollmentStage.PreparingKey:
                AgentConnectionStatus = "完成";
                AgentKeyStatus = "进行中";
                break;
            case AgentEnrollmentStage.AwaitingAuthorization:
                AgentConnectionStatus = "完成";
                AgentKeyStatus = "完成";
                AgentAuthorizationStatus = "等待确认";
                break;
            case AgentEnrollmentStage.FetchingCatalog:
                AgentConnectionStatus = "完成";
                AgentKeyStatus = "完成";
                AgentAuthorizationStatus = "完成";
                AgentCatalogStatus = "同步中";
                break;
            case AgentEnrollmentStage.Completed:
                AgentConnectionStatus = "完成";
                AgentKeyStatus = "完成";
                AgentAuthorizationStatus = "完成";
                AgentCatalogStatus = "完成";
                break;
        }
    }

    public void CompleteAgentEnrollment(string deviceText)
    {
        IsAgentEnrolling = false;
        SetupCompleted = true;
        ConfiguredDeviceText = deviceText;
        AgentConnectionStatus = "完成";
        AgentKeyStatus = "完成";
        AgentAuthorizationStatus = "完成";
        AgentCatalogStatus = "完成";
        AgentRecoveryCode = string.Empty;
        AgentEnrollmentFeedback = "设备已登记，服务清单已同步，正在建立安全隧道。";
        IsAgentConfigured = true;
    }

    public void BeginAgentDiscovery()
    {
        IsAgentDiscovering = true;
        AgentDiscoveryCandidates.Clear();
        SelectedAgentDiscoveryCandidate = null;
        AgentDiscoveryFeedback = "正在只读检查当前 tailnet 的在线 peer；不会扫描网段或登记设备。";
    }

    public void ApplyAgentDiscovery(
        IEnumerable<AgentDiscoveryCandidateUiState> candidates,
        string feedback)
    {
        AgentDiscoveryCandidates.Clear();
        foreach (var candidate in candidates)
        {
            AgentDiscoveryCandidates.Add(candidate);
        }

        SelectedAgentDiscoveryCandidate = AgentDiscoveryCandidates.Count == 1
            ? AgentDiscoveryCandidates[0]
            : null;
        IsAgentDiscovering = false;
        AgentDiscoveryFeedback = feedback;
    }

    public void FailAgentDiscovery(string detail)
    {
        AgentDiscoveryCandidates.Clear();
        SelectedAgentDiscoveryCandidate = null;
        IsAgentDiscovering = false;
        AgentDiscoveryFeedback = detail;
    }

    public void FailAgentEnrollment(string detail)
    {
        IsAgentEnrolling = false;
        AgentEnrollmentFeedback = detail;
        if (AgentConnectionStatus == "进行中")
        {
            AgentConnectionStatus = "需要处理";
        }
        else if (AgentKeyStatus == "进行中")
        {
            AgentKeyStatus = "需要处理";
        }
        else if (AgentAuthorizationStatus == "等待确认")
        {
            AgentAuthorizationStatus = "未授权";
        }
        else
        {
            AgentCatalogStatus = "需要处理";
        }
    }

    public void ApplyAgentRegistration(bool configured)
    {
        IsAgentConfigured = configured;
        if (!configured)
        {
            AgentDevices.Clear();
            SelectedAgentDevice = null;
            CurrentAgentDeviceIsAdmin = false;
            ClearAgentEnrollmentCode("完成 Agent 登记后，管理员可在这里生成新设备添加码。");
            ClearAgentRelayPreview();
        }
    }

    public void BeginAgentManagement(string detail)
    {
        IsAgentManaging = true;
        AgentManagementFeedback = detail;
    }

    public void ApplyAgentDevices(
        IEnumerable<AgentDeviceUiState> devices,
        string currentDeviceRole,
        string feedback)
    {
        var selectedId = SelectedAgentDevice?.DeviceId;
        AgentDevices.Clear();
        foreach (var device in devices)
        {
            AgentDevices.Add(device);
        }

        SelectedAgentDevice = AgentDevices.FirstOrDefault(device => device.DeviceId == selectedId)
                              ?? AgentDevices.FirstOrDefault(device => !device.IsCurrent && !device.IsRevoked)
                              ?? AgentDevices.FirstOrDefault();
        CurrentAgentDeviceIsAdmin = string.Equals(currentDeviceRole, "admin", StringComparison.Ordinal);
        AgentRevokeConfirmed = false;
        IsAgentManaging = false;
        AgentManagementFeedback = feedback;
    }

    public void FailAgentManagement(string detail)
    {
        IsAgentManaging = false;
        AgentRevokeConfirmed = false;
        AgentManagementFeedback = detail;
    }

    public void BeginAgentRelayOperation(string detail)
    {
        IsAgentManaging = true;
        AgentRelayConfirmed = false;
        AgentRelayFeedback = detail;
    }

    public void ApplyAgentRelayPreview(string summary, bool canApply)
    {
        IsAgentManaging = false;
        AgentRelayPreviewSummary = summary;
        AgentRelayPlanCanApply = canApply;
        HasAgentRelayPreview = true;
        AgentRelayConfirmed = false;
        AgentRelayFeedback = canApply
            ? "只读计划与探测已完成。核对映射和授权影响后，明确勾选确认才会修改 Linux。"
            : "计划或探测表明当前不能安全应用；Linux 尚未改变。";
    }

    public void CompleteAgentRelayOperation(string detail)
    {
        IsAgentManaging = false;
        AgentRelayFeedback = detail;
        ClearAgentRelayPreview();
    }

    public void FailAgentRelayOperation(string detail, bool preservePreview)
    {
        IsAgentManaging = false;
        AgentRelayFeedback = detail;
        AgentRelayConfirmed = false;
        if (!preservePreview)
        {
            ClearAgentRelayPreview();
        }
    }

    public void BeginAgentCatalogUpdate(string detail)
    {
        IsAgentManaging = true;
        AgentCatalogUpdateConfirmed = false;
        AgentCatalogUpdateFeedback = detail;
    }

    public void ApplyAgentCatalogUpdatePreview(
        string summary,
        bool canApply,
        string feedback)
    {
        IsAgentManaging = false;
        AgentCatalogUpdateSummary = summary;
        AgentCatalogUpdateCanApply = canApply;
        HasAgentCatalogUpdatePreview = true;
        AgentCatalogUpdateConfirmed = false;
        AgentCatalogUpdateFeedback = feedback;
    }

    public void CompleteAgentCatalogUpdate(string detail)
    {
        IsAgentManaging = false;
        AgentCatalogUpdateFeedback = detail;
        ClearAgentCatalogUpdatePreview();
    }

    public void FailAgentCatalogUpdate(string detail, bool preservePreview)
    {
        IsAgentManaging = false;
        AgentCatalogUpdateFeedback = detail;
        AgentCatalogUpdateConfirmed = false;
        if (!preservePreview)
        {
            ClearAgentCatalogUpdatePreview();
        }
    }

    private void ClearAgentCatalogUpdatePreview()
    {
        HasAgentCatalogUpdatePreview = false;
        AgentCatalogUpdateCanApply = false;
        AgentCatalogUpdateConfirmed = false;
        AgentCatalogUpdateSummary = "尚未预览本机与 Linux Agent 的服务清单差异。";
    }

    public void ShowAgentEnrollmentCode(string code, string expiryText)
    {
        AgentEnrollmentCode = code;
        AgentEnrollmentCodeExpiry = expiryText;
        HasAgentEnrollmentCode = true;
        IsAgentManaging = false;
        AgentEnrollmentCodeFeedback = "添加码只显示到过期；复制后请立即发送给目标 Windows，剪贴板内容需自行覆盖。";
    }

    public void SetAgentEnrollmentCodeFeedback(string detail)
    {
        IsAgentManaging = false;
        AgentEnrollmentCodeFeedback = detail;
    }

    public void ClearAgentEnrollmentCode(string detail)
    {
        AgentEnrollmentCode = string.Empty;
        AgentEnrollmentCodeExpiry = string.Empty;
        HasAgentEnrollmentCode = false;
        AgentEnrollmentCodeFeedback = detail;
    }

    private void InvalidateAgentRelayPreview()
    {
        if (!HasAgentRelayPreview)
        {
            return;
        }

        _actions.InvalidateTransientSetupState();
        ClearAgentRelayPreview();
        ClearAgentCatalogUpdatePreview();
        ClearServiceProfilePreview();
        AgentRelayFeedback = "输入已改变。请重新生成只读计划和探测结果。";
    }

    private void ClearAgentRelayPreview()
    {
        HasAgentRelayPreview = false;
        AgentRelayPlanCanApply = false;
        AgentRelayConfirmed = false;
        AgentRelayPreviewSummary = "尚未生成只读计划。";
    }

    public void BeginServiceProfileAction(string detail)
    {
        IsServiceProfileBusy = true;
        ServiceProfileFeedback = detail;
    }

    public void ApplyServiceProfilePreview(string summary)
    {
        IsServiceProfileBusy = false;
        HasServiceProfileImportPreview = true;
        ServiceProfileImportConfirmed = false;
        ServiceProfileImportSummary = summary;
        ServiceProfileFeedback = "已完成只读预览。核对差异并明确勾选确认后，才会替换本机服务列表。";
    }

    public void ClearServiceProfilePreview()
    {
        HasServiceProfileImportPreview = false;
        ServiceProfileImportConfirmed = false;
        ServiceProfileImportSummary = "尚未选择导入文件。";
    }

    public void CompleteServiceProfileAction(string detail, bool clearPreview)
    {
        IsServiceProfileBusy = false;
        ServiceProfileFeedback = detail;
        if (clearPreview)
        {
            HasServiceProfileImportPreview = false;
            ServiceProfileImportConfirmed = false;
            ServiceProfileImportSummary = "尚未选择导入文件。";
        }
    }

    public void FailServiceProfileAction(string detail, bool preservePreview)
    {
        IsServiceProfileBusy = false;
        ServiceProfileFeedback = detail;
        if (!preservePreview)
        {
            HasServiceProfileImportPreview = false;
            ServiceProfileImportConfirmed = false;
            ServiceProfileImportSummary = "尚未选择导入文件。";
        }
    }

    private async Task SaveServiceDraftAsync(ServiceCardViewModel service)
    {
        if (!service.TryCreateState(out var state, out var error))
        {
            ServiceFeedback = error;
            return;
        }

        await _actions.SaveServiceAsync(state!);
    }

    private void LoadDesignData()
    {
        ApplyBridgeState(new BridgeUiState(
            "已连接", "全部服务可用", "断开",
            true, false, false,
            "linux-box", "linux-box.example-tailnet.ts.net",
            "3 / 3 可用", "刚刚检查",
            false, "连接运行正常", "所有转发与网页健康检查均已通过。"));

        TailscaleExecutable = @"C:\Program Files\Tailscale\tailscale.exe";
        TailscaleIdentity = "user@example.com";
        TailnetName = "example-tailnet.ts.net";
        PeerRoute = "MagicDNS · 100.64.0.10（仅本次）";
        SshExecutable = @"C:\Windows\System32\OpenSSH\ssh.exe";
        SshProcess = "PID 18424 · 正在运行";

        SetServices(
        [
            new ServiceUiState(
                "overleaf", "OL", "Overleaf", "论文协作与 LaTeX 编辑",
                "http://127.0.0.1:8080/project", 8080, 80,
                "可用", "HTTP 302 · 18 ms", true, true, false, true, HealthCheckKind.HttpStatus),
            new ServiceUiState(
                "gpu-dashboard", "GPU", "GPU Dashboard", "显卡状态与任务监控",
                "http://127.0.0.1:8765/", 8765, 8765,
                "可用", "健康检查 · 11 ms", true, true, false, true, HealthCheckKind.JsonOk),
            new ServiceUiState(
                "investment-news", "NEWS", "Investment News", "A股产业链资讯与 AI 每日要点",
                "http://127.0.0.1:8793/", 8793, 8793,
                "可用", "HTTP 200 · 9 ms", true, true, false, true, HealthCheckKind.HttpStatus),
        ]);

        SetDiagnostics(
        [
            new DiagnosticUiState("12:42:10", "成功", "SSH 隧道已建立", "已验证固定主机密钥，3 条本地转发开始监听。", "connected"),
            new DiagnosticUiState("12:42:11", "信息", "Overleaf 健康检查通过", "GET /project 返回 HTTP 302，服务可访问。", "info"),
            new DiagnosticUiState("12:42:11", "信息", "GPU Dashboard 健康检查通过", "GET /api/health 返回 ok=true。", "info"),
            new DiagnosticUiState("12:42:11", "信息", "Investment News 健康检查通过", "GET / 返回 HTTP 200，服务可访问。", "info"),
        ]);
    }
}
