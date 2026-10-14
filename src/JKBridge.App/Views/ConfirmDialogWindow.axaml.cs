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
    {
        // Compiled bindings resolve during InitializeComponent, so the data
        // context must already be in place when the XAML loads.
        DataContext = viewModel;
        InitializeComponent();
    }
}
