using EscPosEmulator.Core;
using EscPosEmulator.Models;

namespace EscPosEmulator.Parsing;

/// <summary>
/// Incremental, thread-safe ESC/POS command parser.
/// Feed raw bytes (e.g. one TCP read at a time); it maintains the printer
/// state and the current <see cref="ReceiptDocument"/>.
///
/// All state is guarded by a single lock so any number of TCP clients can
/// feed the parser concurrently.
/// </summary>
public sealed partial class EscPosParser
{
    private readonly object _gate = new();

    private readonly PrinterState _state = new();
    private readonly List<byte> _textBytes = new();  // raw bytes of the current line
    private readonly List<byte> _cmd = new();     // pending command bytes
    private readonly List<byte> _blob = new();    // data attached to GS ( k / GS ( l

    private int _cmdMode;                         // 0 = none, 1 = GS ( k, 2 = GS ( l
    private ImageElement? _lastRasterElement;     // for merging GDI raster bands
    private int _lastRasterBandHeight;
    private int _qrModuleSize = 4;
    private int _qrEcl = 0;
    private byte[]? _qrContent;
    private int _maxChars = 48;

    private PrinterProfile? _profile;

    public ReceiptDocument CurrentDocument { get; private set; } = new();
    public PrinterState State => _state;
    public PrinterProfile? ActiveProfile => _profile;

    /// <summary>Raised once per Feed() batch (on the feeding thread).</summary>
    public event Action? Changed;

    /// <summary>Raised once per NewJob() (on the feeding thread).</summary>
    public event Action? JobStarted;

    /// <summary>Raised when ESC p (drawer kick) is received.</summary>
    public event Action? DrawerKickEvent;

    public EscPosParser() => EncodingHelper.EnsureRegistered();

    public void SetProfile(PrinterProfile profile)
    {
        if (profile == null) return;
        lock (_gate)
        {
            FlushTextLocked();          // finish the current line with its old encoding
            _profile = profile;
            _state.ApplyProfile(profile);
            _maxChars = ComputeMaxChars(profile.Paper);
        }
    }

    /// <summary>Start a fresh print job (new receipt). Call on every new TCP connection.</summary>
    public void NewJob()
    {
        lock (_gate)
        {
            FlushTextLocked();
            ClearPendingLocked();
            CurrentDocument = new ReceiptDocument();
        }
        JobStarted?.Invoke();
    }

    /// <summary>Discard everything (format + current receipt), keep the profile.</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            FlushTextLocked();
            _state.ResetFormat();
            // Keep the selected code page (UI / last ESC t): clearing a
            // receipt must not silently switch the Arabic table back to the
            // profile default.
            ClearPendingLocked();
            CurrentDocument = new ReceiptDocument();
        }
        Changed?.Invoke();
    }

    // ───────────────────────────── Feed / byte processing ─────────────────────────────

    /// <summary>Feed raw bytes. May be called concurrently from any thread.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_gate)
        {
            foreach (byte b in data)
                ProcessByte(b);
            // Deliberately do NOT flush here: a TCP read can split a line or
            // even a multi-byte UTF-8 character in half. Lines are committed
            // on LF, on a formatting command, or by EndJob() on disconnect.
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Flush the last (unterminated) text line when the print job ends.
    /// Called by the TCP server on disconnect.
    /// </summary>
    public void EndJob()
    {
        lock (_gate)
        {
            FlushTextLocked();
            ClearPendingLocked();
        }
        Changed?.Invoke();
    }

    private void ProcessByte(byte b)
    {
        if (_cmdMode is 1 or 2)          // inside GS ( k | GS ( l
        {
            ProcessGsiCommand(b);
            return;
        }

        if (_cmd.Count > 0)              // inside an ordinary command
        {
            _cmd.Add(b);
            TryCompleteCommand();
            if (_cmd.Count > 256)        // runaway guard
            {
                if (IsCollectingLargeRaster())
                    return;              // GS v 0 carries a whole bitmap band
                ClearPendingLocked();
                ProcessByteText(b);
            }
            return;
        }

        ProcessByteText(b);
    }

    private void ProcessByteText(byte b)
    {
        switch (b)
        {
            case 0x0A:                   // LF → print line
                FlushTextLocked();
                break;
            case 0x00:                   // NUL
            case 0x0B:                   // VT
            case 0x0C:                   // FF
                break;                   // ignored (real hardware: some treat 0x00 as feed)
            case 0x0D:                   // CR
                // carriage return alone is a no-op on thermal printers;
                // CRLF is the norm and LF did the work
                break;
            case 0x09:                   // HT → approximate tab
                AppendTab(4);
                break;
            case 0x10:                   // DLE
            case 0x1C:                   // FS
            case 0x1B:                   // ESC
            case 0x1D:                   // GS
                _cmd.Add(b);
                break;
            default:
                if (b >= 0x20)
                    AppendTextByte(b);
                // remaining control bytes (0x01..0x08, 0x0E, 0x0F) are ignored
                break;
        }
    }

    private void AppendTextByte(byte b)
    {
        // Keep raw bytes: UTF-8 / DBCS characters must be decoded as complete
        // sequences, not one byte at a time.
        _textBytes.Add(b);
        CapTextBytes();
    }

    private void AppendTab(int width)
    {
        // simple tab: pad with spaces to the next multiple of `width` chars
        int charCount = _textBytes.Count;   // good approximation for ASCII tabs
        int next = (charCount / width + 1) * width;
        for (int i = charCount; i < next; i++)
            _textBytes.Add(0x20);
        CapTextBytes();
    }

    private void CapTextBytes()
    {
        // Truncation is character-based (see FlushTextLocked); this only stops
        // a runaway text run from growing without bound.
        int max = Math.Max(256, Math.Max(10, _maxChars) * 4);
        if (_textBytes.Count > max)
            _textBytes.RemoveRange(max, _textBytes.Count - max);
    }

    private void TryCompleteCommand()
    {
        if (_cmdMode != 0 || _cmd.Count < 2)
            return;

        byte prefix = _cmd[0];
        byte cmdByte = _cmd[1];

        if (prefix == 0x1B)              // ESC
        {
            var (done, advance) = HandleEsc(cmdByte);
            if (done)
                _cmd.RemoveRange(0, Math.Min(advance, _cmd.Count));
        }
        else if (prefix == 0x1D)         // GS
        {
            var (done, advance) = HandleGs(cmdByte);
            if (done)
                _cmd.RemoveRange(0, Math.Min(advance, _cmd.Count));
        }
        else                             // FS / DLE — consume a small window, drop
        {
            if (_cmd.Count >= 6)
                ClearPendingLocked();
        }
    }

    /// <summary>
    /// True while a GS v 0 raster frame is still being collected. The payload
    /// is a whole bitmap band (xL·xH bytes), so it legitimately exceeds the
    /// 256-byte guard used for ordinary commands.
    /// </summary>
    private bool IsCollectingLargeRaster()
    {
        if (_cmd.Count < 2 || _cmd[0] != 0x1D || _cmd[1] != 0x76) return false;
        if (_cmd.Count < 8) return true;                 // header still arriving
        if (_cmd[2] != 0x30) return false;
        int x = _cmd[4] | (_cmd[5] << 8);
        int y = _cmd[6] | (_cmd[7] << 8);
        long need = (long)x * y;
        return x > 0 && y > 0 && x <= 1024 && y <= 8192 && need <= 8_000_000
               && _cmd.Count <= 8 + need;
    }

    private void FlushTextLocked()
    {
        if (_textBytes.Count == 0) return;

        byte[] bytes = _textBytes.ToArray();
        _textBytes.Clear();

        string text = EncodingHelper.DecodeText(bytes, _state.CurrentEncoding);

        // Like real thermal paper: text beyond the printable width is
        // TRUNCATED at the edge (no wrapping) — overflow characters are dropped.
        int max = Math.Max(10, _maxChars);
        if (text.Length > max)
            text = text[..max];

        if (text.Length > 0)
            CurrentDocument.Add(new TextLineElement(text, _state));
    }

    private void ClearPendingLocked()
    {
        _cmd.Clear();
        _blob.Clear();
        _cmdMode = 0;
        _qrContent = null;
        _qrEcl = 0;
    }

    private static int ComputeMaxChars(PaperSize paper)
    {
        if (paper == null) return 48;
        return Math.Clamp(paper.PrintableWidthDots / 12, 20, 120);
    }

    /// <summary>Select the active code page from the UI (Arabic, UTF-8, …).</summary>
    public void SetEncoding(int table)
    {
        lock (_gate)
        {
            // Flush with the previous table so earlier bytes keep their encoding.
            FlushTextLocked();
            _state.CodePage = table;
            _state.CurrentEncoding = EncodingHelper.FromEscPosTable(table);
        }
        Changed?.Invoke();
    }

    private void SetCodeTable(int table)
    {
        // Flush with the previous table so earlier bytes keep their encoding.
        FlushTextLocked();
        _state.CodePage = table;
        _state.CurrentEncoding = EncodingHelper.FromEscPosTable(table);
    }
}
