using EscPosEmulator.Core;
using EscPosEmulator.Models;

namespace EscPosEmulator.Parsing;

/// <summary>
/// ESC / GS command handlers for <see cref="EscPosParser"/>.
/// A handler either completes (done=true, bytes consumed) or waits
/// for more bytes (done=false).
/// </summary>
public sealed partial class EscPosParser
{
    // ───────────────────────────── ESC ─────────────────────────────

    private (bool Done, int Advance) HandleEsc(byte cmd)
    {
        switch (cmd)
        {
            case 0x40:   // ESC @  — initialize: reset format, keep profile metrics.
                // The code table deliberately survives: like real hardware, the
                // user-selected / last-sent code page must persist through
                // ESC @ (otherwise a receipt that opens with ESC @ would lose
                // the Windows-1256 selection and Arabic bytes would be decoded
                // as CP437). Only profile switches or an explicit ESC t change
                // the table.
                _state.ResetFormat();
                return (true, 2);

            case 0x61:   // ESC a n — alignment
                if (_cmd.Count < 3) return (false, 0);
                _state.Alignment = _cmd[2] switch
                {
                    1 => TextAlign.Center,
                    2 => TextAlign.Right,
                    _ => TextAlign.Left,
                };
                return (true, 3);

            case 0x45:   // ESC E n — bold
                if (_cmd.Count < 3) return (false, 0);
                _state.Bold = _cmd[2] != 0;
                return (true, 3);

            case 0x47:   // ESC G n — emphasized (stronger)
                if (_cmd.Count < 3) return (false, 0);
                _state.Emphasized = _cmd[2] != 0;
                return (true, 3);

            case 0x2D:   // ESC - n — underline 0/1
                if (_cmd.Count < 3) return (false, 0);
                _state.Underline = _cmd[2] >= 1;
                return (true, 3);

            case 0x21:   // ESC ! n — combined print mode
                if (_cmd.Count < 3) return (false, 0);
                {
                    byte n = _cmd[2];
                    _state.Bold = (n & 0x08) != 0;
                    _state.Underline = (n & 0x10) != 0;
                    _state.Emphasized = (n & 0x20) != 0;
                    _state.Size = (n & 0x03) switch
                    {
                        1 => CharacterSize.DoubleWidth,
                        2 => CharacterSize.DoubleHeight,
                        3 => CharacterSize.DoubleWidthHeight,
                        _ => CharacterSize.Normal,
                    };
                    return (true, 3);
                }

            case 0x74:   // ESC t n — select code table
                if (_cmd.Count < 3) return (false, 0);
                SetCodeTable(_cmd[2]);
                return (true, 3);

            case 0x52:   // ESC R n — international charset
                if (_cmd.Count < 3) return (false, 0);
                _state.InternationalCharset = _cmd[2];
                return (true, 3);

            case 0x33:   // ESC 3 n — line spacing (dots)
                if (_cmd.Count < 3) return (false, 0);
                _state.LineSpacingDots = _cmd[2];
                return (true, 3);

            case 0x32:   // ESC 2 — default line spacing / normal height
                _state.LineSpacingDots = _state.CharHeightDots;
                return (true, 2);

            case 0x30:   // ESC 0 — reserved (ignored)
                return (true, 2);

            case 0x64:   // ESC d n — feed n lines
                if (_cmd.Count < 3) return (false, 0);
                FlushTextLocked();
                CurrentDocument.Add(new FeedElement(_cmd[2], dots: 0));
                return (true, 3);

            case 0x4A:   // ESC J n — print and feed n dots
                if (_cmd.Count < 3) return (false, 0);
                // GDI (Xprinter etc.) drivers emit one GS v 0 band of exactly
                // n rows followed by ESC J n. That feed only advances to the
                // next band; adding it as gap would double-space the bitmap.
                if (_lastRasterElement != null &&
                    ReferenceEquals(CurrentDocument.LastElement, _lastRasterElement) &&
                    _cmd[2] == _lastRasterBandHeight)
                    return (true, 3);
                FlushTextLocked();
                CurrentDocument.Add(new FeedElement(0, dots: _cmd[2]));
                return (true, 3);

            case 0x69:   // ESC i — full cut
                if (_cmd.Count < 2) return (false, 0);
                FlushTextLocked();
                if (_profile?.HasCutter != false)
                    CurrentDocument.Add(new CutElement(partial: false));
                return (true, 2);

            case 0x6D:   // ESC m — partial cut
                if (_cmd.Count < 2) return (false, 0);
                FlushTextLocked();
                if (_profile?.HasCutter != false)
                    CurrentDocument.Add(new CutElement(partial: true));
                return (true, 2);

            case 0x70:   // ESC p m t1 t2 — drawer kick
                if (_cmd.Count < 5) return (false, 0);
                DrawerKick();
                return (true, 5);

            default:
                // Unknown ESC command: consume a small plausible parameter
                // window and move on — never hangs the stream.
                if (_cmd.Count >= 8)
                {
                    ClearPendingLocked();
                    return (true, 0);
                }
                return (false, 0);
        }
    }

    private void DrawerKick() => DrawerKickEvent?.Invoke();

    // ───────────────────────────── GS ─────────────────────────────

    private (bool Done, int Advance) HandleGs(byte cmd)
    {
        switch (cmd)
        {
            case 0x21:   // GS ! n — character size
                if (_cmd.Count < 3) return (false, 0);
                {
                    byte n = _cmd[2];
                    bool wide = ((n >> 4) & 0x0F) >= 2;
                    bool tall = (n & 0x0F) >= 2;
                    _state.Size = (wide, tall) switch
                    {
                        (true, true) => CharacterSize.DoubleWidthHeight,
                        (true, false) => CharacterSize.DoubleWidth,
                        (false, true) => CharacterSize.DoubleHeight,
                        _ => CharacterSize.Normal,
                    };
                    return (true, 3);
                }

            case 0x42:   // GS B n — inversion
                if (_cmd.Count < 3) return (false, 0);
                _state.Invert = _cmd[2] != 0;
                return (true, 3);

            case 0x56:   // GS V A/B/d — cut (A=full, B=partial, d=partial)
                if (_cmd.Count < 3) return (false, 0);
                FlushTextLocked();
                if (_profile?.HasCutter != false)
                {
                    byte mode = _cmd[2];
                    bool partial = mode is (byte)('B') or (byte)('d');
                    CurrentDocument.Add(new CutElement(partial));
                }
                return (true, 3);

            case 0x76:   // GS v 0 m xL xH yL yH data — raster bit image
                if (_cmd.Count < 3) return (false, 0);
                if (_cmd[2] != 0x30) return (true, 3);   // only "GS v 0" supported
                if (_cmd.Count < 8) return (false, 0);   // header still arriving
                {
                    int rowBytes = _cmd[4] | (_cmd[5] << 8);   // bytes per row
                    int rows = _cmd[6] | (_cmd[7] << 8);       // rows (dots)
                    long need = (long)rowBytes * rows;
                    if (rowBytes is <= 0 or > 1024 || rows is <= 0 or > 8192 || need > 8_000_000)
                    {
                        ClearPendingLocked();
                        return (true, 0);
                    }
                    if (_cmd.Count < 8 + need) return (false, 0);   // payload arriving
                    var data = new byte[need];
                    _cmd.CopyTo(8, data, 0, (int)need);
                    DecodeRasterLocked(rows, data);
                    return (true, 8 + (int)need);
                }

            case 0x28:   // GS ( k / GS ( l — sub-command
                if (_cmd.Count < 3) return (false, 0);
                byte kind = _cmd[2];
                if (kind is (byte)('k') or (byte)('l'))
                {
                    _cmdMode = kind == (byte)('k') ? 1 : 2;
                    _blob.Clear();
                    _qrContent = null;
                    _qrEcl = 0;
                    return (true, 3);
                }
                return (true, 3);           // unknown kind: drop

            case 0x6B:   // GS k m [n [data]] — barcode
                if (_cmd.Count < 3) return (false, 0);
                if (_cmd[2] is 2 or 4 or 65)
                {
                    // variable data: m, n, d0..dn-1
                    if (_cmd.Count < 4) return (false, 0);
                    int n = _cmd[3];
                    if (n > 96) { ClearPendingLocked(); return (true, 0); }
                    if (_cmd.Count < 4 + n) return (false, 0);
                    string data = GetStringBytes(_cmd, 4, n);
                    if (data.Length > 0)
                        AddBarcodeLocked(data, "CODE128", 80);
                    return (true, 4 + n);
                }
                // fixed formats: m, n, d0..dn-1 (n = data length)
                if (_cmd.Count < 4) return (false, 0);
                {
                    int model = _cmd[2] & 0x0F;
                    string type = model switch
                    {
                        1 => "CODE39",
                        2 => "EAN13",
                        3 => "EAN8",
                        4 => "CODE128",
                        5 => "UPCA",
                        6 => "UPCE",
                        _ => "CODE128",
                    };
                    int dataLen = _cmd[3];
                    if (_cmd.Count < 4 + dataLen) return (false, 0);
                    string data = GetStringBytes(_cmd, 4, dataLen);
                    if (data.Length > 0)
                        AddBarcodeLocked(data, type, 80);
                    return (true, 4 + dataLen);
                }

            case 0x4A:   // GS J n — feed n/170 inch
                if (_cmd.Count < 3) return (false, 0);
                FlushTextLocked();
                CurrentDocument.Add(new FeedElement(0, dots: Math.Max(1, _cmd[2] * 24 / 8)));
                return (true, 3);

            case 0x4B:   // GS K n — feed n /420 inch
                if (_cmd.Count < 3) return (false, 0);
                FlushTextLocked();
                CurrentDocument.Add(new FeedElement(0, dots: Math.Max(1, _cmd[2] * 24 / 17)));
                return (true, 3);

            case 0x03:   // GS W n — left margin (not emulated)
            case 0x46:   // GS f n — horizontal tab
            case 0x4C:   // GS L n — top/bottom print mode
            case 0x79:   // GS y n — character height (ignored)
                if (_cmd.Count < 3) return (false, 0);
                return (true, 3);

            default:
                if (_cmd.Count >= 10)
                {
                    ClearPendingLocked();
                    return (true, 0);
                }
                return (false, 0);
        }
    }

    private string GetStringBytes(List<byte> src, int off, int count)
    {
        if (off < 0 || count < 0 || off + count > src.Count) count = Math.Max(0, src.Count - off);
        if (count <= 0) return "";
        var bytes = new byte[count];
        src.CopyTo(off, bytes, 0, count);
        try
        {
            string s = _state.CurrentEncoding.GetString(bytes);
            // keep only ASCII printable text
            var chars = new char[s.Length];
            int n = 0;
            foreach (char c in s)
                if (c >= 0x20 && c < 0x7F && n < 64)
                    chars[n++] = c;
            return new string(chars, 0, n);
        }
        catch
        {
            return "";
        }
    }

    private void AddBarcodeLocked(string data, string type, int height)
    {
        FlushTextLocked();
        CurrentDocument.Add(new BarcodeElement(data, type, height));
    }

    // ─────────────── GS ( k  /  GS ( l  sub-commands ───────────────
    //
    // Stream:  pn  cn  a  sub  <cn bytes of data>
    //   a=0x6C (k): QR code sub-commands
    //     0x41 (65)  select model 1/2
    //     0x43 (67)  module size (1..16)
    //     0x6E (110) error correction level 0/1/2
    //     0x6A (106) data mode 0=auto 1=UTF-8 2=manual
    //     0x68 (104) store data (manual mode)
    //     0x70 (112) store data (UTF-8 mode, self-contained)
    //     0x69 (105) print stored QR
    //     0x71 (113) store+print? — treated as store data
    //   a=0x6C (l): other functions, e.g.
    //     0x38 (56)  raster image: cn = row count, data = rows

    private void ProcessGsiCommand(byte b)
    {
        _blob.Add(b);

        // Drains every complete sub-frame in the buffer. Real streams re-send
        // the full "GS ( k" header per sub-command, so normally only one frame
        // completes per call; the loop also handles concatenated frames.
        while (_blob.Count >= 4)
        {
            int pn = _blob[0];
            int cn = _blob[1];
            if (cn > 2048) { ClearPendingLocked(); return; }
            if (_blob.Count < 4 + cn) return;    // wait for the data part

            byte sub = _blob[3];
            var data = new byte[cn];
            _blob.CopyTo(4, data, 0, cn);

            ProcessOneGsiFrame(pn, cn, sub, data);   // deletes its frame in a finally
        }
        if (_blob.Count == 0) _cmdMode = 0;      // sub-command sequence finished
    }

    private void ProcessOneGsiFrame(int pn, int cn, byte sub, byte[] data)
    {
        try
        {
            if (_cmdMode == 1)   // GS ( k  → QR / printer features
            {
                // Sub-command numbers vary between vendors, so we accept the
                // wide-range set actually seen in the field. Content-bearing
                // sub-commands render immediately (emulator is best-effort);
                // an explicit "print stored" sub re-renders only if one is
                // pending, so a store→print pair never duplicates.
                switch (sub)
                {
                    case 0x41:   // 65 — set model (ignore)
                        break;
                    case 0x43:   // 67 — module size
                        if (data.Length > 0 && data[0] is >= 1 and <= 16)
                            _qrModuleSize = data[0];
                        break;
                    case 0x44:   // 68 — error correction level
                        if (data.Length > 0) _qrEcl = Math.Clamp((int)data[0], 0, 2);
                        break;
                    case 0x45:   // 69 — data mode / store (manual)
                    case 0x46:   // 70 — store data (manual)
                    case 0x68:   // 104 — store data
                    case 0x6A:   // 106 — store data (auto)
                        // Render-on-store: content-bearing sub-commands
                        // materialize the QR immediately (emulator semantics).
                        if (data.Length > 1)
                        {
                            _qrContent = data;
                            ExecuteQrLocked();
                        }
                        // else: lone mode/size byte — nothing to render
                        break;
                    case 0x47:   // 71 — print stored (manual)
                    case 0x69:   // 105 — print stored (auto)
                        ExecuteQrLocked();
                        break;
                    case 0x6B:   // 107 — horizontal alignment (ignore)
                    default:
                        break;
                }
            }
            else                 // GS ( l → image and other functions
            {
                switch (sub)
                {
                    case 0x38:   // raster image (a=0x6C, sub 56)
                        _ = pn;
                        DecodeRasterLocked(cn, data);
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            _blob.RemoveRange(0, Math.Min(4 + cn, _blob.Count));
        }
    }

    private void ExecuteQrLocked()
    {
        if (_profile?.SupportsQr == false) { _qrContent = null; return; }
        if (_qrContent == null || _qrContent.Length == 0) { _qrContent = null; return; }

        string content;
        try
        {
            var nonNull = _qrContent.Where(x => x != 0).ToArray();
            content = EncodingHelper.DecodeText(nonNull, _state.CurrentEncoding);
        }
        catch
        {
            content = "";
        }
        _qrContent = null;
        if (string.IsNullOrEmpty(content)) return;

        // Module count heuristic by content length (QR auto mode, version cap 8).
        int bytes = content.Length;
        int modules = bytes switch
        {
            <= 17  => 21,
            <= 32  => 25,
            <= 53  => 29,
            <= 78  => 33,
            _      => 37,
        };
        int dots = modules * _qrModuleSize;

        FlushTextLocked();
        CurrentDocument.Add(new QrCodeElement(content, _qrModuleSize, dots));
    }

    private void DecodeRasterLocked(int cn, byte[] data)
    {
        if (_profile?.SupportsRaster == false || data.Length == 0) return;

        int rowBytes = (data.Length + Math.Max(1, cn) - 1) / Math.Max(1, cn);
        int width = rowBytes * 8;
        int height = Math.Max(1, cn);
        if (width <= 0 || height <= 0 || width > 2000 || height > 5000) return;

        var mono = NormalizeRaster(data, rowBytes, width, height);
        FlushTextLocked();

        // GDI drivers (XP-80, etc.) slice one tall page bitmap into bands of
        // (e.g.) 24 rows: merge consecutive same-width bands so the receipt
        // keeps pixel-perfect vertical continuity instead of one gap per band.
        if (_lastRasterElement != null &&
            ReferenceEquals(CurrentDocument.LastElement, _lastRasterElement) &&
            _lastRasterElement.WidthDots == width)
        {
            var prev = _lastRasterElement;
            int prevRowBytes = (prev.WidthDots + 7) / 8;
            int prevLen = prev.HeightDotsValue * prevRowBytes;
            var merged = new byte[prevLen + height * prevRowBytes];
            Buffer.BlockCopy(prev.BitmapData, 0, merged, 0, Math.Min(prev.BitmapData.Length, prevLen));
            Buffer.BlockCopy(mono, 0, merged, prevLen, Math.Min(mono.Length, merged.Length - prevLen));
            var combined = new ImageElement(merged, width, prev.HeightDotsValue + height);
            CurrentDocument.ReplaceLast(combined);
            _lastRasterElement = combined;
            _lastRasterBandHeight = height;
            return;
        }

        var element = new ImageElement(mono, width, height);
        CurrentDocument.Add(element);
        _lastRasterElement = element;
        _lastRasterBandHeight = height;
    }

    /// <summary>Normalize a padded raster block to clean rows of (width+7)/8 bytes, MSB first.</summary>
    private static byte[] NormalizeRaster(byte[] data, int rowBytes, int width, int height)
    {
        int outRowBytes = (width + 7) / 8;
        var mono = new byte[outRowBytes * height];
        for (int row = 0; row < height; row++)
        {
            int srcStart = row * rowBytes;
            if (srcStart >= data.Length) break;
            int avail = Math.Min(rowBytes, data.Length - srcStart);
            for (int x = 0; x < width; x++)
            {
                int by = x / 8;
                if (by >= avail) break;
                int bit = 7 - (x % 8);
                if ((data[srcStart + by] & (1 << bit)) != 0)
                {
                    int ob = row * outRowBytes + (x / 8);
                    mono[ob] |= (byte)(1 << (7 - (x % 8)));
                }
            }
        }
        return mono;
    }
}
