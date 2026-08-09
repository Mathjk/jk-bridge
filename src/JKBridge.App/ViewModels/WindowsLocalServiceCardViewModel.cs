using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.App.ViewModels;

public partial class WindowsLocalServiceCardViewModel : ViewModelBase
{
    private readonly Func<WindowsLocalServiceCardViewModel, Task> _open;
    private readonly Func<WindowsLocalServiceCardViewModel, Task> _retry;
    private readonly Func<WindowsLocalServiceCardViewModel, Task> _copy;
    private readonly Func<WindowsLocalServiceCardViewModel, Task> _save;
    private readonly Func<WindowsLocalServiceCardViewModel, Task> _remove;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _portText;
    [ObservableProperty] private int _hostIndex;
    [ObservableProperty] private int _schemeIndex;
    [ObservableProperty] private string _launchPath;
    [ObservableProperty] private string _processName;
    [ObservableProperty] private string _status;
    [ObservableProperty] private string _detail;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private bool _canOpen;
    [ObservableProperty] private int _healthModeIndex;

    public WindowsLocalServiceCardViewModel(
        WindowsLocalServiceUiState state,
        Func<WindowsLocalServiceCardViewModel, Task> open,
        Func<WindowsLocalServiceCardViewModel, Task> retry,
        Func<WindowsLocalServiceCardViewModel, Task> copy,
        Func<WindowsLocalServiceCardViewModel, Task> save,
        Func<WindowsLocalServiceCardViewModel, Task> remove)
    {
        ArgumentNullException.ThrowIfNull(state);
        Id = state.Id;
        _name = state.Name;
        _description = state.Description;
        _portText = state.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _hostIndex = state.Host == WindowsLocalServiceUrl.Ipv6Loopback ? 1 : 0;
        _schemeIndex = string.Equals(state.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        _launchPath = state.LaunchPath;
        _processName = state.ProcessName;
        _status = state.Status;
        _detail = state.Detail;
        _isEnabled = state.IsEnabled;
        _isAvailable = state.IsAvailable;
        _hasWarning = state.HasWarning;
        _canOpen = state.CanOpen;
        _healthModeIndex = ToHealthModeIndex(state.HealthKind);
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _remove = remove ?? throw new ArgumentNullException(nameof(remove));
    }

    public string Id { get; }

    public string Host => HostIndex == 1
        ? WindowsLocalServiceUrl.Ipv6Loopback
        : WindowsLocalServiceUrl.Ipv4Loopback;

    public string Scheme => SchemeIndex == 1 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;

    public string LocalUrlPreview =>
        int.TryParse(PortText, out var port) &&
        WindowsLocalServiceUrl.TryBuild(Host, Scheme, port, NormalizePath(LaunchPath), out var url)
            ? url
            : "请填写有效的本机回环网址";

    public string ProcessText => string.IsNullOrWhiteSpace(ProcessName)
        ? "进程未知或当前离线"
        : $"进程：{ProcessName}";

    public string ShortCode
    {
        get
        {
            var letters = new string(Name.Where(char.IsLetterOrDigit).Take(4).ToArray());
            return letters.Length == 0 ? "PC" : letters.ToUpperInvariant();
        }
    }

    public bool TryCreateState(out WindowsLocalServiceUiState? state, out string error)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 80)
        {
            error = "服务名称必须包含 1–80 个字符。";
            return false;
        }

        if (!int.TryParse(PortText, out var port) || port is < 1 or > 65535)
        {
            error = "本机端口必须是 1–65535 的整数。";
            return false;
        }

        var path = NormalizePath(LaunchPath);
        if (!WindowsLocalServiceUrl.TryBuild(Host, Scheme, port, path, out _))
        {
            error = "打开路径必须以单个 / 开头，且只能形成回环 HTTP(S) 地址。";
            return false;
        }

        state = new(
            Id,
            Name.Trim(),
            Description.Trim(),
            Host,
            port,
            Scheme,
            path,
            ProcessName,
            Status,
            Detail,
            IsEnabled,
            IsAvailable,
            HasWarning,
            CanOpen,
            FromHealthModeIndex(HealthModeIndex));
        error = string.Empty;
        return true;
    }

    public void ApplyRuntimeState(WindowsLocalServiceUiState state)
    {
        ProcessName = state.ProcessName;
        Status = state.Status;
        Detail = state.Detail;
        IsAvailable = state.IsAvailable;
        HasWarning = state.HasWarning;
        CanOpen = state.CanOpen;
        OnPropertyChanged(nameof(ProcessText));
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(ShortCode));
    }

    partial void OnPortTextChanged(string value)
    {
        OnPropertyChanged(nameof(LocalUrlPreview));
    }

    partial void OnHostIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(LocalUrlPreview));
    }

    partial void OnSchemeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Scheme));
        OnPropertyChanged(nameof(LocalUrlPreview));
    }

    partial void OnLaunchPathChanged(string value)
    {
        OnPropertyChanged(nameof(LocalUrlPreview));
    }

    partial void OnProcessNameChanged(string value)
    {
        OnPropertyChanged(nameof(ProcessText));
    }

    [RelayCommand]
    private Task OpenAsync() => CanOpen ? _open(this) : Task.CompletedTask;

    [RelayCommand]
    private Task RetryAsync() => _retry(this);

    [RelayCommand]
    private Task CopyAsync() => _copy(this);

    [RelayCommand]
    private Task SaveAsync() => _save(this);

    [RelayCommand]
    private Task RemoveAsync() => _remove(this);

    private static string NormalizePath(string? path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "/";
        }

        return trimmed.StartsWith("/", StringComparison.Ordinal) ? trimmed : $"/{trimmed}";
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
}
