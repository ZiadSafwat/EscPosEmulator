using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EscPosEmulator.Models;
using EscPosEmulator.Rendering;
using SkiaSharp;

namespace EscPosEmulator.Services;

/// <summary>
/// Sends the rendered receipt to a real Windows system printer
/// ("Print Receipt" preview → "Microsoft Print to PDF", a physical
/// thermal printer, …). Everything is best-effort: no throw out of
/// the public API.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrintService
{
    private readonly ReceiptRenderer _renderer = new();

    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public IReadOnlyList<string> GetInstalledPrinters()
    {
        if (!IsWindows) return Array.Empty<string>();
        try
        {
            return System.Drawing.Printing.PrinterSettings.InstalledPrinters
                .Cast<string>()
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public bool PrinterExists(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !IsWindows) return false;
        try
        {
            return System.Drawing.Printing.PrinterSettings.InstalledPrinters
                .Cast<string>()
                .Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prints the receipt document to the named Windows printer.
    /// Returns (success, human-readable message).
    /// </summary>
    public (bool Ok, string Message) Print(ReceiptDocument? doc, PaperSize? paper, string? printerName,
            int copies = 1, HalftoningMode halftoning = HalftoningMode.None, double pageScale = 1.0)
    {
        if (doc == null)
            return (false, "No receipt to print yet.");
        if (doc.Elements.Count == 0)
            return (false, "The receipt is empty — nothing to print.");
        if (string.IsNullOrWhiteSpace(printerName))
            return (false, "No target printer selected.");
        if (!IsWindows)
            return (false, "System printer output is only supported on Windows.");

        paper ??= PaperSize.Presets.FirstOrDefault(p => p.WidthMm == 80) ?? PaperSize.Presets[0];
        copies = Math.Clamp(copies, 1, 99);
        pageScale = Math.Clamp(pageScale, 0.5, 4.0);

        var renderOptions = new RenderOptions
        {
            Halftoning = halftoning,
            PageScale = pageScale,
            DrawBorders = false,     // printed paper has no decorative border
            MarginTop = 1,
        };

        try
        {
            if (!PrinterExists(printerName))
            {
                var available = string.Join(", ", GetInstalledPrinters().Take(8));
                return (false, $"Printer '{printerName}' not found. Installed: {available}");
            }

            // Render at scale 1 for print fidelity.
            using var skBitmap = _renderer.Render(doc, paper, scale: 1, renderOptions);
            if (skBitmap == null)
                return (false, "Render returned null.");

            using var data = skBitmap.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null)
                return (false, "Failed to encode receipt image.");

            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;

            using var image = System.Drawing.Image.FromStream(ms);
            using var pd = new System.Drawing.Printing.PrintDocument();
            pd.PrinterSettings.PrinterName = printerName;
            pd.PrinterSettings.Copies = (short)copies;    // driver-level "Copies"
            pd.DefaultPageSettings.Margins = new System.Drawing.Printing.Margins(20, 20, 20, 20);

            // Narrow paper width matching the thermal profile.
            try
            {
                float widthHundredths = paper.WidthMm / 25.4f * 100f;
                pd.DefaultPageSettings.PaperSize = new System.Drawing.Printing.PaperSize(
                    "Receipt",
                    (int)widthHundredths,
                    Math.Max(300, image.Height + 40));
            }
            catch
            {
                // keep the default paper — the draw below still fits the image
            }

            pd.PrintPage += (_, e) =>
            {
                if (e.Graphics == null) return;
                float maxW = e.MarginBounds.Width;
                float scale = maxW > 0 ? maxW / image.Width : 1f;
                float w = image.Width * scale;
                float h = image.Height * scale;
                e.Graphics.DrawImage(image, e.MarginBounds.Left, e.MarginBounds.Top, w, h);
                e.HasMorePages = false;
            };

            pd.Print();
            return (true, copies > 1
                ? $"Sent to printer '{printerName}' ({copies} copies)."
                : $"Sent to printer '{printerName}'.");
        }
        catch (Exception ex)
        {
            return (false, $"Print failed: {ex.Message}");
        }
    }
}
