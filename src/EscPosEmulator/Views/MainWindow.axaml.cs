using Avalonia.Controls;
using EscPosEmulator.ViewModels;

namespace EscPosEmulator.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // DataContext is assigned after construction (see App.axaml.cs),
        // so defer VM wiring until the window is loaded.
        Loaded += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.PropertyChanged += OnVmPropertyChanged;

            // "Printer Preferences…" opens the Windows-style dialog on top
            // of the live render options; OK persists + redraws the preview.
            vm.PrintSettingsRequested += options =>
            {
                string paper = vm.SelectedPaperSize?.Name ?? "80 mm";
                var dialogVm = new PrintingPreferencesViewModel(options, $"{paper} · 300 mm paper roll");
                dialogVm.ConfigurePersistence(vm.Settings);
                dialogVm.Applied += _ => vm.OnPrinterSettingsChanged();   // OK / Apply only
                _ = new PrintingPreferencesWindow(dialogVm).ShowDialog(this);
            };
        };

        Closed += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged -= OnVmPropertyChanged;
                vm.Dispose();
            }
        };
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.LogText))
            return;
        if (this.FindControl<TextBox>("LogBox") is { } log)
        {
            log.CaretIndex = log.Text?.Length ?? 0;
        }
    }
}
