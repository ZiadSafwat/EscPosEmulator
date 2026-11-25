using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace EscPosEmulator.Services;

/// <summary>
/// Creates / renames / removes a real Windows printer (visible in
/// Settings → Printers) with ANY name you choose. Any app that prints RAW to
/// that printer name ends up in the emulator.
///
/// How it works (same trick used by every "virtual ESC/POS printer"):
///   Windows printer "test"
///        └─ driver: "Generic / Text Only" (inbox driver, passes RAW bytes through)
///        └─ port:   Standard TCP/IP  127.0.0.1:9100 (RAW)
///                       └─ TcpPrinterServer inside this app
///
/// Creating/renaming/removing a printer needs admin rights, so only those
/// steps are elevated (UAC prompt) — the app itself stays a normal user.
/// </summary>
public sealed class VirtualPrinterService
{
    public const string DriverName = "Generic / Text Only";
    private const string PortPrefix = "EscPosEmu_";

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string PortNameFor(int port) => $"{PortPrefix}127.0.0.1_{port}";

    /// <summary>
    /// All printer driver names installed on the machine ("Printer Drivers"
    /// under the spooler). Best-effort; empty list when unavailable.
    /// </summary>
    public static IReadOnlyList<string> GetInstalledDrivers()
    {
        if (!IsWindows) return Array.Empty<string>();
        try
        {
            Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\PrintDrivers\amd64\3")
                ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\PrintDrivers\x64\3");
            if (key == null) return Array.Empty<string>();

            string[] names;
            using (key)
            {
                names = key.GetSubKeyNames();
            }
            return names
                   .Where(n => n != "DriverList")
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                   .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>True when a printer driver with this exact name is installed.</summary>
    public static bool DriverExists(string? driver)
    {
        if (!IsWindows) return false;
        return GetInstalledDrivers().Any(d =>
            string.Equals(d, driver, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolve the driver to actually install: the requested one when it is
    /// present on the machine, otherwise fall back to the always-present
    /// inbox "Generic / Text Only" (which passes raw bytes through).
    /// </summary>
    public static string ResolveDriver(string? requested) =>
        !string.IsNullOrWhiteSpace(requested) && DriverExists(requested)
            ? requested!.Trim()
            : DriverName;

    /// <summary>Returns an error message, or null when the name is acceptable.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a printer name first.";
        if (name != name.Trim())
            return "Printer name cannot start or end with a space.";
        if (name.Length > 200)
            return "Printer name is too long.";
        if (name.IndexOfAny(new[] { '\\', '/', ',', '!' }) >= 0)
            return "Printer name cannot contain  \\  /  ,  !";
        if (name.Any(char.IsControl))
            return "Printer name contains invalid characters.";
        return null;
    }

    public bool Exists(string? name)
    {
        if (!IsWindows || string.IsNullOrWhiteSpace(name)) return false;
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
    /// True when <paramref name="name"/> exists AND uses one of our
    /// EscPosEmu_* ports — i.e. it was created by this tool.
    /// </summary>
    public string? GetOurs(string? name)
    {
        if (!IsWindows || string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            // The spooler keeps every installed printer under
            // HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\<name> — read-only.
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{name.Trim()}");
            var port = key?.GetValue("PortName") as string;
            if (port != null && port.StartsWith(PortPrefix, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>
    /// The driver a printer currently uses, read from the spooler registry
    /// (read-only, no elevation). Null when unknown.
    /// </summary>
    public static string? ExistingDriverName(string? name)
    {
        if (!IsWindows || string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{name.Trim()}");
            return (key?.GetValue("Printer Driver")  as string)
                ?? (key?.GetValue("DriverName")      as string);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Can TCP reach 127.0.0.1:port right now? (Is the emulator listening?)</summary>
    public static bool ServerReachable(int port, int timeoutMs = 1500)
    {
        try
        {
            using var probe = new TcpClient();
            var t = probe.ConnectAsync(IPAddress.Loopback, port);
            return t.Wait(TimeSpan.FromMilliseconds(timeoutMs)) && probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Ok, string Message)> InstallAsync(string name, int port, string? driver = null)
    {
        if (!IsWindows)
            return (false, "Virtual system printers are only supported on Windows.");
        var err = ValidateName(name);
        if (err != null) return (false, err);
        if (port is < 1 or > 65535) return (false, "Invalid port.");
        string useDriver = ResolveDriver(driver);

        string script = $@"
$ErrorActionPreference = 'Stop'
$name     = {PsQuote(name)}
$portName = {PsQuote(PortNameFor(port))}
$driver   = {PsQuote(useDriver)}

# 1. make sure the TCP/IP port exists
if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {{
    Add-PrinterPort -Name $portName -PrinterHostAddress '127.0.0.1' -PortNumber {port}
}}

# 2. make sure the driver exists (inbox driver on Windows)
if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {{
    Add-PrinterDriver -Name $driver
}}

# 3. create or update the printer (rebuild when the driver changed)
$existing = Get-Printer -Name $name -ErrorAction SilentlyContinue
if ($existing) {{
    if ($existing.PortName -notlike '{PortPrefix}*') {{
        throw ""A different printer named '$name' already exists (not created by this tool). Pick another name.""
    }}
    if ($existing.DriverName -eq $driver) {{
        Set-Printer -Name $name -PortName $portName
    }} else {{
        Remove-Printer -Name $name
        Add-Printer -Name $name -DriverName $driver -PortName $portName
    }}
}} else {{
    Add-Printer -Name $name -DriverName $driver -PortName $portName
}}
";

        var (ok, msg) = await RunElevatedAsync(script);
        if (ok && !Exists(name))
            return (false, "The install finished, but Windows does not list the printer. Check the Printer Settings.");

        return ok
            ? (true, $"Printer '{name}' installed → 127.0.0.1:{port}")
            : (false, msg);
    }

    /// <summary>
    /// Re-attach a different driver to an already-installed virtual printer
    /// (remove + re-add inside ONE elevated call, keeping name and port).
    /// </summary>
    public async Task<(bool Ok, string Message)> SetDriverAsync(string name, int port, string? driver = null)
    {
        if (!IsWindows)
            return (false, "Virtual system printers are only supported on Windows.");
        string useDriver = ResolveDriver(driver);

        string script = $@"
$ErrorActionPreference = 'Stop'
$name     = {PsQuote(name)}
$portName = {PsQuote(PortNameFor(port))}
$driver   = {PsQuote(useDriver)}

$p = Get-Printer -Name $name -ErrorAction SilentlyContinue
if (-not $p) {{
    throw ""Printer '$name' was not found.""
}}
if ($p.PortName -notlike '{PortPrefix}*') {{
    throw ""'$name' was not created by this tool - refusing to touch it.""
}}
if ($p.DriverName -ne $driver) {{
    Remove-Printer -Name $name
    Add-Printer -Name $name -DriverName $driver -PortName $portName
}}
";

        var (ok, msg) = await RunElevatedAsync(script);
        if (ok && !Exists(name))
            return (false, "The driver change finished, but Windows does not list the printer anymore.");

        return ok
            ? (true, $"Printer '{name}' now uses driver '{useDriver}'.")
            : (false, msg);
    }

    /// <summary>
    /// Rename = remove old + create new in ONE elevated call.
    /// Old name may not exist (then it is just an install).
    /// </summary>
    public async Task<(bool Ok, string Message)> RenameAsync(string? oldName, string newName, int port,
                                                              string? driver = null)
    {
        if (!IsWindows)
            return (false, "Virtual system printers are only supported on Windows.");
        var err = ValidateName(newName);
        if (err != null) return (false, err);
        if (port is < 1 or > 65535) return (false, "Invalid port.");
        if (string.IsNullOrWhiteSpace(oldName))
            return await InstallAsync(newName, port, driver);
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            return (true, "Printer already has that name.");
        string useDriver = ResolveDriver(driver);

        string script = $@"
$ErrorActionPreference = 'Stop'
$old    = {PsQuote(oldName)}
$name   = {PsQuote(newName)}
$portName = {PsQuote(PortNameFor(port))}
$driver   = {PsQuote(useDriver)}

if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {{
    Add-PrinterPort -Name $portName -PrinterHostAddress '127.0.0.1' -PortNumber {port}
}}
if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {{
    Add-PrinterDriver -Name $driver
}}

$conflict = Get-Printer -Name $name -ErrorAction SilentlyContinue
if ($conflict -and $conflict.PortName -notlike '{PortPrefix}*') {{
    throw ""A different printer named '$name' already exists (not created by this tool).""
}}
if ($conflict) {{ Remove-Printer -Name $name }}

$oldPrinter = Get-Printer -Name $old -ErrorAction SilentlyContinue
if ($oldPrinter) {{
    if ($oldPrinter.PortName -notlike '{PortPrefix}*') {{
        throw ""'$old' was not created by this tool - refusing to touch it.""
    }}
    Remove-Printer -Name $old
}}

Add-Printer -Name $name -DriverName $driver -PortName $portName
";

        var (ok, msg) = await RunElevatedAsync(script);
        if (ok && !Exists(newName))
            return (false, "The rename finished, but Windows does not list the printer.");
        return ok
            ? (true, $"Printer renamed: '{oldName}' → '{newName}' (127.0.0.1:{port})")
            : (false, msg);
    }

    public async Task<(bool Ok, string Message)> RemoveAsync(string name, int port)
    {
        if (!IsWindows)
            return (false, "Virtual system printers are only supported on Windows.");
        var err = ValidateName(name);
        if (err != null) return (false, err);

        string script = $@"
$ErrorActionPreference = 'Stop'
$name = {PsQuote(name)}

$p = Get-Printer -Name $name -ErrorAction SilentlyContinue
if ($p) {{
    if ($p.PortName -notlike '{PortPrefix}*') {{
        throw ""'$name' was not created by this tool - refusing to remove it.""
    }}
    $portName = $p.PortName
    Remove-Printer -Name $name

    # drop the port too when nothing else uses it
    $stillUsed = Get-Printer | Where-Object {{ $_.PortName -eq $portName }}
    if (-not $stillUsed) {{
        Remove-PrinterPort -Name $portName -ErrorAction SilentlyContinue
    }}
}}
";

        var (ok, msg) = await RunElevatedAsync(script);
        if (ok && Exists(name))
            return (false, "Windows still lists the printer (it may be in use). Close apps using it and retry.");

        return ok ? (true, $"Printer '{name}' removed.") : (false, msg);
    }

    // ------------------------------------------------------------------

    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Runs a PowerShell script elevated (UAC). The elevated process cannot
    /// stream output back to us, so on failure it writes the message to a
    /// temp log file which we read back.
    /// </summary>
    private static async Task<(bool Ok, string Message)> RunElevatedAsync(string body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "EscPosEmulator");
        Directory.CreateDirectory(dir);
        string id = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(dir, $"vp_{id}.ps1");
        string logPath = Path.Combine(dir, $"vp_{id}.log");

        string full = $@"
try {{
{body}
    exit 0
}} catch {{
    $_.Exception.Message | Out-File -FilePath {PsQuote(logPath)} -Encoding utf8
    exit 1
}}
";

        // UTF-8 *with BOM*: Windows PowerShell 5.1 only reads non-ASCII
        // (e.g. Arabic printer names) correctly with the BOM prefix.
        await File.WriteAllTextAsync(scriptPath, full, new UTF8Encoding(true));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,   // required for Verb = runas
                Verb = "runas",           // UAC elevation prompt
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Could not start PowerShell.");

            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0) return (true, "OK");

            string detail = File.Exists(logPath)
                ? (await File.ReadAllTextAsync(logPath)).Trim()
                : $"PowerShell exited with code {proc.ExitCode}.";
            return (false, detail);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Administrator permission was declined (UAC).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            // best-effort cleanup of the temp script / log
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
        }
    }
}
