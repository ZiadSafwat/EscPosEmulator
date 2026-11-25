using System.Runtime.InteropServices;
using EscPosEmulator.Models;
using QRCoder;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using ZXing;
using ZXing.SkiaSharp;

namespace EscPosEmulator.Rendering;

/// <summary>
/// Renders a <see cref="ReceiptDocument"/> to a SkiaSharp bitmap.
/// The bitmap height is computed exactly from the elements first (2-pass),
/// so long receipts are never clipped at the bottom.
/// </summary>
public sealed class ReceiptRenderer
{
    private const int Margin = 20;   // paper margin inside the card
    private const int Pad = 40;      // card padding around the paper
    private readonly SKTypeface _typeface;
    private readonly SKTypeface _typefaceBold;
    private readonly SKTypeface? _typefaceRtl;
    private readonly SKTypeface? _typefaceRtlBold;

    // Fonts that ship with Arabic (and Hebrew) glyphs. Monospace fonts such as
    // Consolas/Courier New have no Arabic coverage at all, which is why Arabic
    // receipts used to render as blank boxes / dots.
    private static readonly string[] RtlFontFamilies =
    [
        "Segoe UI",
        "Tahoma",
        "Arial",
        "Noto Naskh Arabic",
        "Noto Sans Arabic",
        "Dubai",
        "Sakkal Majalla",
        "Traditional Arabic",
        "DejaVu Sans",
        "FreeSans",
    ];

    public ReceiptRenderer()
    {
        _typeface = ResolveTypeface(bold: false);
        _typefaceBold = ResolveTypeface(bold: true);
        _typefaceRtl = ResolveRtlTypeface(bold: false);
        _typefaceRtlBold = ResolveRtlTypeface(bold: true) ?? _typefaceRtl;
    }

    private static SKTypeface ResolveTypeface(bool bold)
    {
        var weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        try
        {
            return SKTypeface.FromFamilyName("Consolas", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                   ?? SKTypeface.FromFamilyName("Courier New", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                   ?? SKTypeface.FromFamilyName("DejaVu Sans Mono", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                   ?? SKTypeface.FromFamilyName("monospace", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                   ?? SKTypeface.Default
                   ?? SKTypeface.CreateDefault();
        }
        catch
        {
            return SKTypeface.CreateDefault();
        }
    }

    private static SKTypeface? ResolveRtlTypeface(bool bold)
    {
        var weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        foreach (string family in RtlFontFamilies)
        {
            try
            {
                var typeface = SKTypeface.FromFamilyName(
                    family, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
                if (typeface != null && HasArabicGlyphs(typeface))
                    return typeface;
            }
            catch
            {
                // try the next family
            }
        }

        try
        {
            var match = SKFontManager.Default.MatchCharacter(
                null, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright,
                ["ar", "fa", "ur"], 'ا');
            if (match != null) return match;
        }
        catch
        {
            // fall through
        }

        try
        {
            return SKFontManager.Default.MatchCharacter('ا');
        }
        catch
        {
            return null;
        }
    }

    private static bool HasArabicGlyphs(SKTypeface typeface)
    {
        try
        {
            using var font = new SKFont(typeface);
            // ا ل م — a font without these cannot render Arabic words.
            return font.ContainsGlyph(0x0627) && font.ContainsGlyph(0x0644) && font.ContainsGlyph(0x0645);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Renders the document. Never throws — returns a placeholder bitmap on failure.</summary>
    public SKBitmap Render(ReceiptDocument? doc, PaperSize? paper, int scale = 2)
    {
        try
        {
            paper ??= new PaperSize("80 mm", 80, 639, 512);
            doc ??= new ReceiptDocument();
            scale = Math.Clamp(scale, 1, 8);

            var elements = doc.Elements;
            int printable = Math.Max(1, paper.PrintableWidthDots);

            // ── pass 1: exact content height ─────────────────────────
            float contentH = 0;
            foreach (var el in elements)
                if (el != null)
                    contentH += Measure(el, printable, scale);

            int contentPx = (int)Math.Ceiling(contentH) + 30;          // top + bottom breathing room
            int width = Math.Clamp(printable * scale, 100, 4000);
            int height = Math.Clamp(contentPx + Pad * 2 - Margin, 200, 20000);
            int bmpW = width + Pad * 2;
            int bmpH = height + Pad * 2 - 20;

            var bitmap = new SKBitmap(bmpW, bmpH);
            if (bitmap.Handle == IntPtr.Zero)
                return CreateErrorBitmap("Failed to allocate bitmap");

            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            using var paperPaint = new SKPaint { Color = new SKColor(250, 250, 245), IsAntialias = true };
            canvas.DrawRoundRect(Pad / 2f, Pad / 2f, Pad / 2f + width + Pad, Pad / 2f + height, 6, 6, paperPaint);

            using var borderPaint = new SKPaint
            {
                Color = new SKColor(180, 180, 180),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };
            canvas.DrawRoundRect(Pad / 2f, Pad / 2f, Pad / 2f + width + Pad, Pad / 2f + height, 6, 6, borderPaint);

            // ── pass 2: draw ──────────────────────────────────────────
            float y = Pad / 2f + 30f;
            foreach (var el in elements)
            {
                if (el == null) continue;
                try
                {
                    switch (el)
                    {
                        case TextLineElement t: y = DrawText(canvas, t, y, printable, scale); break;
                        case QrCodeElement q:   y = DrawQr(canvas, q, y, printable, scale); break;
                        case BarcodeElement b:  y = DrawBarcode(canvas, b, y, printable, scale); break;
                        case ImageElement i:    y = DrawImage(canvas, i, y, printable, scale); break;
                        case CutElement c:      y = DrawCut(canvas, c, y, printable, scale); break;
                        case FeedElement f:     y += Math.Max(0, f.HeightDots) * scale; break;
                    }
                }
                catch
                {
                    y += 20; // skip broken element, keep rendering
                }
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            return CreateErrorBitmap($"Render error: {ex.Message}");
        }
    }

    private static SKBitmap CreateErrorBitmap(string message)
    {
        var bmp = new SKBitmap(400, 120);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(255, 240, 240));
        using var paint = new SKPaint
        {
            Color = SKColors.DarkRed,
            TextSize = 14,
            IsAntialias = true,
            Typeface = SKTypeface.Default ?? SKTypeface.CreateDefault()
        };
        canvas.DrawText(message ?? "Error", 20, 60, paint);
        return bmp;
    }

    // Exact pixel advance for one element (used by pass 1 *and* mirroring pass 2).
    private float Measure(ReceiptElement el, int printable, int scale) => el switch
    {
        TextLineElement t => t.HeightDots * scale + 4,
        QrCodeElement q => Math.Min(Math.Max(1, q.RenderedDots), (int)(printable * 0.7f)) * scale + 10,
        BarcodeElement b => (b.Height + 24) * scale + 8,
        ImageElement i =>
            i.HeightDotsValue * (Math.Min(i.WidthDots, printable) * scale / Math.Max(1f, i.WidthDots)) + 6,
        CutElement => 40f * scale + 4,
        FeedElement f => Math.Max(0, f.HeightDots) * scale,
        _ => 20
    };

    private float DrawText(SKCanvas canvas, TextLineElement el, float y, int printable, int scale)
    {
        string text = el.Text ?? "";
        float fontSize = 12f * scale;
        if (el.Style.Size is CharacterSize.DoubleHeight or CharacterSize.DoubleWidthHeight)
            fontSize *= 1.8f;

        bool bold = el.Style.Bold || el.Style.Emphasized;
        bool rtl = ContainsRtlText(text);

        // Monospace for Latin receipts; a real Arabic-capable font for RTL lines.
        SKTypeface typeface = rtl
            ? (bold ? _typefaceRtlBold : _typefaceRtl) ?? _typeface
            : (bold ? _typefaceBold : _typeface);
        typeface ??= SKTypeface.CreateDefault();

        using var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = fontSize,
            IsAntialias = true,
            Color = el.Style.Invert ? SKColors.White : SKColors.Black,
            FakeBoldText = el.Style.Bold
        };

        float maxW = printable * scale;
        float baseline = y + fontSize;
        float textWidth;
        float x = Margin;

        if (rtl)
        {
            // HarfBuzz gives us contextual Arabic letter joining (initial /
            // medial / final forms). Digits and Latin words are split into
            // separate LTR runs and the run order is reversed for RTL lines,
            // so "الإجمالي: 123.45" keeps its numbers in the right order.
            using var shaper = new SKShaper(typeface);
            var runs = BuildVisualRuns(text);
            var widths = new float[runs.Count];
            textWidth = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                widths[i] = ShapeWidth(shaper, runs[i].Text, paint);
                textWidth += widths[i];
            }

            switch (el.Style.Alignment)
            {
                case TextAlign.Center:
                    x = Margin + Math.Max(0, (maxW - textWidth) / 2);
                    break;
                case TextAlign.Right:
                    x = Margin + Math.Max(0, maxW - textWidth);
                    break;
            }

            if (el.Style.Invert)
            {
                using var bg = new SKPaint { Color = SKColors.Black };
                canvas.DrawRect(x - 2, baseline - fontSize + 2, x + textWidth + 4, baseline + 4, bg);
            }

            float cursor = x;
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i].Text.Length > 0)
                    canvas.DrawShapedText(shaper, runs[i].Text, cursor, baseline, paint);
                cursor += widths[i];
            }
        }
        else
        {
            // text is already truncated at the paper edge by the parser
            textWidth = paint.MeasureText(text);

            switch (el.Style.Alignment)
            {
                case TextAlign.Center:
                    x = Margin + Math.Max(0, (maxW - textWidth) / 2);
                    break;
                case TextAlign.Right:
                    x = Margin + Math.Max(0, maxW - textWidth);
                    break;
            }

            if (el.Style.Invert)
            {
                using var bg = new SKPaint { Color = SKColors.Black };
                canvas.DrawRect(x - 2, baseline - fontSize + 2, x + textWidth + 4, baseline + 4, bg);
            }

            if (!string.IsNullOrEmpty(text))
                canvas.DrawText(text, x, baseline, paint);
        }

        if (el.Style.Underline)
        {
            using var under = new SKPaint
            {
                Color = el.Style.Invert ? SKColors.White : SKColors.Black,
                StrokeWidth = 1.5f * scale,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawLine(x, baseline + 3, x + Math.Max(4, textWidth), baseline + 3, under);
        }

        return y + Math.Max(1, el.HeightDots) * scale + 4;
    }

    private static float ShapeWidth(SKShaper shaper, string text, SKPaint paint)
    {
        if (text.Length == 0) return 0;
        try
        {
            return Math.Max(0, shaper.Shape(text, paint).Width);
        }
        catch
        {
            return paint.MeasureText(text);
        }
    }

    private readonly record struct TextRun(string Text, bool Rtl);

    private enum CharDirection
    {
        Neutral,
        Rtl,
        Ltr,
    }

    /// <summary>
    /// Splits a line into directional runs and returns them in visual
    /// (left-to-right) order. A compact bidi pass is enough for receipts:
    /// Arabic/Hebrew are RTL, digits and Latin letters are LTR, and neutral
    /// characters follow their surrounding text.
    /// </summary>
    private static List<TextRun> BuildVisualRuns(string text)
    {
        int length = text.Length;
        var directions = new CharDirection[length];
        bool? firstStrongRtl = null;

        for (int i = 0; i < length; i++)
        {
            directions[i] = Classify(text[i]);
            if (firstStrongRtl == null)
            {
                if (directions[i] == CharDirection.Rtl) firstStrongRtl = true;
                else if (directions[i] == CharDirection.Ltr) firstStrongRtl = false;
            }
        }

        bool baseRtl = firstStrongRtl ?? false;

        // Resolve neutral runs (spaces, punctuation, currency signs, …).
        for (int i = 0; i < length;)
        {
            if (directions[i] != CharDirection.Neutral)
            {
                i++;
                continue;
            }

            int start = i;
            while (i < length && directions[i] == CharDirection.Neutral)
                i++;

            var previous = start > 0 ? directions[start - 1] : CharDirection.Neutral;
            var next = i < length ? directions[i] : CharDirection.Neutral;

            CharDirection fill;
            if (previous != CharDirection.Neutral && next != CharDirection.Neutral)
                fill = previous == next ? previous : (baseRtl ? CharDirection.Rtl : CharDirection.Ltr);
            else if (previous != CharDirection.Neutral)
                fill = previous;
            else if (next != CharDirection.Neutral)
                fill = next;
            else
                fill = baseRtl ? CharDirection.Rtl : CharDirection.Ltr;

            for (int j = start; j < i; j++)
                directions[j] = fill;
        }

        var runs = new List<TextRun>();
        int runStart = 0;
        for (int i = 1; i <= length; i++)
        {
            if (i == length || directions[i] != directions[runStart])
            {
                runs.Add(new TextRun(text[runStart..i], directions[runStart] == CharDirection.Rtl));
                runStart = i;
            }
        }

        // RTL base: place logical runs from right to left.
        if (baseRtl)
            runs.Reverse();

        return runs;
    }

    private static CharDirection Classify(char c)
    {
        if (IsRtlChar(c))
            return CharDirection.Rtl;
        if (c == '\u200F')                 // RIGHT-TO-LEFT MARK
            return CharDirection.Rtl;
        if (char.IsDigit(c) || char.IsLetter(c) || c == '\u200E')
            return CharDirection.Ltr;
        return CharDirection.Neutral;
    }

    private static bool ContainsRtlText(string text)
    {
        foreach (char c in text)
            if (IsRtlChar(c)) return true;
        return false;
    }

    private static bool IsRtlChar(char c)
    {
        // Arabic-Indic / Persian digits display left-to-right like ASCII digits.
        if (c is >= '\u0660' and <= '\u0669') return false;
        if (c is >= '\u06F0' and <= '\u06F9') return false;

        return (c >= '\u0590' && c <= '\u05FF') ||   // Hebrew
               (c >= '\u0600' && c <= '\u06FF') ||   // Arabic
               (c >= '\u0700' && c <= '\u074F') ||   // Syriac
               (c >= '\u0750' && c <= '\u077F') ||   // Arabic Supplement
               (c >= '\u0780' && c <= '\u07BF') ||   // Thaana / NKo
               (c >= '\u08A0' && c <= '\u08FF') ||   // Arabic Extended-A
               (c >= '\uFB1D' && c <= '\uFB4F') ||   // Hebrew presentation forms
               (c >= '\uFB50' && c <= '\uFDFF') ||   // Arabic presentation forms-A
               (c >= '\uFE70' && c <= '\uFEFF');     // Arabic presentation forms-B
    }

    private float DrawQr(SKCanvas canvas, QrCodeElement qr, float y, int printable, int scale)
    {
        if (string.IsNullOrEmpty(qr.Content)) return y + 20;
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(qr.Content, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(data);
            int module = Math.Clamp(qr.ModuleSize, 1, 16);
            byte[] pngBytes = png.GetGraphic(module);
            if (pngBytes == null || pngBytes.Length == 0) return y + 20;

            using var skData = SKData.CreateCopy(pngBytes);
            using var bitmap = SKBitmap.Decode(skData);
            if (bitmap == null) return y + 20;

            float size = Math.Min(qr.RenderedDots > 0 ? qr.RenderedDots : bitmap.Width, printable * 0.7f) * scale;
            float x = Margin + (printable * scale - size) / 2;
            canvas.DrawBitmap(bitmap, new SKRect(x, y, x + size, y + size));
            return y + size + 10;
        }
        catch
        {
            return y + 20;
        }
    }

    private float DrawBarcode(SKCanvas canvas, BarcodeElement bc, float y, int printable, int scale)
    {
        if (string.IsNullOrEmpty(bc.Data)) return y + 20;
        try
        {
            float w = Math.Min(printable * 0.9f, 480f) * scale;
            float h = (bc.Height + 24) * scale;

            // ZXing.NET's BarcodeWriter is not IDisposable — plain local.
            var writer = new BarcodeWriter
            {
                Format = (bc.Type ?? "CODE128").ToUpperInvariant() switch
                {
                    "EAN13" => BarcodeFormat.EAN_13,
                    "EAN8" => BarcodeFormat.EAN_8,
                    "UPCA" => BarcodeFormat.UPC_A,
                    "UPCE" => BarcodeFormat.UPC_E,
                    "CODE39" => BarcodeFormat.CODE_39,
                    _ => BarcodeFormat.CODE_128,
                },
                Options = new ZXing.Common.EncodingOptions
                {
                    Height = Math.Max(40, (int)h),
                    Width = Math.Max(120, (int)w),
                    Margin = 1,
                    PureBarcode = false
                }
            };

            using var bmp = writer.Write(bc.Data);
            if (bmp == null || bmp.Width < 2) return y + 20;

            float x = Margin + (printable * scale - w) / 2;
            canvas.DrawBitmap(bmp, new SKRect(x, y, x + w, y + h));
            return y + h + 8;
        }
        catch
        {
            return y + 20;
        }
    }

    private float DrawImage(SKCanvas canvas, ImageElement img, float y, int printable, int scale)
    {
        if (img.WidthDots <= 0 || img.HeightDotsValue <= 0 || img.BitmapData == null || img.BitmapData.Length == 0)
            return y + 20;
        try
        {
            int wpx = img.WidthDots, hpx = img.HeightDotsValue;
            int rowBytes = (wpx + 7) / 8;
            // Bulk-convert 1bpp → BGRA instead of SetPixel() per dot: GDI
            // drivers send whole-page bands (hundreds of thousands of dots),
            // and per-pixel interop would freeze the UI thread.
            var pixels = new byte[wpx * hpx * 4];
            int pi = 0;
            for (int row = 0; row < hpx; row++)
            {
                int rowStart = row * rowBytes;
                for (int col = 0; col < wpx; col++)
                {
                    int bi = rowStart + (col >> 3);
                    bool black = bi < img.BitmapData.Length
                                 && (img.BitmapData[bi] & (0x80 >> (col & 7))) != 0;
                    byte v = black ? (byte)0 : (byte)255;
                    pixels[pi++] = v;       // B
                    pixels[pi++] = v;       // G
                    pixels[pi++] = v;       // R
                    pixels[pi++] = 255;     // A
                }
            }

            var info = new SKImageInfo(wpx, hpx, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap(info);
            Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);

            float w = Math.Min(wpx, printable) * scale;
            float h = hpx * (w / Math.Max(1, wpx));
            float x = Margin + (printable * scale - w) / 2;
            canvas.DrawBitmap(bmp, new SKRect(x, y, x + w, y + h));
            return y + h + 6;
        }
        catch
        {
            return y + 20;
        }
    }

    private float DrawCut(SKCanvas canvas, CutElement cut, float y, int printable, int scale)
    {
        float cy = y + 20f * scale;
        using var paint = new SKPaint
        {
            Color = new SKColor(120, 120, 120),
            StrokeWidth = 1.5f,
            PathEffect = SKPathEffect.CreateDash([6, 4], 0),
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawLine(Margin, cy, Margin + printable * scale, cy, paint);

        using var textPaint = new SKPaint
        {
            Typeface = _typeface ?? SKTypeface.CreateDefault(),
            TextSize = 10f * scale,
            Color = new SKColor(140, 140, 140),
            IsAntialias = true
        };
        string label = cut.Partial ? "✂ PARTIAL CUT" : "✂ FULL CUT";
        float tw = textPaint.MeasureText(label);
        canvas.DrawText(label, Margin + (printable * scale - tw) / 2, y + 36f * scale, textPaint);
        return y + 40f * scale + 4;
    }
}
