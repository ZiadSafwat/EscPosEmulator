using System.Text.Json;
using System.Text.Json.Serialization;

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
            return settings;
        }
        catch
        {
            return new AppSettings();
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
