using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JKBridge.App.ViewModels;

/// <summary>
/// Presentation state for the reusable modal confirmation dialog.
/// </summary>
public sealed partial class ConfirmDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _confirmText = "确认";
    [ObservableProperty] private string _cancelText = "取消";
    [ObservableProperty] private bool _isDangerous;

    public bool Confirmed { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        Close();
    }

    [RelayCommand]
    private void Cancel() => Close();

    private void Close()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.Windows.FirstOrDefault(window => window.DataContext == this) is Avalonia.Controls.Window window)
        {
            window.Close(Confirmed);
        }
    }
}
