using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JKBridge.Core.Models;

namespace JKBridge.App.ViewModels;

public partial class ServiceCardViewModel : ViewModelBase
{
    private readonly Func<ServiceCardViewModel, Task> _open;
    private readonly Func<ServiceCardViewModel, Task> _retry;
    private readonly Func<ServiceCardViewModel, Task> _copy;
    private readonly Func<ServiceCardViewModel, bool, Task> _setEnabled;
    private readonly Func<ServiceCardViewModel, Task> _save;
    private readonly Func<ServiceCardViewModel, Task> _remove;

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

    public bool IsUnavailable => !IsAvailable && !HasWarning;

    public ServiceCardViewModel(
        ServiceUiState state,
        Func<ServiceCardViewModel, Task> open,
        Func<ServiceCardViewModel, Task> retry,
        Func<ServiceCardViewModel, Task> copy,
        Func<ServiceCardViewModel, bool, Task> setEnabled,
        Func<ServiceCardViewModel, Task> save,
        Func<ServiceCardViewModel, Task> remove)
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
        _open = open;
        _retry = retry;
        _copy = copy;
        _setEnabled = setEnabled;
        _save = save;
        _remove = remove;
    }

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(IsUnavailable));
    partial void OnHasWarningChanged(bool value) => OnPropertyChanged(nameof(IsUnavailable));
    partial void OnLocalPortTextChanged(string value) => OnPropertyChanged(nameof(PortMapping));
    partial void OnRemotePortTextChanged(string value) => OnPropertyChanged(nameof(PortMapping));

    [RelayCommand]
    private Task OpenAsync() => _open(this);

    [RelayCommand]
    private Task RetryAsync() => _retry(this);

    [RelayCommand]
    private Task CopyAddressAsync() => _copy(this);

    [RelayCommand]
    private Task SetEnabledAsync() => _setEnabled(this, IsEnabled);

    [RelayCommand]
    private Task SaveAsync() => _save(this);

    [RelayCommand]
    private Task RemoveAsync() => _remove(this);

    public string PortMapping =>
        $"127.0.0.1:{LocalPortText}  →  127.0.0.1:{RemotePortText}";

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

        state = new ServiceUiState(
            Id, ShortCode, Name, Description, LocalUrl, localPort, remotePort,
            Status, HealthDetail, IsEnabled, IsAvailable, HasWarning, CanOpen,
            FromHealthModeIndex(HealthModeIndex));
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
