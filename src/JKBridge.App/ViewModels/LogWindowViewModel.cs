using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JKBridge.App.ViewModels;

/// <summary>
/// Presentation state for the Linux service log viewer.
/// </summary>
public sealed partial class LogWindowViewModel : ViewModelBase
{
    private readonly Func<int, Task<string>> _fetch;

    [ObservableProperty] private string _title = "Linux 日志";
    [ObservableProperty] private string _logText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _feedback = string.Empty;
    [ObservableProperty] private int _lineCountIndex = 1;

    public LogWindowViewModel(Func<int, Task<string>> fetch)
    {
        _fetch = fetch;
    }

    public int LineCount => LineCountIndex switch
    {
        0 => 100,
        2 => 500,
        _ => 300,
    };

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Feedback = "正在读取…";
        try
        {
            LogText = await _fetch(LineCount);
            Feedback = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            Feedback = $"读取失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.Windows.FirstOrDefault(window => window.DataContext == this) is Avalonia.Controls.Window window)
        {
            window.Close();
        }
    }
}
