using Avalonia.Controls;
using Avalonia.Interactivity;
using EscPosEmulator.ViewModels;

namespace EscPosEmulator.Views;

public partial class AdvancedDocumentSettingsWindow : Window
{
    public AdvancedDocumentSettingsWindow(AdvancedDocumentSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnAccept(object? sender, RoutedEventArgs e) => Close();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
