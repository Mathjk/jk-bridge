using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JKBridge.App.Services;

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
                "等待隧道", "尚未检查", true, false, false),
            new ServiceUiState(
                "gpu-dashboard", "GPU", "GPU Dashboard", "显卡状态与任务监控",
                "http://127.0.0.1:8765/", 8765, 8765,
                "等待隧道", "尚未检查", true, false, false),
        ]);
    }

    public ObservableCollection<ServiceCardViewModel> Services { get; } = [];

    public ObservableCollection<DiagnosticItemViewModel> Diagnostics { get; } = [];

    public bool IsOverview => SelectedPage == AppPage.Overview;
    public bool IsSetup => SelectedPage == AppPage.Setup;
    public bool IsServices => SelectedPage == AppPage.Services;
    public bool IsDiagnostics => SelectedPage == AppPage.Diagnostics;
    public bool IsSettings => SelectedPage == AppPage.Settings;
    public bool IsStopped => !IsConnected && !IsConnecting && !IsDegraded;
    public bool HasNoIssue => !HasIssue;
    public bool CanStartPairing => !IsPairing;

    partial void OnSelectedPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsSetup));
        OnPropertyChanged(nameof(IsServices));
        OnPropertyChanged(nameof(IsDiagnostics));
        OnPropertyChanged(nameof(IsSettings));
    }

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnIsDegradedChanged(bool value) => OnPropertyChanged(nameof(IsStopped));
    partial void OnHasIssueChanged(bool value) => OnPropertyChanged(nameof(HasNoIssue));
    partial void OnIsPairingChanged(bool value) => OnPropertyChanged(nameof(CanStartPairing));

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
    private async Task PairDeviceAsync()
    {
        if (IsPairing)
        {
            return;
        }

        await _actions.PairDeviceAsync(new PairingDraft(PairingCode, PairingIdentityFile));
    }

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
            "2 / 2 可用", "刚刚检查",
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
                "可用", "HTTP 302 · 18 ms", true, true, false),
            new ServiceUiState(
                "gpu-dashboard", "GPU", "GPU Dashboard", "显卡状态与任务监控",
                "http://127.0.0.1:8765/", 8765, 8765,
                "可用", "健康检查 · 11 ms", true, true, false),
        ]);

        SetDiagnostics(
        [
            new DiagnosticUiState("12:42:10", "成功", "SSH 隧道已建立", "已验证固定主机密钥，2 条本地转发开始监听。", "connected"),
            new DiagnosticUiState("12:42:11", "信息", "Overleaf 健康检查通过", "GET /project 返回 HTTP 302，服务可访问。", "info"),
            new DiagnosticUiState("12:42:11", "信息", "GPU Dashboard 健康检查通过", "GET /api/health 返回 ok=true。", "info"),
        ]);
    }
}
