using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using JKBridge.App.ViewModels;

namespace JKBridge.App.Views;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
    }

    public LogWindow(LogWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
