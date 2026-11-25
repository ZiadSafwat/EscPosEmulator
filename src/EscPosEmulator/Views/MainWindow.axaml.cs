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
            if (DataContext is MainViewModel vm)
                vm.PropertyChanged += OnVmPropertyChanged;
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
