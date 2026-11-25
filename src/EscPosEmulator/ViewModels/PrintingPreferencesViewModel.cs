using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EscPosEmulator.Models;
using EscPosEmulator.Services;

namespace EscPosEmulator.ViewModels;

/// <summary>
/// View model for the Windows-style "XP-80C — Printing Preferences" dialog:
/// Layout / Paper-Quality / Advanced tabs plus the "Advanced Document
/// Settings" properties dialog (see the Windows printer preferences of a
/// physical Xprinter / Zjiang 80 mm thermal printer).
/// </summary>
public partial class PrintingPreferencesViewModel : ObservableObject
{
    private readonly RenderOptions _current;
    private AppSettings? _settings;

    public PrintingPreferencesViewModel(RenderOptions current, string paperLabel)
    {
        ArgumentNullException.ThrowIfNull(current);
        _current = current;
        PaperLabel = paperLabel;

        HalftoningOptions = new[]
        {
            HalftoningMode.None,
            HalftoningMode.Dots,
            HalftoningMode.Smooth,
        };

        OkCommand = new RelayCommand(Ok);
        CancelCommand = new RelayCommand(Cancel);
        ApplyCommand = new RelayCommand(Apply);
        ResetCommand = new RelayCommand(Reset);
        OpenAdvancedPropertiesCommand = new RelayCommand(OnOpenAdvancedProperties);

        LoadFrom(current);
        RefreshAdvancedRows();

        // keep the advanced-tab summary in sync while the dialog is open
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Halftoning) or nameof(Copies) or nameof(PageScale)
                or nameof(LineSpacing) or nameof(DrawBorders) or nameof(MarginTop)
                or nameof(MarginLeft) or nameof(PrintOrderIsFrontToBack)
                or nameof(PrintOrderIsRightThenDown))
                RefreshAdvancedRows();
        };
    }

    /// <summary>Set by the caller; values are written to settings.json on OK.</summary>
    public void ConfigurePersistence(AppSettings settings) => _settings = settings;

    /// <summary>Device label shown in the dialog header (e.g. "80 mm · 300 mm").</summary>
    public string PaperLabel { get; }

    /// <summary>Raised exactly once when the user hits OK, Cancel, or closes the window.</summary>
    public event Action<RenderOptions>? Completed;

    /// <summary>Raised after OK / Apply copies the draft onto the live options (preview refresh).</summary>
    public event Action<RenderOptions>? Applied;

    /// <summary>The live options instance (unchanged until OK is pressed).</summary>
    public RenderOptions CurrentOptions => _current;

    /// <summary>True once the user has confirmed (OK / Apply) or cancelled —
    /// used by the window so its OnClosed hook does not fire Completed twice.</summary>
    public bool IsDone { get; private set; }

    /// <summary>
    /// Raised when the user opens the "Advanced Document Settings" dialog
    /// from the Advanced tab. The view model is passed so the secondary
    /// dialog can show the same values.
    /// </summary>
    public event Action<AdvancedDocumentSettingsViewModel>? AdvancedPropertiesOpened;

    // ── commands (observable so XAML binds {Binding OkCommand} etc.) ──
    [ObservableProperty] private IRelayCommand _okCommand;
    [ObservableProperty] private IRelayCommand _cancelCommand;
    [ObservableProperty] private IRelayCommand _applyCommand;
    [ObservableProperty] private IRelayCommand _resetCommand;
    [ObservableProperty] private IRelayCommand _openAdvancedPropertiesCommand;

    // ── editable draft values ───────────────────────────────────
    [ObservableProperty] private bool _printOrderIsFrontToBack = true;
    [ObservableProperty] private bool _printOrderIsRightThenDown;
    [ObservableProperty] private double _pageScale = 1.0;
    [ObservableProperty] private int _lineSpacing;
    [ObservableProperty] private bool _drawBorders = true;
    [ObservableProperty] private int _marginTop = 30;
    [ObservableProperty] private int _marginLeft;
    [ObservableProperty] private int _copies = 1;
    [ObservableProperty] private HalftoningMode _halftoning;
    [ObservableProperty] private HalftoningMode[] _halftoningOptions = [];
    [ObservableProperty] private IReadOnlyList<PropertyValue> _advancedRows = [];

    private void LoadFrom(RenderOptions o)
    {
        PrintOrderIsFrontToBack = o.PrintFrontToBack;
        // the handler above keeps these mutually exclusive; set the off one first
        PrintOrderIsRightThenDown = !o.PrintFrontToBack;
        PageScale = o.PageScale;
        LineSpacing = o.LineSpacing;
        DrawBorders = o.DrawBorders;
        MarginTop = o.MarginTop;
        MarginLeft = o.MarginLeft;
        Copies = o.Copies;
        Halftoning = o.Halftoning;
    }

    partial void OnPrintOrderIsFrontToBackChanged(bool value)
    {
        if (value) PrintOrderIsRightThenDown = false;
    }

    partial void OnPrintOrderIsRightThenDownChanged(bool value)
    {
        if (value) PrintOrderIsFrontToBack = false;
    }

    public string HalftoningCaption => Halftoning switch
    {
        HalftoningMode.None => "No halftoning",
        HalftoningMode.Dots => "Halftoning (ordered dots)",
        HalftoningMode.Smooth => "Halftoning (smooth / error diffusing)",
        _ => "No halftoning",
    };

    partial void OnHalftoningChanged(HalftoningMode value) => OnPropertyChanged(nameof(HalftoningCaption));

    // ── commands body ───────────────────────────────────────────

    /// <summary>OK = Apply the draft, then close the dialog.</summary>
    private void Ok()
    {
        Apply();
        Finish();
    }

    /// <summary>Cancel closes without touching the live options.</summary>
    private void Cancel() => Finish();

    /// <summary>Apply copies the draft onto the live options and persists, but keeps the dialog open.</summary>
    private void Apply()
    {
        NormalizeDraft();

        _current.Copies = Copies;
        _current.Halftoning = Halftoning;
        _current.PrintFrontToBack = PrintOrderIsFrontToBack;
        _current.DrawBorders = DrawBorders;
        _current.PageScale = PageScale;
        _current.LineSpacing = LineSpacing;
        _current.MarginTop = MarginTop;
        _current.MarginLeft = MarginLeft;

        // keep settings.json in sync with the live instance
        if (_settings is { } s)
        {
            s.PrinterSettings = CloneOf(_current);
            AppSettings.Save(s);
        }

        Applied?.Invoke(_current);
    }

    /// <summary>Restore Defaults resets only the draft; nothing is applied until OK / Apply.</summary>
    private void Reset() => LoadFrom(new RenderOptions());

    private void NormalizeDraft()
    {
        Copies = Math.Clamp(Copies, 1, 99);
        PageScale = Math.Clamp(PageScale, 0.5, 4.0);
        LineSpacing = Math.Clamp(LineSpacing, 0, 99);
        MarginTop = Math.Clamp(MarginTop, 0, 120);
        MarginLeft = Math.Clamp(MarginLeft, 0, 60);
    }

    /// <summary>Called by the window when the user closes it without OK / Cancel; behaves as Cancel.</summary>
    public void HandleWindowClosed() => Finish();

    /// <summary>Fires Completed exactly once (OK, Cancel, or closing the window).</summary>
    private void Finish()
    {
        if (IsDone) return;
        IsDone = true;
        Completed?.Invoke(_current);
    }

    private void OnOpenAdvancedProperties() =>
        AdvancedPropertiesOpened?.Invoke(new AdvancedDocumentSettingsViewModel(BuildDraft()));

    private void RefreshAdvancedRows() =>
        AdvancedRows = new AdvancedDocumentSettingsViewModel(BuildDraft()).Rows;

    /// <summary>Snapshot of the draft values (clamped) for the summary grid / advanced dialog.</summary>
    private RenderOptions BuildDraft() => new()
    {
        Copies = Math.Clamp(Copies, 1, 99),
        Halftoning = Halftoning,
        Landscape = _current.Landscape,
        PrintFrontToBack = PrintOrderIsFrontToBack,
        DrawBorders = DrawBorders,
        PageScale = Math.Clamp(PageScale, 0.5, 4.0),
        LineSpacing = Math.Clamp(LineSpacing, 0, 99),
        MarginTop = Math.Clamp(MarginTop, 0, 120),
        MarginLeft = Math.Clamp(MarginLeft, 0, 60),
    };

    private static RenderOptions CloneOf(RenderOptions o) => new()
    {
        Copies = o.Copies,
        Halftoning = o.Halftoning,
        Landscape = o.Landscape,
        PrintFrontToBack = o.PrintFrontToBack,
        DrawBorders = o.DrawBorders,
        PageScale = o.PageScale,
        LineSpacing = o.LineSpacing,
        MarginTop = o.MarginTop,
        MarginLeft = o.MarginLeft,
    };
}

/// <summary>
/// View model of the "Advanced Document Settings" properties dialog — a
/// Windows-style properties grid (Property / Value rows). Editing a value
/// in the main preferences dialog updates the shared options, and a new
/// instance of this view model always reflects the current state.
/// </summary>
public sealed class AdvancedDocumentSettingsViewModel
{
    public string PrinterName { get; } = "XP-80C (Virtual)";

    public AdvancedDocumentSettingsViewModel(RenderOptions options) => _options = options;

    private readonly RenderOptions _options;

    public PropertyValue[] Rows =>
    [
        new("Print order", _options.PrintFrontToBack ? "Front to back" : "Right, then down"),
        new("Halftoning", HalftoningLabel(_options.Halftoning)),
        new("Copies", _options.Copies.ToString()),
        new("Page scale", $"{_options.PageScale:0.##} ×"),
        new("Line height", _options.LineSpacing == 0 ? "Follow printer stream" : $"{_options.LineSpacing} × 18 dots"),
        new("Draw paper edges", _options.DrawBorders ? "Yes" : "No"),
        new("Top margin", $"{_options.MarginTop} dots"),
        new("Left margin", $"{_options.MarginLeft} dots"),
    ];

    private static string HalftoningLabel(HalftoningMode mode) => mode switch
    {
        HalftoningMode.None => "No halftoning",
        HalftoningMode.Dots => "Halftoning (ordered dots)",
        HalftoningMode.Smooth => "Halftoning (smooth)",
        _ => "No halftoning",
    };
}
