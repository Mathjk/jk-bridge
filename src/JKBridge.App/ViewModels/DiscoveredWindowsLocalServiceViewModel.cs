using CommunityToolkit.Mvvm.Input;

namespace JKBridge.App.ViewModels;

public partial class DiscoveredWindowsLocalServiceViewModel : ViewModelBase
{
    private readonly Func<DiscoveredWindowsLocalServiceViewModel, Task> _open;
    private readonly Func<DiscoveredWindowsLocalServiceViewModel, Task> _copy;
    private readonly Func<DiscoveredWindowsLocalServiceViewModel, Task> _save;

    public DiscoveredWindowsLocalServiceViewModel(
        DiscoveredWindowsLocalServiceUiState state,
        Func<DiscoveredWindowsLocalServiceViewModel, Task> open,
        Func<DiscoveredWindowsLocalServiceViewModel, Task> copy,
        Func<DiscoveredWindowsLocalServiceViewModel, Task> save)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        _save = save ?? throw new ArgumentNullException(nameof(save));
    }

    public DiscoveredWindowsLocalServiceUiState State { get; }

    public string Key => State.Key;
    public string DisplayName => State.DisplayName;
    public string Address => State.Address;
    public string ProcessName => State.ProcessName;
    public string Protocol => State.Protocol;
    public string OpenUrl => State.OpenUrl;
    public string Status => State.Status;
    public string Detail => State.Detail;
    public bool IsWeb => State.IsWeb;
    public bool CanSave => State.CanSave;
    public bool IsExistingTunnel => State.IsExistingTunnel;
    public bool CertificateUnverified => State.CertificateUnverified;
    public bool CanOpen => State.IsWeb && !State.IsExistingTunnel;

    public string ProcessText => string.IsNullOrWhiteSpace(ProcessName)
        ? "未能读取进程名"
        : $"进程：{ProcessName}";

    public string TypeText => IsExistingTunnel
        ? "Linux 隧道"
        : IsWeb
            ? Protocol.ToUpperInvariant()
            : "仅 TCP";

    [RelayCommand]
    private Task OpenAsync() => CanOpen ? _open(this) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyAsync() =>
        CanOpen ? _copy(this) : Task.CompletedTask;

    [RelayCommand]
    private Task SaveAsync() =>
        CanSave ? _save(this) : Task.CompletedTask;
}
