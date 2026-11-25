using Avalonia.Controls;
using EscPosEmulator.ViewModels;

namespace EscPosEmulator.Views;

public partial class AdvancedDocumentSettingsWindow : Window
{
    public AdvancedDocumentSettingsWindow(AdvancedDocumentSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnAccept(object sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
