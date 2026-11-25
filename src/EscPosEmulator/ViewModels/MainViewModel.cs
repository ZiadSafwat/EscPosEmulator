using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EscPosEmulator.Core;
using EscPosEmulator.Models;
using EscPosEmulator.Parsing;
using EscPosEmulator.Rendering;
using EscPosEmulator.Services;
using SkiaSharp;

namespace EscPosEmulator.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly EscPosParser _parser = new();
    private readonly ReceiptRenderer _renderer = new();
    private readonly TcpPrinterServer _server;
    private readonly PrintService _printService = new();
    private readonly VirtualPrinterService _virtualPrinter = new();
    private readonly AppSettings _settings;

    private SKBitmap? _lastSkBitmap;   // keep last render for zoom re-scaling
    private readonly RenderOptions _printRenderOptions = new();
    private readonly DispatcherTimer _previewDebounce;

    /// <summary>Render options the preview is built with (updated from the UI).</summary>
    public RenderOptions PrintRenderOptions => _printRenderOptions;

    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private string _serverStatus = "Stopped";
    [ObservableProperty] private bool _isServerRunning;
    [ObservableProperty] private int _port = 9100;
    [ObservableProperty] private bool _listenOnLan;
    [ObservableProperty] private PaperSize? _selectedPaperSize;
    [ObservableProperty] private PrinterProfile? _selectedProfile;
    [ObservableProperty] private string _selectedCodePage = "CP437 USA (0)";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private int _jobCount;
    [ObservableProperty] private int _drawerKickCount;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _profileNotes = "";
    [ObservableProperty] private double _zoomLevel = 1.0;
    [ObservableProperty] private string _zoomLabel = "100%";

    // ── printer settings (persisted) ────────────────────────────────
    [ObservableProperty] private bool _startServerOnLaunch;
    [ObservableProperty] private string _systemPrinterName = "";
    [ObservableProperty] private string _printStatus = "";
    [ObservableProperty] private string _virtualPrinterName = "";
    [ObservableProperty] private string _virtualPrinterDriver = "";
    [ObservableProperty] private string _virtualPrinterDriverHint = "";
    [ObservableProperty] private string _virtualPrinterStatus = "";
    [ObservableProperty] private bool _isVirtualPrinterInstalled;
    [ObservableProperty] private bool _isVirtualPrinterBusy;
    [ObservableProperty] private string _serverBadgeText = "STOPPED";
    [ObservableProperty] private Avalonia.Media.IBrush _serverBadgeBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xC4, 0x2B, 0x1C));

    // ── Arabic / installed ESC-POS printer (auto-detected) ────────────────
    // Best installed real thermal printer the app found on this machine.
    [ObservableProperty] private string _escPosPrinterName = "";
    [ObservableProperty] private bool _hasEscPosPrinter;
    [ObservableProperty] private string _arabicAdvice = "";

    private static readonly Avalonia.Media.IBrush BadgeRunning =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x2D, 0x7A, 0x27));
    private static readonly Avalonia.Media.IBrush BadgeStopped =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xC4, 0x2B, 0x1C));

    public ObservableCollection<PaperSize> PaperSizes { get; } = new(PaperSize.Presets);
    public ObservableCollection<PrinterProfile> Profiles { get; } = new(PrinterProfile.All);
    public ObservableCollection<string> SystemPrinters { get; } = new();
    public ObservableCollection<string> VirtualPrinterDrivers { get; } = new();
    public ObservableCollection<string> CodePages { get; } = new(EncodingHelper.Tables.Select(t => t.Label));

    public ICommand StartServerCommand { get; }
    public ICommand StopServerCommand { get; }
    public ICommand ClearReceiptCommand { get; }
    public ICommand SendDemoCommand { get; }
    public ICommand SendArabicDemoCommand { get; }
    public ICommand AddCustomSizeCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomResetCommand { get; }
    public ICommand PrintToSystemCommand { get; }
    public ICommand RefreshPrintersCommand { get; }
    public ICommand InstallVirtualPrinterCommand { get; }
    public ICommand RemoveVirtualPrinterCommand { get; }
    public ICommand SelectEscPosPrinterCommand { get; }

    public MainViewModel()
    {
        EncodingHelper.EnsureRegistered();
        _settings = AppSettings.Load();

        Port = _settings.Port > 0 ? _settings.Port : 9100;
        ListenOnLan = _settings.ListenOnLan;
        StartServerOnLaunch = _settings.StartServerOnLaunch;

        // Render options: seed the live instance from the persisted copy.
        _printRenderOptions.PageScale = _settings.PrinterSettings.PageScale;
        _printRenderOptions.Halftoning = _settings.PrinterSettings.Halftoning;
        _printRenderOptions.Copies = _settings.PrinterSettings.Copies;
        _printRenderOptions.LineSpacing = _settings.PrinterSettings.LineSpacing;
        _printRenderOptions.DrawBorders = _settings.PrinterSettings.DrawBorders;
        _printRenderOptions.PrintFrontToBack = _settings.PrinterSettings.PrintFrontToBack;
        _printRenderOptions.MarginTop = _settings.PrinterSettings.MarginTop;
        _printRenderOptions.MarginLeft = _settings.PrinterSettings.MarginLeft;

        SystemPrinterName = _settings.ForwardPrinterName ?? "";
        VirtualPrinterDriver = _settings.VirtualPrinterDriver ?? "";
        VirtualPrinterName = _settings.VirtualPrinterName ?? "";

        // Capture the table saved by the user before selecting a profile:
        // ApplyProfile writes the profile default into _settings.SelectedCodePage,
        // which would otherwise lose a saved Arabic / UTF-8 choice on restart.
        string savedCodePage = _settings.SelectedCodePage;

        // Safe default profile
        SelectedProfile = Profiles.FirstOrDefault(p =>
                                string.Equals(p.DisplayName, _settings.SelectedProfile, StringComparison.OrdinalIgnoreCase))
                           ?? Profiles.FirstOrDefault()
                           ?? PrinterProfile.All.First();

        // ApplyProfile (triggered by the SelectedProfile assignment above) has
        // already picked the matching preset from PaperSizes. Do not overwrite
        // it with the profile's separate PaperSize instance — an item that is
        // not in the ComboBox's ItemsSource would leave the dropdown blank.
        SelectedPaperSize ??= SelectedProfile?.Paper ?? PaperSize.Presets[0];

        // Restore a manually chosen table (e.g. Windows-1256 Arabic) after the
        // profile has applied its own default.
        if (!string.IsNullOrWhiteSpace(savedCodePage))
        {
            var savedTable = EncodingHelper.Tables.FirstOrDefault(t => t.Label == savedCodePage);
            if (savedTable.Label == savedCodePage)
                SelectedCodePage = savedTable.Label;
        }

        _server = new TcpPrinterServer(_parser, _settings.ListenOnLan);
        _server.StatusChanged += () => Dispatcher.UIThread.Post(UpdateServerStatus);
        _server.Log += msg => Dispatcher.UIThread.Post(() => AppendLog(msg));
        _server.JobCompleted += () => Dispatcher.UIThread.Post(() => JobCount++);

        // Coalesce preview redraws: large raster jobs arrive in many TCP
        // chunks and must not queue one full render per chunk on the UI thread.
        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            UpdatePreview();
        };
        _parser.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            _previewDebounce.Stop();
            _previewDebounce.Start();
        });
        _parser.DrawerKickEvent += () => Dispatcher.UIThread.Post(() =>
        {
            DrawerKickCount++;
            AppendLog("Drawer kick (ESC p) received.");
        });

        StartServerCommand = new AsyncRelayCommand(StartServerAsync);
        StopServerCommand = new RelayCommand(StopServer);
        OpenPrintSettingsCommand = new RelayCommand(() => PrintSettingsRequested?.Invoke(_printRenderOptions));
        ClearReceiptCommand = new RelayCommand(ClearReceipt);
        SendDemoCommand = new RelayCommand(SendDemo);
        SendArabicDemoCommand = new RelayCommand(SendArabicDemo);
        AddCustomSizeCommand = new RelayCommand(AddCustomSize);
        ZoomInCommand = new RelayCommand(() => SetZoom(ZoomLevel + 0.25));
        ZoomOutCommand = new RelayCommand(() => SetZoom(ZoomLevel - 0.25));
        ZoomResetCommand = new RelayCommand(() => SetZoom(1.0));
        PrintToSystemCommand = new RelayCommand(PrintToSystemPrinter);
        RefreshPrintersCommand = new RelayCommand(RefreshSystemPrinters);
        InstallVirtualPrinterCommand = new AsyncRelayCommand(InstallVirtualPrinterAsync);
        RemoveVirtualPrinterCommand = new AsyncRelayCommand(RemoveVirtualPrinterAsync);
        SelectEscPosPrinterCommand = new RelayCommand(SelectEscPosPrinter);

        RefreshSystemPrinters();
        RefreshVirtualPrinterState();
        UpdateArabicPrinterAdvice();

        StatusMessage = "Ready — type a printer name and click Install, then print to it from any app.";
        RefreshPrinterSettingsSummary();
        UpdatePreview();

        // Start the server automatically if the user asked for it — the
        // OnStartServerOnLaunchChanged handler would also start it, but only
        // if the flag changed; a no-op assignment would not, so start here.
        if (_settings.StartServerOnLaunch)
            _ = StartServerAsync();
    }

    // ───────────────────────── settings persistence ─────────────────────────

    private void PersistSettings() => AppSettings.Save(_settings);

    private void PersistPort()
    {
        _settings.Port = Port;
        PersistSettings();
    }

    partial void OnListenOnLanChanged(bool value)
    {
        _settings.ListenOnLan = value;
        PersistSettings();
        if (_server.IsRunning)
        {
            // the server reads the flag at start — hint the user to restart
            AppendLog("LAN option changed while running — press Stop/Start to apply.");
        }
    }

    partial void OnVirtualPrinterNameChanged(string value)
    {
        _settings.VirtualPrinterName = value ?? "";
        PersistSettings();
        RefreshVirtualPrinterState();
    }

    partial void OnSystemPrinterNameChanged(string value)
    {
        // persist on selection, not on every keystroke
        if (string.IsNullOrEmpty(value)) return;
        _settings.ForwardPrinterName = value;
        PersistSettings();
    }

    partial void OnSelectedProfileChanged(PrinterProfile? value)
    {
        if (value == null) return;
        _settings.SelectedProfile = value.DisplayName;
        PersistSettings();
        ApplyProfile(value);
    }

    partial void OnSelectedCodePageChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var table = EncodingHelper.Tables.FirstOrDefault(t => t.Label == value);
        if (table.Label != value) return;

        _parser.SetEncoding(table.Id);
        _settings.SelectedCodePage = value;
        PersistSettings();
    }

    partial void OnSelectedPaperSizeChanged(PaperSize? value)
    {
        if (value != null) UpdatePreview();
    }

    partial void OnStartServerOnLaunchChanged(bool value)
    {
        _settings.StartServerOnLaunch = value;
        PersistSettings();
        if (value && _server is { IsRunning: false })
            _ = StartServerAsync();
    }

    [ObservableProperty] private string _printerSettingsSummary = "";
    [ObservableProperty] private IRelayCommand _openPrintSettingsCommand;

    /// <summary>
    /// Raised when the user clicks "Printer Preferences…" in the PRINTER
    /// SETTINGS card. The live options instance is passed so the caller
    /// (MainWindow) can construct the modal dialog on top of it.
    /// </summary>
    public Action<RenderOptions>? PrintSettingsRequested;

    // ───────────────────────── printer settings summary ─────────────────────────

    /// <summary>Live settings (used by the preferences dialog to persist on OK).</summary>
    public AppSettings Settings => _settings;

    public void RefreshPrinterSettingsSummary()
    {
        var o = _printRenderOptions;
        var parts = new List<string>
        {
            $"page ×{(int)Math.Round(o.PageScale * 100)}%",
            o.LineSpacing == 0 ? "line: follow stream" : $"line: {o.LineSpacing}×18 pt",
        };
        if (o.Copies > 1) parts.Add($"{o.Copies} copies");
        if (o.Halftoning is not HalftoningMode.None) parts.Add("halftoning");
        if (!o.DrawBorders) parts.Add("no borders");
        if (o.MarginTop != 30) parts.Add($"top {o.MarginTop}");
        if (o.MarginLeft != 0) parts.Add($"left {o.MarginLeft}");
        PrinterSettingsSummary = string.Join("  •  ", parts);
    }

    /// <summary>Called after the preferences dialog closes (OK or Cancel): refresh summary + preview.</summary>
    public void OnPrinterSettingsChanged()
    {
        RefreshPrinterSettingsSummary();
        UpdatePreview();
    }

    // ───────────────────────── profile ─────────────────────────

    private void ApplyProfile(PrinterProfile profile)
    {
        try
        {
            _parser.SetProfile(profile);

            var match = PaperSizes.FirstOrDefault(p =>
                p != null && p.WidthMm == profile.Paper.WidthMm && p.Dpi == profile.Dpi);

            SelectedPaperSize = match ?? profile.Paper;
            if (SelectedPaperSize != null && !PaperSizes.Contains(SelectedPaperSize))
                PaperSizes.Add(SelectedPaperSize);

            SelectedCodePage = EncodingHelper.DescribeTable(profile.DefaultCodePage);
            ProfileNotes = profile.Notes ?? "";
            StatusMessage = $"Profile: {profile.DisplayName}  •  {SelectedPaperSize?.Name}  •  {EncodingHelper.DescribeTable(profile.DefaultCodePage)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Profile error: {ex.Message}";
        }
    }

    // ───────────────────────── preview / zoom ─────────────────────────

    private void UpdatePreview()
    {
        try
        {
            var paper = SelectedPaperSize ?? PaperSize.Presets[0];
            var doc = _parser.CurrentDocument ?? new ReceiptDocument();

            var old = _lastSkBitmap;
            _lastSkBitmap = _renderer.Render(doc, paper, scale: 2, _printRenderOptions);
            old?.Dispose();

            ApplyZoomToPreview();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Render error: {ex.Message}";
        }
    }

    private void SetZoom(double level)
    {
        ZoomLevel = Math.Clamp(level, 0.25, 3.0);
        ZoomLabel = $"{(int)(ZoomLevel * 100)}%";
        ApplyZoomToPreview();
    }

    private void ApplyZoomToPreview()
    {
        try
        {
            if (_lastSkBitmap == null)
            {
                UpdatePreview();
                return;
            }

            int w = Math.Max(1, (int)(_lastSkBitmap.Width * ZoomLevel));
            int h = Math.Max(1, (int)(_lastSkBitmap.Height * ZoomLevel));
            w = Math.Min(w, 6000);
            h = Math.Min(h, 30000);

            using var scaled = _lastSkBitmap.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
            if (scaled == null) return;

            using var data = scaled.Encode(SKEncodedImageFormat.Png, 90);
            if (data == null) return;

            using var stream = data.AsStream();
            var old = PreviewImage;
            PreviewImage = new Bitmap(stream);
            old?.Dispose();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Zoom error: {ex.Message}";
        }
    }

    // ───────────────────────── server ─────────────────────────

    private void UpdateServerStatus()
    {
        ServerStatus = _server.Status ?? "";
        IsServerRunning = _server.IsRunning;
        ServerBadgeText = IsServerRunning ? "LISTENING" : "STOPPED";
        ServerBadgeBrush = IsServerRunning ? BadgeRunning : BadgeStopped;
    }

    private async Task StartServerAsync()
    {
        if (Port is < 1 or > 65535)
        {
            StatusMessage = "Port must be between 1 and 65535.";
            return;
        }
        PersistPort();

        var ok = await _server.StartAsync(Port);
        if (ok)
            StatusMessage = $"Listening — {SelectedProfile?.DisplayName ?? "printer"} emulated on port {Port}.";
        else
            StatusMessage = $"Failed to start: {_server.LastError ?? "unknown error"}";
    }

    private void StopServer()
    {
        try
        {
            _server.Stop();
            StatusMessage = "Server stopped.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Stop error: {ex.Message}";
        }
    }

    // ───────────────────────── receipt actions ─────────────────────────

    private void ClearReceipt()
    {
        try
        {
            _parser.ResetAll();
            StatusMessage = "Receipt cleared.";
            UpdatePreview();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Clear error: {ex.Message}";
        }
    }

    private void SendDemo()
    {
        try
        {
            // The demo goes through the same path a spooled job takes:
            // NewJob() then a raw-byte Feed().
            _parser.NewJob();
            _parser.Feed(BuildDemoReceipt().AsSpan());
            StatusMessage = "Demo receipt sent (raw bytes through the parser).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Demo error: {ex.Message}";
        }
    }

    /// <summary>
    /// Arabic demo through the same raw-byte pipeline: ESC t 27 selects
    /// Windows-1256 and Arabic text arrives as single-byte CP1256 sequences,
    /// exactly like a real Arabic POS / thermal printer setup.
    /// </summary>
    private void SendArabicDemo()
    {
        try
        {
            _parser.NewJob();
            _parser.Feed(BuildArabicDemoReceipt().AsSpan());
            StatusMessage = "Arabic demo sent (Windows-1256 / ESC t 27).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Arabic demo error: {ex.Message}";
        }
    }

    private static byte[] BuildArabicDemoReceipt()
    {
        var cp1256 = EncodingHelper.Get(1256);
        var ms = new MemoryStream();
        void W(params byte[] b) => ms.Write(b);
        void Ascii(string s) => ms.Write(System.Text.Encoding.ASCII.GetBytes(s ?? ""));
        void Arabic(string s) => ms.Write(cp1256.GetBytes(s ?? ""));

        W(0x1B, 0x40);                        // ESC @ init
        W(0x1B, 0x74, 27);                    // ESC t 27 — Windows-1256 Arabic
        W(0x1B, 0x61, 0x01);                  // center
        W(0x1B, 0x45, 0x01);                  // bold
        Arabic("متجر الإلكترونيات\n");
        W(0x1B, 0x45, 0x00);                  // bold off
        Ascii("ELECTRONICS STORE\n");
        W(0x1B, 0x61, 0x00);                  // left
        Ascii("--------------------------------\n");
        Arabic("العميل: عبد الرحمن\n");
        Arabic("رقم الفاتورة: 2024/123\n");
        Arabic("التاريخ: 24/11/2026\n");
        Ascii("--------------------------------\n");
        Arabic("سماعات لاسلكية        3,200.00\n");
        Arabic("شاحن سريع              150.00\n");
        Ascii("--------------------------------\n");
        W(0x1B, 0x45, 0x01);                  // bold
        Arabic("الإجمالي: 3,350.00 ريال\n");
        W(0x1B, 0x45, 0x00);                  // bold off
        W(0x1B, 0x61, 0x01);                  // center
        Arabic("شكراً لزيارتكم!\n");
        W(0x1B, 0x61, 0x00);
        W(0x1D, 0x56, 0x00);                  // full cut
        return ms.ToArray();
    }

    private void AddCustomSize()
    {
        try
        {
            var custom = PaperSize.ForProfile(72, 203);
            if (!PaperSizes.Any(p => p.Name == custom.Name))
                PaperSizes.Add(custom);
            SelectedPaperSize = custom;
            UpdatePreview();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Custom size error: {ex.Message}";
        }
    }

    /// <summary>
    /// Demo receipt as RAW ESC/POS bytes — the exact stream a POS app would
    /// send (init → centered bold title → items → total → QR → cut).
    /// </summary>
    private byte[] BuildDemoReceipt()
    {
        var profile = SelectedProfile;
        string title = profile?.DisplayName ?? "ESC/POS Emulator";

        var ms = new MemoryStream();
        void W(params byte[] b) => ms.Write(b);
        void T(string s) => ms.Write(System.Text.Encoding.ASCII.GetBytes(s ?? ""));

        W(0x1B, 0x40);                       // ESC @  init
        W(0x1B, 0x61, 0x01);                 // center
        W(0x1B, 0x21, 0x08);                 // double width
        T("ESPRESSO LAB\n");
        W(0x1B, 0x21, 0x00);                 // normal width
        T($"{title}\n");
        T("123 Main Street\n");
        T("Tel: +1 555-0123\n");
        W(0x1B, 0x61, 0x00);                 // left
        T("--------------------------------\n");
        T("Espresso              $3.50\n");
        T("Cappuccino            $4.50\n");
        T("Croissant             $2.80\n");
        T("--------------------------------\n");
        W(0x1B, 0x45, 0x01);                 // bold on
        T("TOTAL                 $10.80\n");
        W(0x1B, 0x45, 0x00);                 // bold off
        T("\n");
        W(0x1B, 0x61, 0x01);                 // center
        T("Thank you!\n");

        // QR: GS ( k sub-commands, each frame carries its own header.
        // Frame layout: pn  cn  cn(repeat)  a  <cn bytes of data>
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("https://example.com/receipt/demo");
        if (profile?.SupportsQr != false)
        {
            W(0x1D, 0x28, 0x6B, 0x03, 0x01, 0x00, 0x41, 0x31);           // model 1
            W(0x1D, 0x28, 0x6B, 0x03, 0x01, 0x00, 0x43, 0x04);           // module size 4
            W(0x1D, 0x28, 0x6B, (byte)(payload.Length + 1),
                                  (byte)payload.Length, 0x00, 0x68);      // store data
            W(payload);
            W(0x1D, 0x28, 0x6B, 0x01, 0x00, 0x00, 0x69);                 // print stored
        }

        T("\nVisit us again\n");
        if (profile?.HasCutter != false)
            W(0x1D, 0x56, 0x00);               // GS V A — full cut
        return ms.ToArray();
    }

    // ───────────────────────── virtual (system) printer ─────────────────────────

    private void RefreshVirtualPrinterState()
    {
        if (!VirtualPrinterService.IsWindows)
        {
            IsVirtualPrinterInstalled = false;
            VirtualPrinterStatus = "Virtual system printers: Windows only";
            return;
        }

        RefreshVirtualPrinterDrivers();

        IsVirtualPrinterInstalled = !string.IsNullOrWhiteSpace(VirtualPrinterName) &&
                                    _virtualPrinter.Exists(VirtualPrinterName);

        VirtualPrinterStatus = string.IsNullOrWhiteSpace(VirtualPrinterName)
            ? "Type the name your POS app prints to (e.g. test)"
            : IsVirtualPrinterInstalled
                ? $"'{VirtualPrinterName}' is installed → {(ListenOnLan ? "0.0.0.0" : "127.0.0.1")}:{Port}"
                : $"'{VirtualPrinterName}' is not installed yet";
    }

    /// <summary>
    /// Fills the driver dropdown ("" = Generic / Text Only, then every
    /// printer driver Windows has, with real thermal drivers marked with ✓)
    /// and AUTO-SELECTS the best installed ESC/POS/thermal driver the first
    /// time (the choice is then persisted, so a later manual pick — including
    /// "Generic / Text Only" — always sticks).
    /// </summary>
    private void RefreshVirtualPrinterDrivers()
    {
        var installed = VirtualPrinterService.GetInstalledDrivers();
        var thermalNames = installed
            .Where(d => PrinterScanner.IsEscPos(d) && !PrinterScanner.IsTextDriver(d))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // null = not pinned yet (auto-pick); "" = explicitly "Generic / Text Only".
        string? pinned = _settings.VirtualPrinterDriver;
        if (pinned != null && pinned != "" && !installed.Contains(pinned))
            pinned = null;   // the pinned driver was uninstalled → back to auto

        string pick;
        if (pinned == null)
        {
            // First run (or the saved driver is gone): prefer the driver
            // attached to the best found ESC/POS printer, else first thermal.
            var byPrinter = PrinterScanner.FindBestEscPos()?.DriverName;
            pick = (byPrinter != null && installed.Contains(byPrinter))
                ? byPrinter
                : thermalNames.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "";
            _settings.VirtualPrinterDriver = pick;   // pin the auto choice
            PersistSettings();
        }
        else
        {
            pick = pinned;
        }

        VirtualPrinterDriverHint = pinned == null ? "auto-selected" : "remembered";

        var list = new List<string> { "" };
        foreach (var d in installed)
            list.Add(thermalNames.Contains(d) ? $"✓ {d}" : d);

        // display form (thermal entries shown with the ✓ prefix)
        string display = list.Contains($"✓ {pick}") ? $"✓ {pick}" : pick;

        VirtualPrinterDrivers.Clear();
        foreach (var d in list) VirtualPrinterDrivers.Add(d);
        VirtualPrinterDriver = display;
    }

    partial void OnVirtualPrinterDriverChanged(string value)
    {
        // store the raw driver name (the dropdown prefixes thermal entries with "✓ ")
        string raw = value ?? "";
        _settings.VirtualPrinterDriver = raw.StartsWith("✓ ") ? raw[2..] : raw;
        PersistSettings();
    }

    private async Task InstallVirtualPrinterAsync()
    {
        if (IsVirtualPrinterBusy) return;
        IsVirtualPrinterBusy = true;
        try
        {
            if (!VirtualPrinterService.IsWindows)
            {
                VirtualPrinterStatus = "Virtual system printers: Windows only";
                return;
            }

            var problem = VirtualPrinterService.ValidateName(VirtualPrinterName);
            if (problem != null)
            {
                VirtualPrinterStatus = problem;
                StatusMessage = problem;
                return;
            }

            if (Port is < 1 or > 65535)
            {
                VirtualPrinterStatus = "Pick a valid port (1–65535) first.";
                StatusMessage = VirtualPrinterStatus;
                return;
            }
            PersistPort();

            VirtualPrinterStatus = "Waiting for administrator permission…";
            string name = VirtualPrinterName.Trim();
            // raw driver name without the "✓ " display prefix ("" = Generic / Text Only)
            string drv = VirtualPrinterDriver ?? "";
            string driver = drv.StartsWith("✓ ") ? drv[2..] : drv;

            (bool Ok, string Message) result =
                _virtualPrinter.Exists(name)
                    // already our printer but port/name may differ → rename path:
                    // remove + install happen inside ONE elevated (UAC) call
                    ? await _virtualPrinter.RenameAsync(name, name, Port, driver)
                    : await _virtualPrinter.InstallAsync(name, Port, driver);

            if (result.Ok && _settings.AutoStartOnInstall && !_server.IsRunning)
                await _server.StartAsync(Port);     // a printer nobody listens to is useless

            _settings.VirtualPrinterName = name;
            PersistSettings();
            RefreshVirtualPrinterState();
            VirtualPrinterStatus = result.Message;
            StatusMessage = result.Message;
            RefreshSystemPrinters();
        }
        catch (Exception ex)
        {
            VirtualPrinterStatus = $"Install error: {ex.Message}";
            StatusMessage = VirtualPrinterStatus;
        }
        finally
        {
            IsVirtualPrinterBusy = false;
        }
    }

    private async Task RemoveVirtualPrinterAsync()
    {
        if (IsVirtualPrinterBusy) return;
        IsVirtualPrinterBusy = true;
        try
        {
            if (!VirtualPrinterService.IsWindows || string.IsNullOrWhiteSpace(VirtualPrinterName))
            {
                RefreshVirtualPrinterState();
                return;
            }

            VirtualPrinterStatus = "Waiting for administrator permission…";
            var (ok, msg) = await _virtualPrinter.RemoveAsync(VirtualPrinterName, Port);

            if (ok) _settings.VirtualPrinterName = "";
            PersistSettings();
            RefreshVirtualPrinterState();
            VirtualPrinterStatus = msg;
            StatusMessage = msg;
            RefreshSystemPrinters();
        }
        catch (Exception ex)
        {
            VirtualPrinterStatus = $"Remove error: {ex.Message}";
            StatusMessage = VirtualPrinterStatus;
        }
        finally
        {
            IsVirtualPrinterBusy = false;
        }
    }

    // ───────────────────────── system printers ─────────────────────────

    private void RefreshSystemPrinters()
    {
        try
        {
            SystemPrinters.Clear();
            if (!_printService.IsWindows)
            {
                PrintStatus = "System printers: Windows only";
                return;
            }

            foreach (var p in _printService.GetInstalledPrinters())
                SystemPrinters.Add(p);

            // keep the configured name visible even if it is gone now
            if (!string.IsNullOrWhiteSpace(SystemPrinterName) &&
                !SystemPrinters.Contains(SystemPrinterName))
            {
                SystemPrinters.Insert(0, SystemPrinterName);
            }

            PrintStatus = SystemPrinters.Count > 0
                ? $"{SystemPrinters.Count} printer(s) found"
                : "No printers found";
        }
        catch (Exception ex)
        {
            PrintStatus = $"Printer list error: {ex.Message}";
        }

        UpdateArabicPrinterAdvice();
    }

    // ───────────────────────── Arabic / ESC-POS auto-detect ─────────────────────────

    /// <summary>
    /// Re-evaluates the Arabic-printer situation: finds the best installed
    /// real ESC/POS/thermal driver and decides what advice to show. Safe to
    /// call on the UI thread — the scan touches the spooler registry only.
    /// </summary>
    private void UpdateArabicPrinterAdvice()
    {
        if (!PrinterScanner.IsWindows)
        {
            HasEscPosPrinter = false;
            EscPosPrinterName = "";
            ArabicAdvice = "";
            return;
        }

        var printers = PrinterScanner.Scan();
        EscPosPrinterName = PrinterScanner.FindBestEscPos(printers)?.Name ?? "";
        HasEscPosPrinter = !string.IsNullOrEmpty(EscPosPrinterName);

        bool targetingTextDriver =
            !string.IsNullOrWhiteSpace(SystemPrinterName) &&
            SystemPrinters.Contains(SystemPrinterName) &&
            printers.FirstOrDefault(p => string.Equals(p.Name, SystemPrinterName,
                                                        StringComparison.OrdinalIgnoreCase))?.Kind
                        is PrinterKind.TextDriver;

        if (targetingTextDriver && HasEscPosPrinter)
        {
            ArabicAdvice =
                "“Generic / Text Only” mangles Arabic into dots on the way in. " +
                $"A real thermal printer is installed ({EscPosPrinterName}) — switch to it.";
        }
        else if (targetingTextDriver && !HasEscPosPrinter)
        {
            ArabicAdvice =
                "“Generic / Text Only” turns Arabic into dots. No thermal driver " +
                "installed — install the real driver for your physical printer.";
        }
        else if (HasEscPosPrinter)
        {
            ArabicAdvice = $"Thermal printer installed: {EscPosPrinterName} (Arabic-safe).";
        }
        else
        {
            ArabicAdvice = "No thermal (ESC/POS) driver installed on this machine.";
        }
    }

    /// <summary>
    /// One-click: point the "Print Receipt" (forward) target at the detected
    /// real thermal driver, so receipts are emitted as true ESC/POS bytes with
    /// real CP1256 Arabic instead of dots.
    ///
    /// Note: the *virtual* printer (top card) must KEEP the Generic/Text-Only →
    /// TCP/IP design — that is what lets an external POS app pipe its stream
    /// into this emulator. Only the forward target changes.
    /// </summary>
    private void SelectEscPosPrinter()
    {
        if (!HasEscPosPrinter)
        {
            StatusMessage = "No installed ESC/POS / thermal printer to select.";
            return;
        }

        SystemPrinterName = EscPosPrinterName;   // forward target → real driver
        UpdateArabicPrinterAdvice();
        StatusMessage = $"Print target set to '{EscPosPrinterName}' (real thermal driver — Arabic-safe).";
        AppendLog($"Auto-selected installed ESC/POS printer: {EscPosPrinterName}");
    }

    private void PrintToSystemPrinter()
    {
        try
        {
            string name = SystemPrinterName ?? "";
            var (ok, message) = _printService.Print(
                _parser.CurrentDocument,
                SelectedPaperSize ?? PaperSize.Presets[0],
                name,
                copies: Math.Max(1, _printRenderOptions.Copies),
                halftoning: _printRenderOptions.Halftoning,
                pageScale: _printRenderOptions.PageScale);

            if (ok)
            {
                _settings.ForwardPrinterName = name;
                PersistSettings();
            }

            PrintStatus = message;
            StatusMessage = message;
        }
        catch (Exception ex)
        {
            PrintStatus = $"Print error: {ex.Message}";
            StatusMessage = PrintStatus;
        }
    }

    // ───────────────────────── log ─────────────────────────

    private void AppendLog(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        LogText += message + Environment.NewLine;
        if (LogText.Length > 8000)
            LogText = LogText[^6000..];
    }

    public void Dispose()
    {
        try
        {
            _server.Dispose();
            PreviewImage?.Dispose();
            _lastSkBitmap?.Dispose();
        }
        catch
        {
            // ignore dispose errors
        }
    }
}
