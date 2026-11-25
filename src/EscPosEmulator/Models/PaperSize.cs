namespace EscPosEmulator.Models;

/// <summary>
/// Thermal paper size. Width in mm and dots at the given DPI.
/// Most modern thermal printers are 203 dpi; older Epson T88 series are 180 dpi.
/// </summary>
public sealed class PaperSize
{
    public string Name { get; }
    public int WidthMm { get; }
    public int WidthDots { get; }
    public int PrintableWidthDots { get; }
    public int Dpi { get; }

    public PaperSize(string name, int widthMm, int widthDots, int printableWidthDots, int dpi = 203)
    {
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        Name = string.IsNullOrWhiteSpace(name) ? $"{widthMm} mm" : name;
        WidthMm = widthMm;
        WidthDots = Math.Max(1, widthDots);
        PrintableWidthDots = Math.Max(1, printableWidthDots);
        Dpi = dpi;
    }

    /// <summary>Presets offered in the UI (203 dpi unless noted).</summary>
    public static IReadOnlyList<PaperSize> Presets { get; } =
    [
        new("58 mm",  58,  406, 384),
        new("76 mm",  76,  607, 512),
        new("80 mm",  80,  639, 512),
        new("80 mm (180 dpi)", 80, 576, 512, 180),
        new("112 mm", 112, 895, 832),
    ];

    /// <summary>Builds a paper size matching a hardware profile's width + DPI.</summary>
    public static PaperSize ForProfile(int widthMm, int dpi, int? printableDots = null)
    {
        int dots = (int)Math.Round(widthMm / 25.4 * dpi);
        int printable = printableDots ?? (int)Math.Round(dots * 0.90);
        string name = dpi == 203 ? $"{widthMm} mm" : $"{widthMm} mm ({dpi} dpi)";
        return new(name, widthMm, dots, printable, dpi);
    }

    public int CharactersPerLine => Math.Max(1, PrintableWidthDots / 12);

    public override string ToString() => Name;
}
