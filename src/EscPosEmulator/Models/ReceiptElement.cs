namespace EscPosEmulator.Models;

public abstract class ReceiptElement
{
    /// <summary>Height this element occupies, in printer dots.</summary>
    public abstract int HeightDots { get; }
}

/// <summary>A line of text with the format it was printed in.</summary>
public sealed class TextLineElement : ReceiptElement
{
    public string Text { get; }
    public PrinterState Style { get; }
    public override int HeightDots => Style.LineHeightDots;

    public TextLineElement(string text, PrinterState style)
    {
        Text = text ?? "";
        Style = style?.Clone() ?? new PrinterState();
    }
}

/// <summary>1-bit monochrome raster image (row-major, MSB-first per row).</summary>
public sealed class ImageElement : ReceiptElement
{
    public byte[] BitmapData { get; }
    public int WidthDots { get; }
    public int HeightDotsValue { get; }
    public override int HeightDots => HeightDotsValue;

    public ImageElement(byte[] data, int width, int height)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        BitmapData = data;
        WidthDots = width;
        HeightDotsValue = height;
    }
}

/// <summary>QR code. The content is encoded at render time; height is estimated here.</summary>
public sealed class QrCodeElement : ReceiptElement
{
    public string Content { get; }
    public int ModuleSize { get; }

    /// <summary>Estimated size in dots (QR codes from GS ( k n 65/67 are square: this x this).</summary>
    public int RenderedDots { get; }

    public override int HeightDots => RenderedDots > 0 ? RenderedDots : 96;

    public QrCodeElement(string content, int moduleSize, int renderedDots)
    {
        Content = content ?? "";
        ModuleSize = Math.Clamp(moduleSize, 1, 16);
        RenderedDots = renderedDots;
    }
}

/// <summary>Linear barcode. Type examples: CODE128, EAN13, EAN8, UPCA, CODE39.</summary>
public sealed class BarcodeElement : ReceiptElement
{
    public string Data { get; }
    public string Type { get; }
    public int Height { get; }
    public override int HeightDots => Height + 24; // barcode + human readable text

    public BarcodeElement(string data, string type, int height)
    {
        Data = data ?? "";
        Type = string.IsNullOrWhiteSpace(type) ? "CODE128" : type.ToUpperInvariant();
        Height = Math.Clamp(height, 10, 576);
    }
}

/// <summary>Paper cut (full or partial) and feed markers.</summary>
public sealed class CutElement : ReceiptElement
{
    public bool Partial { get; }
    public override int HeightDots => 40;

    public CutElement(bool partial) => Partial = partial;
}

/// <summary>Plain feed without printing (ESC d / ESC J / GS V n).</summary>
public sealed class FeedElement : ReceiptElement
{
    public int Lines { get; }
    public int Dots { get; }
    public override int HeightDots => Dots > 0 ? Dots : Lines * 24;

    public FeedElement(int lines, int dots = 0)
    {
        Lines = Math.Max(0, lines);
        Dots = Math.Max(0, dots);
    }
}
