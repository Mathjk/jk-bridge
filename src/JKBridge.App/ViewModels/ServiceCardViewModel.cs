using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JKBridge.App.Platform;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.App.ViewModels;

public partial class ServiceCardViewModel : ViewModelBase
{
    private readonly Func<ServiceCardViewModel, Task> _open;
    private readonly Func<ServiceCardViewModel, Task> _retry;
    private readonly Func<ServiceCardViewModel, Task> _restart;
    private readonly Func<ServiceCardViewModel, Task> _openLog;
    private readonly Func<ServiceCardViewModel, Task> _copy;
    private readonly Func<ServiceCardViewModel, bool, Task> _setEnabled;
    private readonly Func<ServiceCardViewModel, Task> _save;
    private readonly Func<ServiceCardViewModel, Task> _remove;
    private readonly ILocalPortAvailabilityProbe _localPortProbe;
    private readonly int _savedLocalPort;
    private readonly int _savedRemotePort;
    private LocalPortAvailabilityResult _localPortAvailability = new(LocalPortAvailabilityStatus.Unknown);
    private bool _isSynchronizingSimpleUrl;

    public string Id { get; }

    [ObservableProperty] private string _shortCode;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _localUrl;
    [ObservableProperty] private string _localPortText;
    [ObservableProperty] private string _remotePortText;
    [ObservableProperty] private string _status;
    [ObservableProperty] private string _healthDetail;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private bool _canOpen;
    [ObservableProperty] private int _healthModeIndex;
    [ObservableProperty] private int _protocolIndex;
    [ObservableProperty] private string _launchPath;
    [ObservableProperty] private bool _isAdvanced;
    [ObservableProperty] private bool _useCustomUrl;
    [ObservableProperty] private string _healthPathText;
    [ObservableProperty] private bool _useCustomHealthPath;
    [ObservableProperty] private string _restartPathText;
    [ObservableProperty] private string _restartTokenText;
    [ObservableProperty] private string _restartLocalPortText;
    [ObservableProperty] private bool _useRestartAction;
    [ObservableProperty] private bool _isRestarting;
    [ObservableProperty] private string _lastRestartText;
    [ObservableProperty] private ServiceTemplateOptionViewModel? _selectedTemplate;
    [ObservableProperty] private bool _templateApplyConfirmed;
    [ObservableProperty] private string _templateApplyFeedback = "选择模板只会显示预览；确认并点击应用后才会改动当前草稿。";

    public bool IsUnavailable => !IsAvailable && !HasWarning;
    public bool UsesGeneratedUrl => !UseCustomUrl;
    public bool UsesDerivedHealthPath => !UseCustomHealthPath;
    public IReadOnlyList<ServiceTemplateOptionViewModel> ServiceTemplates { get; } =
        ServiceTemplateOptionViewModel.CreateCatalog();

    public ServiceCardViewModel(
        ServiceUiState state,
        Func<ServiceCardViewModel, Task> open,
        Func<ServiceCardViewModel, Task> retry,
        Func<ServiceCardViewModel, Task> restart,
        Func<ServiceCardViewModel, Task> openLog,
        Func<ServiceCardViewModel, Task> copy,
        Func<ServiceCardViewModel, bool, Task> setEnabled,
        Func<ServiceCardViewModel, Task> save,
        Func<ServiceCardViewModel, Task> remove,
        ILocalPortAvailabilityProbe? localPortProbe = null)
    {
        Id = state.Id;
        _shortCode = state.ShortCode;
        _name = state.Name;
        _description = state.Description;
        _localUrl = state.LocalUrl;
        _localPortText = state.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _remotePortText = state.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _status = state.Status;
        _healthDetail = state.HealthDetail;
        _isEnabled = state.IsEnabled;
        _isAvailable = state.IsAvailable;
        _hasWarning = state.HasWarning;
        _canOpen = state.CanOpen;
        _healthModeIndex = ToHealthModeIndex(state.HealthKind);
        _savedLocalPort = state.LocalPort;
        _savedRemotePort = state.RemotePort;
        _localPortProbe = localPortProbe ?? SystemLocalPortAvailabilityProbe.Instance;

        if (ServiceOpenUrl.TryParse(state.LocalUrl, state.LocalPort, out var urlParts))
        {
            _protocolIndex = string.Equals(urlParts!.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
            _launchPath = urlParts.LaunchPath;
        }
        else
        {
            _protocolIndex = 0;
            _launchPath = "/";
            _useCustomUrl = true;
            _isAdvanced = true;
        }

        var derivedHealthPath = ServiceRouteDefinition.DeriveHealthPath(state.LocalUrl, "/");
        _healthPathText = state.HealthPath;
        _useCustomHealthPath = !string.Equals(
            state.HealthPath,
            derivedHealthPath,
            StringComparison.Ordinal);

        _restartPathText = state.RestartPath;
        _restartTokenText = state.RestartToken;
        _restartLocalPortText = state.RestartLocalPort == 0
            ? string.Empty
            : state.RestartLocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _useRestartAction = !string.IsNullOrWhiteSpace(state.RestartPath);
        _lastRestartText = state.LastRestartText;

        _open = open;
        _retry = retry;
        _restart = restart;
        _openLog = openLog;
        _copy = copy;
        _setEnabled = setEnabled;
        _save = save;
        _remove = remove;
        RefreshLocalPortAvailability();
    }

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(IsUnavailable));
    partial void OnHasWarningChanged(bool value) => OnPropertyChanged(nameof(IsUnavailable));

    partial void OnCanOpenChanged(bool value) => RefreshLocalPortAvailability();

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(RequiresLinuxReauthorization));
        OnPropertyChanged(nameof(ReauthorizationHint));
    }

    partial void OnLocalPortTextChanged(string value)
    {
        OnPropertyChanged(nameof(PortMapping));
        OnPropertyChanged(nameof(RoutePreview));
        OnPropertyChanged(nameof(IsHttps));
        RefreshLocalPortAvailability();
        if (!UseCustomUrl)
        {
            UpdateGeneratedUrl();
        }
        else
        {
            NotifyBrowserUrlStateChanged();
        }
    }

    partial void OnRemotePortTextChanged(string value)
    {
        OnPropertyChanged(nameof(PortMapping));
        OnPropertyChanged(nameof(RoutePreview));
        OnPropertyChanged(nameof(RequiresLinuxReauthorization));
        OnPropertyChanged(nameof(ReauthorizationHint));
    }

    partial void OnProtocolIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(IsHttps));
        OnPropertyChanged(nameof(HttpsCompatibilityHint));
        if (!UseCustomUrl)
        {
            UpdateGeneratedUrl();
        }
    }

    partial void OnLaunchPathChanged(string value)
    {
        if (!UseCustomUrl)
        {
            UpdateGeneratedUrl();
        }
    }

    partial void OnUseCustomUrlChanged(bool value)
    {
        OnPropertyChanged(nameof(UsesGeneratedUrl));
        OnPropertyChanged(nameof(IsHttps));
        if (!value)
        {
            UpdateGeneratedUrl();
        }
        else
        {
            NotifyBrowserUrlStateChanged();
        }
    }

    partial void OnHealthModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HealthModeExplanation));
        OnPropertyChanged(nameof(HttpsCompatibilityHint));
    }

    partial void OnHealthPathTextChanged(string value) =>
        OnPropertyChanged(nameof(HealthPathValidationHint));

    partial void OnUseRestartActionChanged(bool value)
    {
        OnPropertyChanged(nameof(HasRestartAction));
        OnPropertyChanged(nameof(RestartActionHint));
    }

    partial void OnRestartPathTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasRestartAction));
        OnPropertyChanged(nameof(RestartActionHint));
    }

    partial void OnRestartLocalPortTextChanged(string value) =>
        OnPropertyChanged(nameof(RestartActionHint));

    partial void OnUseCustomHealthPathChanged(bool value)
    {
        OnPropertyChanged(nameof(UsesDerivedHealthPath));
        if (!value)
        {
            UpdateDerivedHealthPath();
        }
    }

    partial void OnSelectedTemplateChanged(ServiceTemplateOptionViewModel? value)
    {
        TemplateApplyConfirmed = false;
        TemplateApplyFeedback = value is null
            ? "选择模板只会显示预览；确认并点击应用后才会改动当前草稿。"
            : $"预览：{value.Summary}。尚未修改当前草稿。";
        OnPropertyChanged(nameof(HasSelectedTemplate));
        OnPropertyChanged(nameof(CanApplySelectedTemplate));
    }

    partial void OnTemplateApplyConfirmedChanged(bool value) =>
        OnPropertyChanged(nameof(CanApplySelectedTemplate));

    partial void OnLocalUrlChanged(string value)
    {
        if (!_isSynchronizingSimpleUrl)
        {
            NotifyBrowserUrlStateChanged();
            UpdateDerivedHealthPath();
            OnPropertyChanged(nameof(IsHttps));
        }
    }

    [RelayCommand]
    private Task OpenAsync() => _open(this);

    [RelayCommand]
    private Task RetryAsync() => _retry(this);

    [RelayCommand]
    private Task RestartAsync() => _restart(this);

    [RelayCommand]
    private Task OpenLogAsync() => _openLog(this);

    [RelayCommand]
    private Task CopyAddressAsync() => _copy(this);

    [RelayCommand]
    private Task SetEnabledAsync() => _setEnabled(this, IsEnabled);

    [RelayCommand]
    private Task SaveAsync() => _save(this);

    [RelayCommand]
    private Task RemoveAsync() => _remove(this);

    [RelayCommand]
    private void ApplySuggestedLocalPort()
    {
        if (_localPortAvailability.Status == LocalPortAvailabilityStatus.InUse &&
            _localPortAvailability.SuggestedPort is int suggestedPort)
        {
            LocalPortText = suggestedPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [RelayCommand]
    private void ApplySelectedTemplate()
    {
        if (!CanApplySelectedTemplate ||
            !ServiceTemplateCatalog.TryCreate(SelectedTemplate!.TemplateId, out var template))
        {
            return;
        }

        Name = template!.Name;
        Description = template.Description;
        LocalPortText = template.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RemotePortText = template.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        IsEnabled = template.Enabled;

        if (ServiceOpenUrl.TryParse(template.OpenUrl, template.LocalPort, out var urlParts))
        {
            UseCustomUrl = false;
            ProtocolIndex = string.Equals(urlParts!.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
            LaunchPath = urlParts.LaunchPath;
        }
        else
        {
            UseCustomUrl = true;
            LocalUrl = template.OpenUrl;
        }

        HealthModeIndex = ToHealthModeIndex(template.HealthKind);
        var derivedHealthPath = ServiceRouteDefinition.DeriveHealthPath(template.OpenUrl, "/");
        UseCustomHealthPath = !string.Equals(
            template.HealthPath,
            derivedHealthPath,
            StringComparison.Ordinal);
        HealthPathText = template.HealthPath;

        TemplateApplyConfirmed = false;
        TemplateApplyFeedback = $"已将 {SelectedTemplate.DisplayName} 应用到当前草稿，尚未保存。请核对端口和网址后点击“保存更改”。";
    }

    public string PortMapping =>
        $"Windows 本机 :{LocalPortText}  →  Linux 回环 :{RemotePortText}";

    public string Protocol => ProtocolIndex == 1 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;

    public bool IsHttps => UseCustomUrl
        ? ServiceOpenUrl.TryParse(
              LocalUrl,
              int.TryParse(LocalPortText, out var localPort) ? localPort : 0,
              out var parts) &&
          string.Equals(parts!.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        : ProtocolIndex == 1;

    public string BrowserUrlPreview => string.IsNullOrWhiteSpace(LocalUrl)
        ? "等待生成浏览器地址"
        : LocalUrl.Trim();

    public string RoutePreview =>
        $"Windows 127.0.0.1:{LocalPortText}  →  Linux 127.0.0.1:{RemotePortText}";

    public bool HasLocalPortConflict =>
        _localPortAvailability.Status == LocalPortAvailabilityStatus.InUse;

    public bool HasSuggestedLocalPort =>
        HasLocalPortConflict && _localPortAvailability.SuggestedPort.HasValue;

    public string SuggestedLocalPortText => _localPortAvailability.SuggestedPort is int port
        ? $"使用 {port}"
        : "没有可推荐的端口";

    public string LocalPortAvailabilityHint
    {
        get
        {
            if (!int.TryParse(LocalPortText, out var localPort) || localPort is < 1 or > 65535)
            {
                return "输入 1–65535 后，软件会自动检查 Windows 本地 TCP 端口。";
            }

            if (_localPortAvailability.Status == LocalPortAvailabilityStatus.Available)
            {
                return localPort == _savedLocalPort && CanOpen
                    ? $"端口 {localPort} 正由当前 JK Bridge 转发使用，可以继续保留。"
                    : $"端口 {localPort} 当前未被 TCP 监听占用。";
            }

            if (_localPortAvailability.Status == LocalPortAvailabilityStatus.InUse)
            {
                return _localPortAvailability.SuggestedPort is int suggestion
                    ? $"端口 {localPort} 已被占用。建议改用 {suggestion}；只有点击右侧按钮才会更改。"
                    : $"端口 {localPort} 已被占用，暂时没有找到候选端口。";
            }

            return _localPortAvailability.Detail ?? "暂时无法检查 Windows 本地端口；保存时仍会执行配置校验。";
        }
    }

    public bool HasSelectedTemplate => SelectedTemplate is not null;

    public bool CanApplySelectedTemplate =>
        SelectedTemplate is not null && TemplateApplyConfirmed;

    public string HealthPathValidationHint => IsSafeHealthPath(HealthPathText)
        ? UseCustomHealthPath
            ? "健康检查使用这个独立路径，不影响浏览器打开页面。"
            : "默认跟随打开网址的路径；URL 查询参数和 # 后的片段不会参与健康检查。"
        : "健康路径必须以单个 / 开头，不能包含 #、反斜杠或控制字符。";

    /// <summary>
    /// Drives the restart button. Only a safe single-slash path enables the action;
    /// the token stays optional so services may rely on the loopback boundary.
    /// </summary>
    public bool HasRestartAction => UseRestartAction && IsSafeRestartPath(RestartPathText);

    partial void OnIsRestartingChanged(bool value) => OnPropertyChanged(nameof(RestartButtonText));

    partial void OnLastRestartTextChanged(string value) =>
        OnPropertyChanged(nameof(HasLastRestartText));

    public string RestartButtonText => IsRestarting ? "重启中…" : "重启";

    public bool HasLastRestartText => !string.IsNullOrWhiteSpace(LastRestartText);

    public string RestartActionHint
    {
        get
        {
            if (!UseRestartAction)
            {
                return "关闭时不显示重启按钮，已有的路径和令牌会保留在配置中。";
            }

            if (!IsSafeRestartPath(RestartPathText))
            {
                return "重启路径必须以单个 / 开头，不能包含 #、反斜杠、查询参数或控制字符。";
            }

            var portHint = string.IsNullOrWhiteSpace(RestartLocalPortText) || RestartLocalPortText == LocalPortText
                ? $"请求发送到本机端口 {LocalPortText}（当前服务的隧道）。"
                : $"请求发送到本机端口 {RestartLocalPortText}（另一个已有隧道的端口，用于共享重启控制端）。";

            return string.IsNullOrWhiteSpace(RestartTokenText)
                ? $"未设置令牌：任何能访问该 Windows 端口的程序都可以触发重启。{portHint}"
                : $"重启请求会经现有隧道 POST 到该路径，并携带令牌头；令牌保存在 Windows 凭据管理器中。{portHint}";
        }
    }

    /// <summary>
    /// Effective Windows-local port for the restart request; 0 reuses the service port.
    /// </summary>
    public int EffectiveRestartLocalPort =>
        int.TryParse(RestartLocalPortText, out var port) && port is >= 1 and <= 65535 ? port : 0;

    private static bool IsSafeRestartPath(string? path) =>
        IsSafeHealthPath(path) && !path!.Contains('?');

    public string HealthModeExplanation => HealthModeIndex switch
    {
        0 => "TCP 端口：只确认连接能够建立；适合自签名证书、SNI 不匹配或非 HTTP 服务。",
        2 => "JSON ok=true：请求健康路径，并要求响应 JSON 对象包含 ok=true；HTTPS 会严格验证证书。",
        _ => "HTTP 状态码：请求健康路径，并将 200–499 视为服务可达；HTTPS 会严格验证证书。",
    };

    public string HttpsCompatibilityHint => HealthModeIndex == 0
        ? "HTTPS 仍由浏览器直接校验证书。TCP 健康检查只能避免自签名或 SNI 不匹配造成错误的“服务离线”提示，不会消除浏览器证书警告。"
        : "JK Bridge 只转发原始 TCP，不改写证书、Host 或 SNI。浏览器使用 127.0.0.1，若证书只签给原始主机名，健康检查可能失败且浏览器会警告；可改用 TCP 健康检查进行连通性判断。";

    public bool RequiresLinuxReauthorization =>
        !int.TryParse(RemotePortText, out var remotePort) ||
        IsEnabled && remotePort != _savedRemotePort;

    public string ReauthorizationHint
    {
        get
        {
            if (!int.TryParse(RemotePortText, out var remotePort) || remotePort is < 1 or > 65535)
            {
                return "Linux 服务端口无效；修正端口后，如果与已保存端口不同，需要重新授权。";
            }

            if (remotePort == _savedRemotePort)
            {
                return "Linux 服务端口未变化；这次端口编辑本身不新增授权。Windows 端口和打开路径可以直接调整。";
            }

            return IsEnabled
                ? $"Linux 服务端口将从 {_savedRemotePort} 改为 {remotePort}；保存时软件会核对完整端口集合，必要时要求重新配对或由 Linux Agent 更新精确授权。"
                : $"Linux 服务端口将从 {_savedRemotePort} 改为 {remotePort}。服务当前已停用，暂不申请权限；以后启用时需要核对 Linux 授权。";
        }
    }

    public string BrowserUrlValidationHint
    {
        get
        {
            if (!int.TryParse(LocalPortText, out var localPort) || localPort is < 1 or > 65535)
            {
                return "请先填写有效的 Windows 本地端口。";
            }

            return ServiceOpenUrl.TryParse(LocalUrl, localPort, out _)
                ? "浏览器只访问当前 Windows 的 127.0.0.1，再由安全隧道转发到 Linux。"
                : "地址必须使用 http/https、127.0.0.1，并与上方 Windows 本地端口一致。";
        }
    }

    public bool TryCreateState(out ServiceUiState? state, out string error)
    {
        state = null;
        if (!int.TryParse(LocalPortText, out var localPort) || localPort is < 1 or > 65535)
        {
            error = "Windows 本地端口必须是 1–65535 的整数。";
            return false;
        }

        if (!int.TryParse(RemotePortText, out var remotePort) || remotePort is < 1 or > 65535)
        {
            error = "Linux 远端端口必须是 1–65535 的整数。";
            return false;
        }

        RefreshLocalPortAvailability();
        if (HasLocalPortConflict)
        {
            error = _localPortAvailability.SuggestedPort is int suggestion
                ? $"Windows 本地端口 {localPort} 已被占用。可以确认改用建议端口 {suggestion}，或关闭占用端口的程序后重试。"
                : $"Windows 本地端口 {localPort} 已被占用，请关闭占用端口的程序或选择其他端口。";
            return false;
        }

        if (!UseCustomUrl)
        {
            UpdateGeneratedUrl();
        }

        if (!ServiceOpenUrl.TryParse(LocalUrl, localPort, out _))
        {
            error = "浏览器地址必须使用 http/https、127.0.0.1，并与 Windows 本地端口一致。";
            return false;
        }

        if (!IsSafeHealthPath(HealthPathText))
        {
            error = "健康检查路径必须以单个 / 开头，不能包含 #、反斜杠或控制字符。";
            return false;
        }

        var restartPath = RestartPathText.Trim();
        var restartToken = RestartTokenText.Trim();
        var restartLocalPort = 0;
        if (UseRestartAction)
        {
            if (!IsSafeRestartPath(restartPath))
            {
                error = "重启路径必须以单个 / 开头，不能包含 #、反斜杠、查询参数或控制字符。";
                return false;
            }

            if (restartToken.Length > 256 || restartToken.Any(char.IsControl))
            {
                error = "重启令牌最多 256 个字符，且不能包含控制字符。";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(RestartLocalPortText))
            {
                if (!int.TryParse(RestartLocalPortText, out restartLocalPort) ||
                    restartLocalPort is < 1 or > 65535)
                {
                    error = "重启端口必须是 1–65535 的整数，或留空复用本服务端口。";
                    return false;
                }
            }
        }
        else
        {
            restartPath = string.Empty;
            restartToken = string.Empty;
        }

        state = new ServiceUiState(
            Id, ShortCode, Name, Description, LocalUrl.Trim(), localPort, remotePort,
            Status, HealthDetail, IsEnabled, IsAvailable, HasWarning, CanOpen,
            FromHealthModeIndex(HealthModeIndex), HealthPathText.Trim(), restartPath, restartToken,
            restartLocalPort);
        error = string.Empty;
        return true;
    }

    public void ApplyRuntimeState(ServiceUiState state)
    {
        Status = state.Status;
        HealthDetail = state.HealthDetail;
        IsEnabled = state.IsEnabled;
        IsAvailable = state.IsAvailable;
        HasWarning = state.HasWarning;
        CanOpen = state.CanOpen;
        LastRestartText = state.LastRestartText;
    }

    private static int ToHealthModeIndex(HealthCheckKind kind) => kind switch
    {
        HealthCheckKind.TcpConnect => 0,
        HealthCheckKind.HttpStatus => 1,
        HealthCheckKind.JsonOk => 2,
        _ => 1,
    };

    private static HealthCheckKind FromHealthModeIndex(int index) => index switch
    {
        0 => HealthCheckKind.TcpConnect,
        1 => HealthCheckKind.HttpStatus,
        2 => HealthCheckKind.JsonOk,
        _ => HealthCheckKind.HttpStatus,
    };

    private void UpdateGeneratedUrl()
    {
        if (!int.TryParse(LocalPortText, out var localPort) ||
            !ServiceOpenUrl.TryBuild(Protocol, localPort, NormalizeLaunchPath(LaunchPath), out var openUrl))
        {
            NotifyBrowserUrlStateChanged();
            return;
        }

        _isSynchronizingSimpleUrl = true;
        try
        {
            LocalUrl = openUrl;
        }
        finally
        {
            _isSynchronizingSimpleUrl = false;
        }

        NotifyBrowserUrlStateChanged();
        UpdateDerivedHealthPath();
    }

    private void NotifyBrowserUrlStateChanged()
    {
        OnPropertyChanged(nameof(BrowserUrlPreview));
        OnPropertyChanged(nameof(BrowserUrlValidationHint));
    }

    private void UpdateDerivedHealthPath()
    {
        if (!UseCustomHealthPath)
        {
            HealthPathText = ServiceRouteDefinition.DeriveHealthPath(LocalUrl, "/");
        }
    }

    private void RefreshLocalPortAvailability()
    {
        if (!int.TryParse(LocalPortText, out var localPort) || localPort is < 1 or > 65535)
        {
            _localPortAvailability = new(
                LocalPortAvailabilityStatus.Unknown,
                Detail: "端口必须是 1–65535 的整数。");
        }
        else if (localPort == _savedLocalPort && CanOpen)
        {
            // The active SSH process owns this listener. Treat it as the current
            // valid route instead of presenting JK Bridge as a conflicting app.
            _localPortAvailability = new(LocalPortAvailabilityStatus.Available);
        }
        else
        {
            _localPortAvailability = _localPortProbe.Check(localPort);
        }

        OnPropertyChanged(nameof(HasLocalPortConflict));
        OnPropertyChanged(nameof(HasSuggestedLocalPort));
        OnPropertyChanged(nameof(SuggestedLocalPortText));
        OnPropertyChanged(nameof(LocalPortAvailabilityHint));
    }

    private static string NormalizeLaunchPath(string? launchPath)
    {
        var trimmed = launchPath?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "/";
        }

        return trimmed.StartsWith("/", StringComparison.Ordinal)
            ? trimmed
            : $"/{trimmed}";
    }

    private static bool IsSafeHealthPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= 2048 &&
        path.StartsWith("/", StringComparison.Ordinal) &&
        !path.StartsWith("//", StringComparison.Ordinal) &&
        !path.Contains('#') &&
        !path.Contains('\\') &&
        !path.Any(char.IsControl) &&
        Uri.IsWellFormedUriString(path, UriKind.Relative);
}
