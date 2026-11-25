using System.Text.Json;
using System.Text.Json.Serialization;
using EscPosEmulator.Models;

namespace EscPosEmulator.Services;

/// <summary>
/// Persistent application settings. Stored as JSON in
/// %LOCALAPPDATA%\EscPosEmulator\settings.json — writable in every
/// install scenario (no read-only exe directory issues).
/// </summary>
public sealed class AppSettings
{
    public int Port { get; set; } = 9100;

    /// <summary>
    /// When true the TCP listener binds 0.0.0.0 (reachable from the LAN).
    /// Default false: bind 127.0.0.1 only — enough for the local virtual printer
    /// and it avoids the Windows Firewall prompt.
    /// </summary>
    public bool ListenOnLan { get; set; }

    /// <summary>Automatically start the listener when the virtual printer is installed.</summary>
    public bool AutoStartOnInstall { get; set; } = true;

    /// <summary>Start the TCP listener automatically when the app launches.</summary>
    public bool StartServerOnLaunch { get; set; }

    /// <summary>User-adjustable preview rendering options (printer settings).</summary>
    public RenderOptions PrinterSettings { get; set; } = new();

    /// <summary>Display name of the selected hardware profile ("" = first catalog entry).</summary>
    public string SelectedProfile { get; set; } = "Epson TM-T88V / TM-T88VI";

    /// <summary>
    /// Code-page label last chosen in the UI ("" = profile default).
    /// Lets an Arabic / UTF-8 selection survive a restart.
    /// </summary>
    public string SelectedCodePage { get; set; } = "";

    /// <summary>Virtual printer name remembered between runs.</summary>
    public string VirtualPrinterName { get; set; } = "";

    /// <summary>
    /// Printer driver used when the virtual printer is (re)installed.
    /// "" = always-present inbox "Generic / Text Only". A real thermal driver
    /// (Xprinter / Epson / STAR …) can be selected, so a POS app that prints
    /// Arabic keeps raw CP1256 bytes instead of dot-mangled ASCII.
    /// </summary>
    public string? VirtualPrinterDriver { get; set; }

    /// <summary>
    /// System printer the preview is forwarded to via "Print Receipt"
    /// ("" = none). E.g. an installed thermal printer or "Microsoft Print to PDF".
    /// </summary>
    public string ForwardPrinterName { get; set; } = "";

    // ───────────────────────────── Storage ─────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EscPosEmulator");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static void EnsureDirectory()
    {
        try { Directory.CreateDirectory(SettingsDirectory); }
        catch { /* non-fatal: defaults are used */ }
    }

    public static AppSettings Load()
    {
        EnsureDirectory();
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            string json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings == null) return new AppSettings();
            if (settings.Port < 1 || settings.Port > 65535) settings.Port = 9100;
            settings.PrinterSettings ??= new RenderOptions();
            MigrateLegacyPrinterSettings(json, settings);
            ClampPrinterSettings(settings.PrinterSettings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static void ClampPrinterSettings(RenderOptions o)
    {
        if (o.PageScale is < 0.5 or > 4.0) o.PageScale = 1.0;
        o.Copies = Math.Clamp(o.Copies, 1, 99);
        o.LineSpacing = Math.Clamp(o.LineSpacing, 0, 99);
        o.MarginTop = Math.Clamp(o.MarginTop, 0, 120);
        o.MarginLeft = Math.Clamp(o.MarginLeft, 0, 60);
    }

    /// <summary>Migrate settings.json files saved by an older build.</summary>
    private static void MigrateLegacyPrinterSettings(string json, AppSettings settings)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("PrinterSettings", out var ps)) return;
            // older builds stored text zoom as "TextScale" — carry it over
            if (ps.TryGetProperty("TextScale", out var legacy) &&
                legacy.ValueKind == JsonValueKind.Number)
            {
                double ts = legacy.GetDouble();
                if (ts is >= 0.5 and <= 4.0) settings.PrinterSettings.PageScale = ts;
            }
        }
        catch
        {
            // leave the defaults in place
        }
    }

    /// <summary>Save is best-effort: a failed write never crashes the app.</summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            EnsureDirectory();
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // ignore (e.g. read-only profile) — defaults remain in memory
        }
    }
}
