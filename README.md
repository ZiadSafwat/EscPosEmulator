# ESC/POS Thermal Printer Emulator — v3

> Emulate ESC/POS thermal printers on Windows — with full Arabic (RTL) support.

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Avalonia](https://img.shields.io/badge/Avalonia-Desktop-4513D1?logo=avaloniadesktop&logoColor=white)](https://avaloniaui.net/)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows&logoColor=white)](https://www.microsoft.com/en-us/windows)
[![License](https://img.shields.io/badge/License-MIT-yellow.svg)](#-license)

A Windows desktop app (.NET 8 + Avalonia) that emulates an ESC/POS thermal receipt
printer: it installs a **real, named Windows printer** (e.g. “test”), and RAW bytes
that *any* application sends to that printer show up live in a receipt preview.

Perfect for testing POS integrations — Arabic receipts render correctly:
connected letters, RTL ordering, and the driver's own raster images (`GS v 0`).

## Arabic (RTL) support

Arabic is the reason most emulators fail. This one handles the full pipeline:

- **Code pages** — Windows-1256 (`ESC t 27`), CP720 (`ESC t 30`), Hebrew (26),
  CP437/850/866, and UTF-8 (auto-detected even when a legacy table is selected).
- **Shape & order** — HarfBuzz contextual shaping for connected Arabic letters,
  a bidi pass for RTL runs, embedded Latin/numbers kept LTR, and an
  Arabic-capable font (Segoe UI / Tahoma / Noto Naskh Arabic).
- **Full line buffering** — text is decoded per *line*, so TCP chunk splits in the
  middle of a word (or inside a multi-byte UTF-8 sequence) never corrupt it.
- **Raster fallback** — GDI/Windows printer drivers (Xprinter, Epson, …) often
  rasterize the whole page with `GS v 0` bitmap bands; those are decoded and the
  bands are merged into one continuous image, so driver-mangled text still shows
  exactly what the driver produced.

> **Tip:** if your POS app prints Arabic as literal dots (`.`), you are printing
> through the *Generic / Text Only* virtual printer — it strips non-CP437
> characters in the Windows spooler. Point the app at a printer created with a
> real ESC/POS driver (e.g. Xprinter XP-80) instead.


## How it works

```
POS app / .NET / Excel / WinSpool
        │  prints to "test" (Windows printer, "Generic / Text Only")
        ▼
Windows spooler ── RAW socket ──▶ 127.0.0.1:9100
                                       │
                                TcpPrinterServer
                                       │  parser.Feed(bytes)
                                       ▼
                               EscPosParser (thread-safe)
                                       │
                               ReceiptDocument ──▶ Skia render ──▶ live preview
```

- **One TCP connection = one print job.** Connect = fresh receipt (new paper);
  disconnect = job finished.
- Defaults to loopback (`127.0.0.1`). A “LAN” checkbox switches the listener to
  `0.0.0.0` (needs a stop/start to apply).
- Install / rename / remove of the virtual printer run elevated (one UAC prompt);
  the app itself is asInvoker.
- **Rename** = remove + install inside a single elevated call.
- Remove refuses to touch a printer the tool did not create.

## Feature set (v2 kept, plus)

- 30+ hardware profiles (Epson, Star, Citizen, Bixolon, Xprinter, …) with paper
  width, DPI, cutter, drawer kick, QR/raster capability, code-page defaults.
- Code-page switching (CP437, CP850, CP866, UTF-8, …) — bytes are decoded with the
  active table.
- **Arabic receipts**: Windows-1256 (ESC t 27), CP720 (ESC t 30) and UTF-8 are
  supported. UTF-8 is auto-detected even when a legacy table is selected, text is
  shaped with HarfBuzz (connected Arabic letters, correct RTL order, numbers kept
  LTR), and an Arabic-capable system font (Segoe UI / Tahoma / Noto Naskh Arabic)
  is used instead of the Latin monospace font. Use **Send Arabic Demo** to test.
- Paper sizes + custom size; zoom in/out/reset on the preview.
- QR codes (`GS ( k`), barcodes (`GS k`, CODE39/EAN13/EAN8/CODE128/UPCA/UPCE),
  raster images (`GS v 0` bands and `GS ( l 0x38`; consecutive GDI driver bands
  are merged into one continuous bitmap), text formatting (bold, underline,
  size, align, invert), drawer-kick detection (`ESC p`), cut marks.
- Forward the rendered receipt to any real system printer (thermal, PDF, …).
- Activity log, job counter, drawer-kick counter, status badge.
- Settings persisted in `%LOCALAPPDATA%\EscPosEmulator\settings.json`.

## Build & run

```powershell
dotnet build EscPosEmulator.sln
dotnet run --project src\EscPosEmulator
```

Requires Windows (virtual printer + system-printer features are Windows-only;
the emulated preview itself works anywhere).

## Smoke test

1. Start the server (or install a virtual printer — with **AutoStartOnInstall**
   the listener starts automatically).
2. Print to “test” from any app, or send raw bytes: `powershell` + TCP to
   `127.0.0.1:9100`, or just click **Send Demo Receipt** (initialises, centered
   bold title, lines, total, QR via `GS ( k`, then full cut).

## Layout

```
src/EscPosEmulator/
  Program.cs, App.axaml(.cs), app.manifest        bootstrap, asInvoker
  Core/EncodingHelper.cs                          code-page encodings
  Models/                                         paper, profiles, states, receipt
  Parsing/EscPosParser.cs(+.Commands.cs)          thread-safe ESC/POS parser
  Rendering/ReceiptRenderer.cs                    Skia 2-pass render
  Services/TcpPrinterServer.cs                    RAW TCP server (1 conn = 1 job)
  Services/VirtualPrinterService.cs               install/rename/remove (UAC)
  Services/PrintService.cs                        forward receipt to real printer
  Services/AppSettings.cs                         %LOCALAPPDATA% settings.json
  ViewModels/MainViewModel.cs                     MVVM orchestration
  Views/MainWindow.axaml(.cs)                     UI
```

---

## 🧑‍💻 Author

**Ziad El-Araby**
> Full-Stack Dotnet Developer | Flutter & Angular Enthusiast

- 📧 **Email:** [Ziadsafwataraby@gmail.com](mailto:Ziadsafwataraby@gmail.com)
- 🔗 **Website:** https://ziadsafwat.github.io/Ziad-Safwat-Portfolio/
- 🔗 **GitHub:** [@ZiadSafwat](https://github.com/ZiadSafwat)

## 📜 License

This project is licensed under the [MIT License](LICENSE).

## ⭐ Support the Project

If you like **ESC/POS Thermal Printer Emulator**, please consider giving it a star ⭐ on GitHub!
Or support development [👉 ☕ Buy Me a Coffee](https://bmc.link/ziadsafwat1).
