using System.Text;

namespace EscPosEmulator.Core;

/// <summary>
/// Safe access to code-page encodings.
/// .NET (Core) 5+ does not ship CP437 and friends by default —
/// the CodePagesEncodingProvider must be registered once at startup.
/// </summary>
public static class EncodingHelper
{
    private static bool _registered;
    private static readonly object Lock = new();

    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (Lock)
        {
            if (_registered) return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _registered = true;
        }
    }

    public static Encoding Get(int codePage)
    {
        EnsureRegistered();
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    public static Encoding Get(string name)
    {
        EnsureRegistered();
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Decodes one line of printer text.
    ///
    /// Many modern POS applications send UTF-8 even when they never issue
    /// <c>ESC t 255</c> (or when the code table is still the profile default).
    /// In that case the selected legacy code page (CP437, CP1256, …) would turn
    /// Arabic into gibberish. If the byte sequence is valid UTF-8 and contains
    /// at least one multi-byte character, UTF-8 wins over the fallback table.
    /// Genuine single-byte Arabic (Windows-1256 / CP720) is not valid UTF-8,
    /// so it still decodes through the selected code page.
    /// </summary>
    public static string DecodeText(ReadOnlySpan<byte> bytes, Encoding? fallback)
    {
        EnsureRegistered();
        if (bytes.IsEmpty) return "";
        fallback ??= Encoding.UTF8;

        if (fallback.CodePage != Encoding.UTF8.CodePage && HasNonAscii(bytes))
        {
            if (TryDecodeUtf8(bytes, out string utf8Text))
                return utf8Text;
        }

        try
        {
            return fallback.GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static bool HasNonAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            if (b >= 0x80) return true;
        return false;
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        text = "";
        bool multiByte = false;
        foreach (byte b in bytes)
        {
            if (b >= 0xC0)
            {
                multiByte = true;
                break;
            }
        }
        if (!multiByte) return false;

        try
        {
            // Throw on malformed input: a legacy single-byte line must not be
            // silently reinterpreted as mojibake UTF-8.
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>ESC/POS code-table id (ESC t n) → .NET encoding.</summary>
    public static Encoding FromEscPosTable(int tableId)
    {
        EnsureRegistered();
        return tableId switch
        {
            0 or 1 => Get(437),          // PC437 USA
            2 => Get(850),               // PC850 Multilingual
            3 => Get(860),               // PC860 Portuguese
            4 => Get(863),               // PC863 Canadian French
            5 => Get(865),               // PC865 Nordic
            11 => Get(851),              // PC851 Greek (some Epson)
            12 => Get(853),              // PC853 Turkish
            13 => Get(857),             // PC857 Turkish
            14 => Get(737),              // PC737 Greek
            15 => Get(1257),             // WPC1257 Baltic
            16 or 17 => Get(1252),       // WPC1252 Latin1
            18 => Get(852),              // PC852 Latin2
            19 => Get(858),              // PC858 Euro
            20 => Get(855),              // PC855 Cyrillic
            21 => Get(866),              // PC866 Cyrillic #2
            22 => Get(1250),             // WPC1250 Central Europe
            23 => Get(1251),             // WPC1251 Cyrillic
            24 => Get(1253),             // WPC1253 Greek
            25 => Get(1254),             // WPC1254 Turkish
            26 => Get(1255),             // WPC1255 Hebrew
            27 => Get(1256),             // WPC1256 Arabic
            28 => Get(1258),             // WPC1258 Vietnam
            30 => Get(720),              // PC720 Arabic
            31 => Get(775),              // PC775 Baltic
            32 => Get(874),              // WPC874 Thai
            33 or 34 => Get(932),        // Shift-JIS (Japanese)
            35 or 36 => Get(949),        // Korean
            37 or 38 => Get(936),        // GB2312 / GBK Chinese
            39 or 40 => Get(950),        // Big5 Traditional Chinese
            255 => Encoding.UTF8,
            _ => Get(437)
        };
    }

    /// <summary>UI label for a table id, e.g. "CP437 USA (0)".</summary>
    public static string DescribeTable(int tableId) => tableId switch
    {
        0 or 1 => "CP437 USA (0)",
        2 => "CP850 Multilingual (2)",
        3 => "CP860 Portuguese (3)",
        4 => "CP863 Canadian French (4)",
        5 => "CP865 Nordic (5)",
        16 or 17 => "Windows-1252 (16)",
        18 => "CP852 Latin2 (18)",
        19 => "CP858 Euro (19)",
        21 => "CP866 Cyrillic (21)",
        22 => "Windows-1250 (22)",
        23 => "Windows-1251 Cyrillic (23)",
        24 => "Windows-1253 Greek (24)",
        26 => "Windows-1255 Hebrew (26)",
        27 => "Windows-1256 Arabic (27)",
        30 => "CP720 Arabic (30)",
        32 => "Windows-874 Thai (32)",
        33 or 34 => "Shift-JIS Japanese (33)",
        37 or 38 => "GBK Chinese (37)",
        39 or 40 => "Big5 Chinese (39)",
        255 => "UTF-8 (255)",
        _ => $"Code page {tableId}"
    };

    /// <summary>The list of tables selectable in the UI (id → label).</summary>
    public static IReadOnlyList<(int Id, string Label)> Tables { get; } =
    [
        (0, DescribeTable(0)),
        (2, DescribeTable(2)),
        (3, DescribeTable(3)),
        (4, DescribeTable(4)),
        (5, DescribeTable(5)),
        (16, DescribeTable(16)),
        (18, DescribeTable(18)),
        (19, DescribeTable(19)),
        (21, DescribeTable(21)),
        (22, DescribeTable(22)),
        (23, DescribeTable(23)),
        (24, DescribeTable(24)),
        (26, DescribeTable(26)),
        (27, DescribeTable(27)),
        (30, DescribeTable(30)),
        (32, DescribeTable(32)),
        (33, DescribeTable(33)),
        (37, DescribeTable(37)),
        (39, DescribeTable(39)),
        (255, DescribeTable(255)),
    ];
}
