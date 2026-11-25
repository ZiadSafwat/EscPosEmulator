using Avalonia.Controls;
using EscPosEmulator.ViewModels;

namespace EscPosEmulator.Views;

public partial class PrintingPreferencesWindow : Window
{
    public PrintingPreferencesWindow(PrintingPreferencesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.AdvancedPropertiesOpened += vm =>
        {
            var w = new AdvancedDocumentSettingsWindow(vm);
            _ = w.ShowDialog(this);
        };
    }

    protected override void OnClosed(System.EventArgs e)
    {
        // Closing via the X button or Alt+F4 counts as "Cancel".
        if (DataContext is PrintingPreferencesViewModel vm)
            vm.HandleWindowClosed();
        base.OnClosed(e);
    }
}
