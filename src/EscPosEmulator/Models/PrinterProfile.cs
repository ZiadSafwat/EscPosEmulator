namespace EscPosEmulator.Models;

/// <summary>
/// Hardware profile for a real thermal / ESC/POS printer.
/// Selecting a profile sets paper width, DPI, default code page and feature flags.
/// </summary>
public sealed class PrinterProfile
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string DisplayName => (Manufacturer + " " + Model).Trim();

    public PaperSize Paper { get; init; } = new("80 mm", 80, 639, 512);
    public int DefaultCodePage { get; init; } = 0;   // ESC t n
    public int Dpi { get; init; } = 203;
    public int CharWidthDots { get; init; } = 12;    // Font A
    public int CharHeightDots { get; init; } = 24;   // Font A

    public bool HasCutter { get; init; } = true;
    public bool HasDrawerKick { get; init; } = true;
    public bool SupportsQr { get; init; } = true;
    public bool SupportsRaster { get; init; } = true;
    public bool SupportsKanji { get; init; } = false;

    public string Notes { get; init; } = "";

    public override string ToString() => DisplayName;

    // ───────────────────────────── Catalog ─────────────────────────────

    public static IReadOnlyList<PrinterProfile> All { get; } = BuildCatalog();

    private static List<PrinterProfile> BuildCatalog()
    {
        var list = new List<PrinterProfile>();

        // ── Epson (most common in POS) ────────────────────────────────
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-T88V / TM-T88VI",
            Paper = PaperSize.ForProfile(80, 180, 512), Dpi = 180, DefaultCodePage = 0,
            CharWidthDots = 12, CharHeightDots = 24,
            Notes = "Industry standard 80 mm. 180 dpi, 512 printable dots."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-T20III / TM-T20II",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Entry-level 80 mm. Very common in retail."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-T70II",
            Paper = PaperSize.ForProfile(80, 180, 512), Dpi = 180, DefaultCodePage = 0,
            Notes = "Front-exit 80 mm."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-m30II / TM-m30III",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Compact cube design, USB-C / Ethernet / Bluetooth."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-P20 / TM-P20II",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            HasCutter = false, HasDrawerKick = false,
            Notes = "Mobile 58 mm Bluetooth / Wi-Fi."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-P80",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            HasCutter = false,
            Notes = "Mobile 80 mm."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-U220 (impact)",
            Paper = PaperSize.ForProfile(76, 160, 450), Dpi = 160, DefaultCodePage = 0,
            SupportsQr = false, SupportsRaster = false,
            Notes = "Dot-impact, multi-ply, kitchen / bar."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-T88 (legacy)",
            Paper = PaperSize.ForProfile(80, 180, 512), Dpi = 180, DefaultCodePage = 0,
            Notes = "Older T88 series, same command set as T88V."
        });

        // ── Star Micronics ────────────────────────────────────────────
        list.Add(new PrinterProfile
        {
            Manufacturer = "Star", Model = "TSP100 / TSP143III",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Very popular cloud / Ethernet model. StarPRNT + ESC/POS mode."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Star", Model = "mC-Print3",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Modern cube, multi-interface."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Star", Model = "mC-Print2",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            Notes = "Compact 58 mm cube."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Star", Model = "SM-L200 / SM-S230i",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            HasCutter = false, HasDrawerKick = false,
            Notes = "Mobile Bluetooth 58 mm."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Star", Model = "TSP650II",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "High-speed 80 mm."
        });

        // ── Citizen ──────────────────────────────────────────────────
        list.Add(new PrinterProfile
        {
            Manufacturer = "Citizen", Model = "CT-S310II",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Reliable 80 mm desktop."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Citizen", Model = "CT-E351",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Front-exit, low-cost."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Citizen", Model = "CT-S281",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            Notes = "58 mm desktop."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Citizen", Model = "CMP-20 / CMP-30",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            HasCutter = false, HasDrawerKick = false,
            Notes = "Mobile series."
        });

        // ── Bixolon ──────────────────────────────────────────────────
        list.Add(new PrinterProfile
        {
            Manufacturer = "Bixolon", Model = "SRP-350III / SRP-350plusIII",
            Paper = PaperSize.ForProfile(80, 180, 512), Dpi = 180, DefaultCodePage = 0,
            Notes = "Epson-compatible 80 mm."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Bixolon", Model = "SRP-330II",
            Paper = PaperSize.ForProfile(80, 180, 512), Dpi = 180, DefaultCodePage = 0,
            Notes = "Value 80 mm."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Bixolon", Model = "SPP-R200III / SPP-R210",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            HasCutter = false, HasDrawerKick = false,
            Notes = "Rugged mobile 58 mm."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Bixolon", Model = "SRP-Q300",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Cube design."
        });

        // ── Xprinter / generic Chinese brands ─────────────────────────
        list.Add(new PrinterProfile
        {
            Manufacturer = "Xprinter", Model = "XP-58 / XP-58IIH",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            Notes = "Very common low-cost 58 mm USB."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Xprinter", Model = "XP-80 / XP-80C",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Low-cost 80 mm USB / Ethernet."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Rongta", Model = "RP80 / RP326",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Widely used OEM 80 mm."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Rongta", Model = "RP58",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            Notes = "OEM 58 mm."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Gprinter", Model = "GP-58 / GP-80250",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            Notes = "Label + receipt capable."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Zijiang", Model = "ZJ-58 / ZJ-80",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            Notes = "Common embedded / USB module."
        });

        // ── Other well-known ──────────────────────────────────────────
        list.Add(new PrinterProfile
        {
            Manufacturer = "Sewoo", Model = "LK-P20 / LK-P30",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            HasCutter = false, HasDrawerKick = false,
            Notes = "Mobile."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Honeywell", Model = "PR2 / PR3",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            HasCutter = false,
            Notes = "Enterprise mobile."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Zebra", Model = "iMZ220 / iMZ320",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            HasCutter = false, HasDrawerKick = false,
            Notes = "Mobile; CPCL/ZPL native but many accept ESC/POS."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "POS-X", Model = "EVO HiSpeed",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Retail 80 mm."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Ithaca", Model = "Series 90plus",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Legacy but still in the field."
        });

        // ── Regional / special code-page defaults ─────────────────────
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-T88V (Japan)",
            Paper = PaperSize.ForProfile(80, 180, 512), Dpi = 180, DefaultCodePage = 1,
            SupportsKanji = true,
            Notes = "Japanese model – Shift-JIS / Kanji capable."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Epson", Model = "TM-T88V (China)",
            Paper = PaperSize.ForProfile(80, 180, 512), Dpi = 180, DefaultCodePage = 0,
            SupportsKanji = true,
            Notes = "GB18030 / GBK capable."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Generic", Model = "58 mm ESC/POS",
            Paper = PaperSize.ForProfile(58, 203, 384), Dpi = 203, DefaultCodePage = 0,
            Notes = "Catch-all 58 mm thermal."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Generic", Model = "80 mm ESC/POS",
            Paper = PaperSize.ForProfile(80, 203), Dpi = 203, DefaultCodePage = 0,
            Notes = "Catch-all 80 mm thermal."
        });
        list.Add(new PrinterProfile
        {
            Manufacturer = "Generic", Model = "112 mm ESC/POS",
            Paper = PaperSize.ForProfile(112, 203, 832), Dpi = 203, DefaultCodePage = 0,
            Notes = "Wide format / kitchen tickets."
        });

        return list;
    }
}
