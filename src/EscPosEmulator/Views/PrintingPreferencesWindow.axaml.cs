using System;
using Avalonia.Controls;
using EscPosEmulator.ViewModels;

namespace EscPosEmulator.Views;

public partial class PrintingPreferencesWindow : Window
{
    private bool _closing;

    public PrintingPreferencesWindow(PrintingPreferencesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // OK / Cancel request a close; the view model has already applied
        // (OK) or ignored (Cancel) the draft by the time this fires.
        viewModel.Completed += _ => CloseFromViewModel();

        viewModel.AdvancedPropertiesOpened += vm =>
        {
            var w = new AdvancedDocumentSettingsWindow(vm);
            _ = w.ShowDialog(this);
        };
    }

    private void CloseFromViewModel()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing via the X button or Alt+F4 counts as "Cancel".
        _closing = true;
        if (DataContext is PrintingPreferencesViewModel vm)
            vm.HandleWindowClosed();
        base.OnClosed(e);
    }
}
