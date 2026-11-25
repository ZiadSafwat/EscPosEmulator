namespace EscPosEmulator.Models;

/// <summary>
/// Advanced printer document settings (mirrors the Windows printer
/// preferences dialog of a physical thermal printer). These control how
/// the emulated paper is rendered — they are NOT part of the ESC/POS
/// protocol itself.
/// </summary>
public sealed class RenderOptions
{
    // ── Paper/Output ─────────────────────────────────────────────

    /// <summary>Number of copies to print per receipt.</summary>
    public int Copies { get; set; } = 1;

    /// <summary>Ordered-dither halftoning for raster images ("Advanced Printing Features").</summary>
    public HalftoningMode Halftoning { get; set; } = HalftoningMode.None;

    // ── Layout ───────────────────────────────────────────────────

    /// <summary>Page orientation: false = portrait, true = landscape.</summary>
    public bool Landscape { get; set; }

    /// <summary>Front to back = true, right then down = false.</summary>
    public bool PrintFrontToBack { get; set; } = true;

    /// <summary>Draw the paper card with a border (like the preview paper look).</summary>
    public bool DrawBorders { get; set; } = true;

    // ── Document appearance (preview only) ───────────────────────

    /// <summary>Text scale of the whole page, 1.0 = printer-native size. 0.5 – 4.0.</summary>
    public double PageScale { get; set; } = 1.0;

    /// <summary>
    /// Fixed line height in 18-dot units (0 = follow the printer stream's
    /// own line spacing, 1 – 12 = fixed line height).
    /// </summary>
    public int LineSpacing { get; set; }

    /// <summary>Preview canvas top margin, in dots.</summary>
    public int MarginTop { get; set; } = 30;

    /// <summary>Preview canvas extra left margin, in dots (beyond the paper edge).</summary>
    public int MarginLeft { get; set; }
}

/// <summary>Halftoning (dithering) mode used when rasterizing 1-bit images.</summary>
public enum HalftoningMode
{
    None,
    /// <summary>Ordered 2×2 halftone dots — the classic "Halftoning" look.</summary>
    Dots,
    /// <summary>Error-diffusing dithering — smoother gradients.</summary>
    Smooth,
}

/// <summary>One Property / Value row of the "Advanced Document Settings" dialog.</summary>
public readonly record struct PropertyValue(string Name, string Value);
