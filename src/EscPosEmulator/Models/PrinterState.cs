using System.Text;
using EscPosEmulator.Core;

namespace EscPosEmulator.Models;

public enum TextAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

public enum CharacterSize
{
    Normal = 0,
    DoubleWidth = 1,
    DoubleHeight = 2,
    DoubleWidthHeight = 3,
}

/// <summary>
/// Current printer emulation state (formatting, encoding, metrics).
/// The parser clones this when a line is committed, so each stored line
/// keeps the format it was printed with.
/// </summary>
public sealed class PrinterState
{
    public TextAlign Alignment { get; set; } = TextAlign.Left;
    public bool Bold { get; set; }
    public bool Underline { get; set; } // 0/1 underline; 2 = double -> still drawn as underline
    public bool Invert { get; set; }
    public CharacterSize Size { get; set; } = CharacterSize.Normal;
    public int LineSpacingDots { get; set; } = 24;
    public int CodePage { get; set; } = 0;
    public int InternationalCharset { get; set; }
    public bool Emphasized { get; set; } // ESC G

    public Encoding CurrentEncoding { get; set; } = EncodingHelper.Get(437);

    public int CharWidthDots { get; set; } = 12;   // Font A
    public int CharHeightDots { get; set; } = 24;  // Font A

    /// <summary>Height in dots of a line rendered in this state.</summary>
    public int LineHeightDots =>
        Size is CharacterSize.DoubleHeight or CharacterSize.DoubleWidthHeight
            ? CharHeightDots * 2
            : Math.Max(1, LineSpacingDots > 0 && LineSpacingDots < CharHeightDots
                ? LineSpacingDots
                : CharHeightDots);

    public PrinterState Clone() => new()
    {
        Alignment = Alignment,
        Bold = Bold,
        Underline = Underline,
        Invert = Invert,
        Size = Size,
        LineSpacingDots = LineSpacingDots,
        CodePage = CodePage,
        InternationalCharset = InternationalCharset,
        Emphasized = Emphasized,
        CurrentEncoding = CurrentEncoding,
        CharWidthDots = CharWidthDots,
        CharHeightDots = CharHeightDots,
    };

    public void ApplyProfile(PrinterProfile profile)
    {
        if (profile == null) return;
        CharWidthDots = profile.CharWidthDots;
        CharHeightDots = profile.CharHeightDots;
        LineSpacingDots = CharHeightDots;
        CodePage = profile.DefaultCodePage;
        CurrentEncoding = EncodingHelper.FromEscPosTable(profile.DefaultCodePage);
    }

    /// <summary>ESC @ semantics: reset format to defaults, keep the profile's font metrics.</summary>
    public void ResetFormat()
    {
        Alignment = TextAlign.Left;
        Bold = false;
        Underline = false;
        Invert = false;
        Emphasized = false;
        Size = CharacterSize.Normal;
        LineSpacingDots = CharHeightDots;
        InternationalCharset = 0;
        // Code page / encoding intentionally preserved: on real hardware the
        // selected code table survives ESC @ on most models.
    }
}
