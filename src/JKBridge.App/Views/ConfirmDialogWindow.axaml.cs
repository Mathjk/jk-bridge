using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using JKBridge.App.ViewModels;

namespace JKBridge.App.Views;

public partial class ConfirmDialogWindow : Window
{
    public ConfirmDialogWindow()
    {
        InitializeComponent();
    }

    public ConfirmDialogWindow(ConfirmDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
