using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EscPosEmulator.Services;

/// <summary>One installed Windows printer, enriched with its driver name.</summary>
public sealed record PrinterInfo(string Name, string DriverName, string PortName)
{
    /// <summary>True for real ESC/POS / thermal printer drivers (they emit raw bytes).</summary>
    public bool ArabicSafe => Kind == PrinterKind.EscPos;

    /// <summary>True for Windows *Generic / Text Only* style text drivers that
    /// replace non-representable characters with literal dots (0x2E) before
    /// the bytes reach the port — i.e. the Arabic-shows-as-dots culprit.</summary>
    public bool DotRisk => Kind == PrinterKind.TextDriver;

    public PrinterKind Kind =>
        PrinterScanner.IsTextDriver(DriverName) ? PrinterKind.TextDriver
        : PrinterScanner.IsEscPos(DriverName)   ? PrinterKind.EscPos
                                                 : PrinterKind.Unknown;
}

public enum PrinterKind
{
    Unknown,     // can't tell from the driver name (PDF, MUX, …) — not a dot risk
    TextDriver,  // Generic / Text Only & friends — mangle high-byte text into dots
    EscPos       // real thermal / POS driver — safe for raw ESC/POS + Arabic
}

/// <summary>
/// Scans the Windows spooler for installed printers and classifies each one by
/// driver name. Used to (a) automatically find a real ESC/POS / thermal driver
/// on the machine and (b) warn when the user targets a text driver that turns
/// Arabic into dots.
/// </summary>
public static class PrinterScanner
{
    /// <summary>Driver-name substrings that mark a real ESC/POS / thermal printer.</summary>
    public static readonly string[] EscPosDrivers =
    {
        "esc/pos", "escpos", "esc/p", "esc-p", "receipt", "thermal",
        "pos printer", "pos", "dot printer",
        "xprinter", "epson", "starmicronics", "star", "bixolon", "citizen",
        "honeywell", "phymex", "impress", "gprinter", "ruiying", "gprinter"
    };

    /// <summary>Driver-name substrings that mark a Windows text-only driver (dot risk).</summary>
    public static readonly string[] TextDriverMarkers =
    {
        "text only", "generic/text", "generic / text", "txt only"
    };

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>All installed printers (name + driver + port), best-effort.</summary>
    public static IReadOnlyList<PrinterInfo> Scan()
    {
        if (!IsWindows) return Array.Empty<PrinterInfo>();

        var result = new List<PrinterInfo>();
        try
        {
            string[] names =
                System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().ToArray();

            foreach (string name in names)
            {
                string driver = "", port = "";
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{name}");
                    // The spooler records the driver under the value
                    // "Printer Driver" (not "DriverName"); keep a fallback for
                    // safety across Windows versions.
                    driver = (key?.GetValue("Printer Driver")  as string)
                           ?? (key?.GetValue("DriverName")     as string) ?? "";
                    port = (key?.GetValue("PortName") as string) ?? "";
                }
                catch
                {
                    // no driver info — fall back to name-only
                }

                result.Add(new PrinterInfo(name, driver, port));
            }
        }
        catch
        {
            return Array.Empty<PrinterInfo>();
        }

        return result;
    }

    /// <summary>
    /// The best installed printer for raw Arabic ESC/POS output: a printer whose
    /// driver name looks thermal / ESC/POS. Excludes emulated (port-prefixed)
    /// printer ports created by this tool.
    /// </summary>
    public static PrinterInfo? FindBestEscPos(IReadOnlyList<PrinterInfo>? printers = null)
    {
        printers ??= Scan();
        return printers
            .Where(p => p.Kind == PrinterKind.EscPos)
            .Where(p => !IsEmulatedPort(p.PortName))
            .OrderByDescending(p => p.Name.Length)   // brand+model names first
            .FirstOrDefault();
    }

    /// <summary>True for the TCP/IP port this tool creates (EscPosEmu_*).</summary>
    public static bool IsEmulatedPort(string? port) =>
        !string.IsNullOrWhiteSpace(port) &&
        port.StartsWith("EscPosEmu_", StringComparison.OrdinalIgnoreCase);

    public static bool IsEscPos(string driver)
    {
        if (string.IsNullOrWhiteSpace(driver)) return false;
        string d = driver.ToLowerInvariant();
        return EscPosDrivers.Any(k => d.Contains(k, StringComparison.Ordinal));
    }

    public static bool IsTextDriver(string driver)
    {
        if (string.IsNullOrWhiteSpace(driver)) return false;
        string d = driver.ToLowerInvariant();
        return TextDriverMarkers.Any(k => d.Contains(k, StringComparison.Ordinal));
    }

    /// <summary>
    /// Inspect a captured raw job and decide whether it was mangled by a
    /// Windows *Generic / Text Only* driver: the hallmark is that every
    /// non-representable character (Arabic / high-byte) arrived as a literal
    /// dot (0x2E) and there is effectively no ESC/POS control structure left.
    /// Returns (detection, one-line human explanation, count of replacement dots).
    /// </summary>
    public static (bool Mangled, string Explanation, int Dots) DetectDotMangling(byte[] data)
    {
        if (data.Length < 8) return (false, "", 0);

        int dots = 0;
        int esc = 0;      // 0x1B
        int high = 0;     // raw high bytes (CP1256 Arabic / UTF-8, etc.)
        int letters = 0;  // printable ASCII letters/digits

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0x2E) dots++;
            else if (b == 0x1B) esc++;
            else if (b >= 0x80) high++;
            else if (b is >= 0x20 and <= 0x7E) letters++;
        }

        // Heuristic: dots are dense AND there is no high-byte Arabic data AND
        // almost no ESC/POS structure AND not much plain text.
        bool denseDots = dots >= 12 && dots >= data.Length / 16;
        bool noEscPos = esc < 2 && high == 0;

        if (denseDots && noEscPos && letters < data.Length * 0.6)
        {
            return (true,
                $"{dots} replacement dots found — this job was mangled by a " +
                "Windows text driver (Generic / Text Only) before it reached us. " +
                "Arabic was replaced with '.' in flight; nothing to render.",
                dots);
        }

        return (false, "", dots);
    }
}
