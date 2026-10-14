using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JKBridge.App.Services;
using JKBridge.Core.Models;
using JKBridge.Core.Pairing;

namespace JKBridge.App.ViewModels;

public enum AppPage
{
    Setup,
    Overview,
    Services,
    Diagnostics,
    Settings,
}

public enum ConnectionDoctorActionKind
{
    Refresh,
    Retry,
    Setup,
    Services,
}

public sealed record WelcomeTutorialStep(
    string Eyebrow,
    string Title,
    string Description,
    string FirstTitle,
    string FirstDetail,
    string SecondTitle,
    string SecondDetail,
    string ThirdTitle,
    string ThirdDetail,
    string Note);

/// <summary>
/// Window-level presentation state. Runtime code supplies actions through
/// <see cref="IBridgeUiActions"/> and pushes sanitized state through the Apply/Set methods.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<WelcomeTutorialStep> WelcomeTutorialSteps =
    [
        new(
            "认识 JK Bridge",
            "把只在本机开放的网页，变成清楚可控的入口",
            "JK Bridge 同时管理两类服务：Linux 主机上的回环网页，以及当前 Windows 上只监听 127.0.0.1 / ::1 的网页。",
            "Linux 服务",
            "通过 Tailscale + SSH 转发到 Windows，浏览器仍然只访问本机地址。",
            "Windows 本机服务",
            "读取系统当前的回环监听并做轻量网页识别，保存后作为固定入口使用。",
            "默认不做的事",
            "不扫描磁盘、不遍历全部 1–65535 端口，也不会把服务暴露到局域网。",
            "左侧导航已经按“日常使用”“配置与维护”“帮助与偏好”分类。"),
        new(
            "首次连接",
            "第一次只需完成一次配对",
            "若你只是连接一台 Linux 主机，使用一次性配对即可；Linux Agent 是多设备和集中管理场景的可选高级功能。",
            "1 · Linux 生成配对码",
            "在目标 Linux 上运行配对脚本，得到十分钟有效、只能使用一次的 jkbridge:// 配对码。",
            "2 · Windows 完成设备配置",
            "打开“设备配置”，粘贴配对码并点击“一键配对并连接”。",
            "3 · 回到概览",
            "看到“已连接”后，直接打开常用服务；以后通常只需启动 JK Bridge。",
            "配对码和 Agent 登记码都属于一次性凭据，不要截图、转发或写进服务网址。"),
        new(
            "服务入口",
            "“服务”页分成 Linux 隧道与 Windows 本机",
            "先在页面右上角选择服务来源，再进行添加、扫描、编辑或打开，避免把两类端口混在一起。",
            "Linux 隧道",
            "设置 Windows 访问端口、Linux 回环端口和打开路径；新增端口通常需要重新配对授权。",
            "Windows 本机",
            "点击“扫描本机”查看当前正在监听的回环服务；确认后保存，之后无需重复扫描。",
            "临时结果与固定入口",
            "扫描结果只是候选；只有点击保存后，才会记录为长期使用的本机服务。",
            "扫描只读取操作系统网络监听表，并对候选地址发送不下载正文的轻量请求。"),
        new(
            "日常使用与维护",
            "平时看概览，打不开时再看诊断",
            "大多数操作都可以按“概览 → 服务 → 诊断”的顺序完成；设置页负责长期偏好和软件更新。",
            "概览",
            "连接或断开全部 Linux 隧道，并查看每个服务是否可打开。",
            "诊断",
            "按 Tailscale、Linux 节点、SSH、网页健康检查逐段定位问题，并可导出脱敏诊断包。",
            "设置",
            "保存连接行为、外观和开机启动；版本更新分为检查、下载校验、确认安装三个可见阶段。",
            "以后需要复习时，随时点击左下角“使用指南”重新打开本教程。"),
    ];

    private readonly IBridgeUiActions _actions;

    [ObservableProperty]
    private AppPage _selectedPage = AppPage.Overview;

    [ObservableProperty]
    private bool _isWelcomeTutorialVisible;

    [ObservableProperty]
    private int _welcomeTutorialStepIndex;

    [ObservableProperty]
    private bool _isWelcomeTutorialCompleting;

    [ObservableProperty]
    private string _welcomeTutorialFeedback = "可以随时从左下角重新打开本教程。";

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
    [ObservableProperty] private string _connectionDoctorStatus = "等待检查";
    [ObservableProperty] private string _connectionDoctorProgressText = "尚未完成分层检查";
    [ObservableProperty] private string _connectionDoctorSummary = "点击“重新检查”，连接医生会按 Tailscale、Linux、SSH、网页四层定位。";
    [ObservableProperty] private string _connectionDoctorRootCause = "尚无结论";
    [ObservableProperty] private string _connectionDoctorNextStep = "先运行一次检查。";
    [ObservableProperty] private string _connectionDoctorActionLabel = "开始检查";
    [ObservableProperty] private ConnectionDoctorActionKind _connectionDoctorActionKind = ConnectionDoctorActionKind.Refresh;

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
    [ObservableProperty]
    private string _themePreviewText =
        "当前：跟随系统（已保存）。选择其他模式会立即预览。";
    [ObservableProperty] private string _settingsFeedback = "设置保存在当前 Windows 用户的应用数据目录。";
    [ObservableProperty] private bool _isUpdateChecking;
    [ObservableProperty] private bool _hasUpdateReleaseInfo;
    [ObservableProperty] private bool _hasUpdateInstaller;
    [ObservableProperty] private bool _isUpdateReadyToInstall;
    [ObservableProperty] private bool _isUpdateProgressVisible;
    [ObservableProperty] private bool _isUpdateProgressIndeterminate;
    [ObservableProperty] private double _updateProgressPercent;
    [ObservableProperty] private string _updateProgressText = "尚未开始下载";
    [ObservableProperty] private string _updateStageText = "等待检查";
    [ObservableProperty] private string _applicationVersionText = "JK Bridge";
    [ObservableProperty] private string _applicationFooterText = "JK Bridge · Windows 11";
    [ObservableProperty] private string _updateCheckFeedback = "仅在你点击后查询 JK Bridge 官方 GitHub Releases；确认后可下载、校验并启动覆盖安装。";
    [ObservableProperty] private string _updateReleaseUrl = string.Empty;
    [ObservableProperty] private int _updateNetworkModeIndex;
    [ObservableProperty] private string _updateManualProxyUrl = string.Empty;
    [ObservableProperty]
    private string _linuxQuickPairFeedback =
        "复制到 Linux 终端运行；只需 Python 3，无需 Linux 外网、下载项目或切换目录。";
    [ObservableProperty] private bool _isLinuxServicesMode = true;
    [ObservableProperty] private bool _isWindowsLocalServiceScanning;
    [ObservableProperty] private string _serviceFeedback = "远端主机固定为 127.0.0.1，不会暴露给局域网。";
    [ObservableProperty] private bool _isLinuxPendingServiceScanning;
    [ObservableProperty]
    private string _linuxPendingServiceFeedback =
        "Agent 管理员可读取 Linux 当前 127.0.0.1 监听；不会扫描磁盘或端口范围。";
    [ObservableProperty] private LinuxPendingServiceUiState? _selectedLinuxPendingService;
    [ObservableProperty] private string _linuxQuickAddUrl = string.Empty;
    [ObservableProperty]
    private string _linuxQuickAddFeedback =
        "也可直接粘贴 Linux 上的 http(s)://127.0.0.1:端口/路径。";
    [ObservableProperty]
    private string _windowsLocalScanFeedback =
        "点击“扫描本机”读取当前回环监听；不会扫描磁盘或遍历全部端口。";
    [ObservableProperty] private string _windowsLocalSummaryText = "尚未扫描";

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
    [ObservableProperty] private string _agentSetupCode = string.Empty;
    [ObservableProperty] private string _agentEndpoint = string.Empty;
    [ObservableProperty] private string _agentRecoveryCode = string.Empty;
    [ObservableProperty] private string _agentDeviceLabel = Environment.MachineName;
    [ObservableProperty]
    private string _linuxAgentInstallFeedback =
        "复制后在 Linux 终端运行；无需下载项目、进入目录或使用 sudo。";
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
    public ObservableCollection<WindowsLocalServiceCardViewModel> WindowsLocalServices { get; } = [];
    public ObservableCollection<DiscoveredWindowsLocalServiceViewModel>
        DiscoveredWindowsLocalServices
    { get; } = [];

    public ObservableCollection<DiagnosticItemViewModel> Diagnostics { get; } = [];

    public ObservableCollection<AgentDeviceUiState> AgentDevices { get; } = [];
    public ObservableCollection<AgentDiscoveryCandidateUiState> AgentDiscoveryCandidates { get; } = [];
    public ObservableCollection<LinuxPendingServiceUiState> LinuxPendingServices { get; } = [];

    public bool IsOverview => SelectedPage == AppPage.Overview;
    public bool IsSetup => SelectedPage == AppPage.Setup;
    public bool IsServices => SelectedPage == AppPage.Services;
    public bool IsDiagnostics => SelectedPage == AppPage.Diagnostics;
    public bool IsSettings => SelectedPage == AppPage.Settings;
    public WelcomeTutorialStep WelcomeTutorialStep =>
        WelcomeTutorialSteps[WelcomeTutorialStepIndex];
    public string WelcomeTutorialProgressText =>
        $"步骤 {WelcomeTutorialStepIndex + 1} / {WelcomeTutorialSteps.Count}";
    public bool IsFirstWelcomeTutorialStep => WelcomeTutorialStepIndex == 0;
    public bool IsLastWelcomeTutorialStep => WelcomeTutorialStepIndex == WelcomeTutorialSteps.Count - 1;
    public string WelcomeTutorialNextButtonText => IsLastWelcomeTutorialStep ? "完成教程" : "下一步";
    public bool CanAdvanceWelcomeTutorial => !IsWelcomeTutorialCompleting;
    public bool IsStopped => !IsConnected && !IsConnecting && !IsDegraded;
    public bool HasNoIssue => !HasIssue;
    public bool IsWindowsLocalServicesMode => !IsLinuxServicesMode;
    public bool CanScanWindowsLocalServices => !IsWindowsLocalServiceScanning;
    public bool CanScanLinuxPendingServices =>
        IsAgentConfigured && CurrentAgentDeviceIsAdmin &&
        !IsLinuxPendingServiceScanning && !IsAgentManaging;
    public bool CanAddSelectedLinuxPendingService =>
        CanScanLinuxPendingServices && SelectedLinuxPendingService is not null;
    public bool CanAddLinuxUrlDraft =>
        !string.IsNullOrWhiteSpace(LinuxQuickAddUrl) && !IsAgentManaging;
    public bool HasLinuxPendingServices => LinuxPendingServices.Count > 0;
    public bool HasDiscoveredWindowsLocalServices => DiscoveredWindowsLocalServices.Count > 0;
    public bool CanExportDiagnosticBundle => !IsDiagnosticBundleBusy;
    public bool CanCheckForUpdates => !IsUpdateChecking;
    public bool CanInstallUpdate =>
        HasUpdateInstaller && !IsUpdateChecking && !IsUpdateReadyToInstall;
    public bool CanCancelReleaseUpdate => IsUpdateChecking && !IsUpdateReadyToInstall;
    public bool CanLaunchDownloadedReleaseInstaller => IsUpdateReadyToInstall && !IsUpdateChecking;
    public bool CanStartPairing =>
        !IsPairing && !IsAgentEnrolling && !IsAgentDiscovering && !IsAgentManaging && !IsServiceProfileBusy;
    public bool IsManualUpdateProxy => UpdateNetworkModeIndex == 1;
    public string UpdateNetworkModeDescription =>
        UpdateNetworkModeIndex switch
        {
            1 => "使用本机 HTTP 代理；仅接受 127.0.0.1、localhost 或 ::1 与明确端口。",
            2 => "绕过系统代理直接连接 GitHub；受限网络中可能很慢或失败。",
            _ => "跟随 Windows/.NET 系统代理；系统没有代理时会直接连接。",
        };
    public string LinuxQuickPairCommand => LinuxPairingBootstrap.PairCommand;
    public string LinuxAgentInstallCommand => LinuxAgentBootstrap.InstallCommand;
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

    partial void OnWelcomeTutorialStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(WelcomeTutorialStep));
        OnPropertyChanged(nameof(WelcomeTutorialProgressText));
        OnPropertyChanged(nameof(IsFirstWelcomeTutorialStep));
        OnPropertyChanged(nameof(IsLastWelcomeTutorialStep));
        OnPropertyChanged(nameof(WelcomeTutorialNextButtonText));
    }

    partial void OnIsWelcomeTutorialCompletingChanged(bool value) =>
        OnPropertyChanged(nameof(CanAdvanceWelcomeTutorial));

    public void ShowWelcomeTutorialIfNeeded(bool completed)
    {
        if (!completed)
        {
            ShowWelcomeTutorial();
        }
    }

    public void MarkWelcomeTutorialCompleted()
    {
        WelcomeTutorialFeedback = "教程已完成。以后仍可从左下角重新打开。";
        IsWelcomeTutorialVisible = false;
    }

    partial void OnThemeIndexChanged(int value)
    {
        var theme = ThemeFromIndex(value);
        ThemePreviewText =
            $"正在预览：{ThemeDisplayName(theme)}（尚未保存）。点击页面顶部“保存设置”后长期使用。";
        _actions.PreviewTheme(theme);
    }

    public void MarkThemeSaved(ThemePreference theme) =>
        ThemePreviewText =
            $"当前：{ThemeDisplayName(theme)}（已保存）。选择其他模式会立即预览。";

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnIsDegradedChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnIsLinuxServicesModeChanged(bool value) =>
        OnPropertyChanged(nameof(IsWindowsLocalServicesMode));
    partial void OnIsWindowsLocalServiceScanningChanged(bool value) =>
        OnPropertyChanged(nameof(CanScanWindowsLocalServices));
    partial void OnHasIssueChanged(bool value) => OnPropertyChanged(nameof(HasNoIssue));
    partial void OnIsDiagnosticBundleBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanExportDiagnosticBundle));
    partial void OnIsUpdateCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(CanCancelReleaseUpdate));
        OnPropertyChanged(nameof(CanLaunchDownloadedReleaseInstaller));
    }
    partial void OnHasUpdateInstallerChanged(bool value) =>
        OnPropertyChanged(nameof(CanInstallUpdate));
    partial void OnIsUpdateReadyToInstallChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(CanCancelReleaseUpdate));
        OnPropertyChanged(nameof(CanLaunchDownloadedReleaseInstaller));
    }
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

    partial void OnUpdateNetworkModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsManualUpdateProxy));
        OnPropertyChanged(nameof(UpdateNetworkModeDescription));
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
        OnPropertyChanged(nameof(CanScanLinuxPendingServices));
        OnPropertyChanged(nameof(CanAddSelectedLinuxPendingService));
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
        OnPropertyChanged(nameof(CanScanLinuxPendingServices));
        OnPropertyChanged(nameof(CanAddSelectedLinuxPendingService));
        OnPropertyChanged(nameof(CanAddLinuxUrlDraft));
    }

    partial void OnCurrentAgentDeviceIsAdminChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRevokeAgentDevice));
        OnPropertyChanged(nameof(CanRenameAgentDevice));
        OnPropertyChanged(nameof(CanApplyAgentRelayPlan));
        OnPropertyChanged(nameof(CanDiscoverAgents));
        OnPropertyChanged(nameof(CanUseDiscoveredAgent));
        OnPropertyChanged(nameof(CanApplyAgentCatalogUpdate));
        OnPropertyChanged(nameof(CanScanLinuxPendingServices));
        OnPropertyChanged(nameof(CanAddSelectedLinuxPendingService));
    }

    partial void OnIsLinuxPendingServiceScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanScanLinuxPendingServices));
        OnPropertyChanged(nameof(CanAddSelectedLinuxPendingService));
    }

    partial void OnSelectedLinuxPendingServiceChanged(LinuxPendingServiceUiState? value) =>
        OnPropertyChanged(nameof(CanAddSelectedLinuxPendingService));

    partial void OnLinuxQuickAddUrlChanged(string value) =>
        OnPropertyChanged(nameof(CanAddLinuxUrlDraft));

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
    private void ShowWelcomeTutorial()
    {
        WelcomeTutorialStepIndex = 0;
        WelcomeTutorialFeedback = "可以随时从左下角重新打开本教程。";
        IsWelcomeTutorialVisible = true;
    }

    [RelayCommand]
    private void DismissWelcomeTutorial()
    {
        WelcomeTutorialFeedback = "本次已关闭；下次启动仍会提醒，直到你完成教程。";
        IsWelcomeTutorialVisible = false;
    }

    [RelayCommand]
    private void PreviousWelcomeTutorial()
    {
        if (WelcomeTutorialStepIndex > 0 && !IsWelcomeTutorialCompleting)
        {
            WelcomeTutorialStepIndex--;
        }
    }

    [RelayCommand]
    private async Task AdvanceWelcomeTutorialAsync()
    {
        if (IsWelcomeTutorialCompleting)
        {
            return;
        }

        if (!IsLastWelcomeTutorialStep)
        {
            WelcomeTutorialStepIndex++;
            return;
        }

        IsWelcomeTutorialCompleting = true;
        WelcomeTutorialFeedback = "正在保存教程完成状态…";
        try
        {
            await _actions.CompleteWelcomeTutorialAsync();
        }
        finally
        {
            IsWelcomeTutorialCompleting = false;
        }
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
    private Task RunConnectionDoctorActionAsync() =>
        ConnectionDoctorActionKind switch
        {
            ConnectionDoctorActionKind.Setup => RunAndNavigateAsync(AppPage.Setup),
            ConnectionDoctorActionKind.Services => RunAndNavigateAsync(AppPage.Services),
            ConnectionDoctorActionKind.Retry => _actions.RetryAsync(),
            _ => _actions.RunDiagnosticsAsync(),
        };

    [RelayCommand]
    private Task ExportDiagnosticBundleAsync() =>
        CanExportDiagnosticBundle ? _actions.ExportDiagnosticBundleAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task CheckForUpdatesAsync() =>
        CanCheckForUpdates ? _actions.CheckForUpdatesAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task InstallCheckedReleaseAsync() =>
        CanInstallUpdate ? _actions.InstallCheckedReleaseAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task OpenReleasePageAsync() => _actions.OpenReleasePageAsync();

    [RelayCommand]
    private void CancelReleaseUpdate() => _actions.CancelReleaseUpdate();

    [RelayCommand]
    private Task LaunchDownloadedReleaseInstallerAsync() =>
        CanLaunchDownloadedReleaseInstaller
            ? _actions.LaunchDownloadedReleaseInstallerAsync()
            : Task.CompletedTask;

    [RelayCommand]
    private Task CopyLinuxQuickPairCommandAsync() => _actions.CopyLinuxQuickPairCommandAsync();

    [RelayCommand]
    private Task CopyLinuxAgentInstallCommandAsync() =>
        _actions.CopyLinuxAgentInstallCommandAsync();

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
            PairingIdentityFile,
            AgentSetupCode));
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
            ThemeIndex, UpdateNetworkModeIndex,
            UpdateManualProxyUrl);

        await _actions.SaveSettingsAsync(draft);
    }

    private static ThemePreference ThemeFromIndex(int index) =>
        index switch
        {
            1 => ThemePreference.Light,
            2 => ThemePreference.Dark,
            _ => ThemePreference.System,
        };

    private static string ThemeDisplayName(ThemePreference theme) =>
        theme switch
        {
            ThemePreference.Light => "浅色",
            ThemePreference.Dark => "深色",
            _ => "跟随系统",
        };

    [RelayCommand]
    private Task AddServiceAsync() => _actions.AddServiceAsync();

    [RelayCommand]
    private void ShowLinuxServices()
    {
        IsLinuxServicesMode = true;
        SelectedPage = AppPage.Services;
    }

    [RelayCommand]
    private void ShowWindowsLocalServices()
    {
        IsLinuxServicesMode = false;
        SelectedPage = AppPage.Services;
    }

    [RelayCommand]
    private async Task ScanWindowsLocalServicesAsync()
    {
        if (IsWindowsLocalServiceScanning)
        {
            return;
        }

        IsWindowsLocalServiceScanning = true;
        WindowsLocalScanFeedback = "正在读取回环监听并进行无正文网页识别…";
        try
        {
            await _actions.ScanWindowsLocalServicesAsync();
        }
        finally
        {
            IsWindowsLocalServiceScanning = false;
        }
    }

    [RelayCommand]
    private void CancelWindowsLocalServiceScan() =>
        _actions.CancelWindowsLocalServiceScan();

    [RelayCommand]
    private Task AddWindowsLocalServiceAsync() =>
        _actions.AddWindowsLocalServiceAsync();

    [RelayCommand]
    private async Task ScanLinuxPendingServicesAsync()
    {
        if (!CanScanLinuxPendingServices)
        {
            return;
        }

        IsLinuxPendingServiceScanning = true;
        LinuxPendingServiceFeedback = "正在让 Linux Agent 读取当前回环监听并做有限网页识别…";
        try
        {
            await _actions.ScanLinuxPendingServicesAsync();
        }
        finally
        {
            IsLinuxPendingServiceScanning = false;
        }
    }

    [RelayCommand]
    private Task AddSelectedLinuxPendingServiceAsync() =>
        CanAddSelectedLinuxPendingService && SelectedLinuxPendingService is not null
            ? _actions.AddLinuxPendingServiceAsync(SelectedLinuxPendingService)
            : Task.CompletedTask;

    [RelayCommand]
    private async Task AddLinuxUrlDraftAsync()
    {
        if (!CanAddLinuxUrlDraft)
        {
            return;
        }

        await _actions.AddLinuxUrlDraftAsync(LinuxQuickAddUrl);
    }

    [RelayCommand]
    private Task SaveSelectedServiceAsync() =>
        SelectedService is null ? Task.CompletedTask : SaveServiceDraftAsync(SelectedService);

    [RelayCommand]
    private Task RemoveSelectedServiceAsync() =>
        SelectedService is null ? Task.CompletedTask : _actions.RemoveServiceAsync(SelectedService.Id);

    public void ApplyLinuxPendingServices(
        IEnumerable<LinuxPendingServiceUiState> services,
        string feedback)
    {
        LinuxPendingServices.Clear();
        foreach (var service in services)
        {
            LinuxPendingServices.Add(service);
        }
        SelectedLinuxPendingService = LinuxPendingServices.FirstOrDefault();
        IsAgentManaging = false;
        LinuxPendingServiceFeedback = feedback;
        OnPropertyChanged(nameof(HasLinuxPendingServices));
        OnPropertyChanged(nameof(CanAddSelectedLinuxPendingService));
    }

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
                RestartWithConfirmationAsync,
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

    private async Task RestartWithConfirmationAsync(ServiceCardViewModel card)
    {
        if (card.IsRestarting)
        {
            return;
        }

        var confirmed = await _actions.ConfirmAsync(new ConfirmRequest(
            $"重启 {card.Name}？",
            $"{card.Name} 会短暂中断，正在使用的页面需要刷新。重启请求经现有 SSH 隧道发送到 Linux。",
            "重启"));
        if (!confirmed)
        {
            return;
        }

        card.IsRestarting = true;
        try
        {
            await _actions.RestartServiceAsync(card.Id);
        }
        finally
        {
            card.IsRestarting = false;
        }
    }

    public void SetWindowsLocalServices(IEnumerable<WindowsLocalServiceUiState> services)
    {
        WindowsLocalServices.Clear();
        foreach (var state in services)
        {
            WindowsLocalServices.Add(new WindowsLocalServiceCardViewModel(
                state,
                service => _actions.OpenWindowsLocalServiceAsync(service.Id),
                service => _actions.RetryWindowsLocalServiceAsync(service.Id),
                service => _actions.CopyAddressAsync(service.LocalUrlPreview),
                SaveWindowsLocalServiceDraftAsync,
                service => _actions.RemoveWindowsLocalServiceAsync(service.Id)));
        }
    }

    public void UpdateWindowsLocalServices(IEnumerable<WindowsLocalServiceUiState> services)
    {
        var states = services.ToArray();
        foreach (var state in states)
        {
            var existing = WindowsLocalServices.FirstOrDefault(service => service.Id == state.Id);
            if (existing is null)
            {
                SetWindowsLocalServices(states);
                return;
            }

            existing.ApplyRuntimeState(state);
        }
    }

    public void SetDiscoveredWindowsLocalServices(
        IEnumerable<DiscoveredWindowsLocalServiceUiState> services)
    {
        DiscoveredWindowsLocalServices.Clear();
        foreach (var state in services)
        {
            DiscoveredWindowsLocalServices.Add(new DiscoveredWindowsLocalServiceViewModel(
                state,
                service => _actions.OpenDiscoveredWindowsLocalServiceAsync(service.State),
                service => _actions.CopyAddressAsync(service.OpenUrl),
                service => _actions.SaveDiscoveredWindowsLocalServiceAsync(service.State)));
        }

        OnPropertyChanged(nameof(HasDiscoveredWindowsLocalServices));
    }

    public void ApplyConnectionDoctor(
        string status,
        string progress,
        string summary,
        string rootCause,
        string nextStep,
        string actionLabel,
        ConnectionDoctorActionKind actionKind)
    {
        ConnectionDoctorStatus = status;
        ConnectionDoctorProgressText = progress;
        ConnectionDoctorSummary = summary;
        ConnectionDoctorRootCause = rootCause;
        ConnectionDoctorNextStep = nextStep;
        ConnectionDoctorActionLabel = actionLabel;
        ConnectionDoctorActionKind = actionKind;
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

    private Task RunAndNavigateAsync(AppPage page)
    {
        SelectedPage = page;
        return Task.CompletedTask;
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

    private async Task SaveWindowsLocalServiceDraftAsync(WindowsLocalServiceCardViewModel service)
    {
        if (!service.TryCreateState(out var state, out var error))
        {
            WindowsLocalScanFeedback = error;
            return;
        }

        await _actions.SaveWindowsLocalServiceAsync(state!);
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
