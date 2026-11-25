using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EscPosEmulator.Parsing;

namespace EscPosEmulator.Services;

/// <summary>
/// RAW ESC/POS TCP server — what the Windows virtual printer (or a POS SDK in
/// "network printer" mode) talks to.
///
/// One TCP connection = one print job:
///   • connect   → parser.NewJob()   (fresh receipt, like new paper)
///   • data      → parser.Feed(...)
///   • disconnect→ job finished
///
/// Starts cleanly, stops cleanly: the accept loop and every open client are
/// tracked and drained, so shutdown never touches a disposed socket.
/// </summary>
public sealed class TcpPrinterServer : IDisposable
{
    /// <summary>Disconnect a client that sends nothing for this long (hang guard).</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Raw job capture folder (diagnostic: every received job is saved here).</summary>
    private static string JobCaptureDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "EscPosEmulator", "jobs");

    private static readonly object CaptureGate = new();
    private static int _captureSeq;

    private readonly EscPosParser _parser;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly List<TcpClient> _clients = [];
    private readonly List<Task> _clientTasks = [];
    private readonly object _clientGate = new();

    public int Port { get; private set; }
    public bool ListenOnLan { get; }
    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Stopped";
    public string? LastError { get; private set; }

    public event Action? StatusChanged;
    public event Action<string>? Log;
    public event Action? JobCompleted;

    private void EmitLog(string message) => Log?.Invoke(message);

    public TcpPrinterServer(EscPosParser parser, bool listenOnLan = false)
    {
        _parser = parser;
        ListenOnLan = listenOnLan;
        _parser.JobStarted += () => EmitLog("New print job started (new connection).");
    }

    /// <summary>True when a local TCP probe to the own port succeeds.</summary>
    public bool SelfProbe()
    {
        if (!IsRunning) return false;
        try
        {
            using var probe = new TcpClient();
            var t = probe.ConnectAsync(IPAddress.Loopback, Port);
            return t.Wait(TimeSpan.FromSeconds(1)) && probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts the listener. Returns false (and sets <see cref="LastError"/>) on failure.</summary>
    public async Task<bool> StartAsync(int port)
    {
        if (IsRunning) return true;
        LastError = null;

        IPAddress bind = ListenOnLan ? IPAddress.Any : IPAddress.Loopback;

        TcpListener listener;
        try
        {
            _cts = new CancellationTokenSource();
            listener = new TcpListener(bind, port);
            listener.Start();
        }
        catch (Exception ex)
        {
            _cts?.Dispose();
            _cts = null;
            // WSA 10048 (EADDRINUSE) surfaces with locale-dependent text:
            // ".NET: in use", Winsock native "Only one usage of each socket
            // address (protocol/network address/port) is normally permitted",
            // or the error number itself. Probe all three variants because the
            // local reference pack has a reduced System.Net.Sockets surface.
            string msg = ex.Message ?? string.Empty;
            bool inUse = msg.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
                         msg.Contains("Only one usage of each socket address",
                                      StringComparison.OrdinalIgnoreCase) ||
                         msg.Contains("10048", StringComparison.Ordinal);
            LastError = inUse
                ? $"Port {port} is already in use by another program."
                : msg;
            Status = "Stopped";
            StatusChanged?.Invoke();
            EmitLog($"Listener failed to start: {LastError}");
            return false;
        }

        _listener = listener;
        Port = port;
        IsRunning = true;
        Status = ListenOnLan
            ? $"Listening on 0.0.0.0:{port} (LAN)"
            : $"Listening on 127.0.0.1:{port}";
        StatusChanged?.Invoke();
        EmitLog($"TCP server started — {Status}");

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        await Task.CompletedTask;
        return true;
    }

    /// <summary>Stops the listener and waits (≤ 3 s) for clients to finish.</summary>
    public void Stop()
    {
        if (!IsRunning) return;

        Task? acceptTask;
        Task[] clientTasks;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* already stopped */ }

        lock (_clientGate)
        {
            foreach (var c in _clients)
                SafeClose(c);                 // unblock all pending reads
            acceptTask = _acceptTask;
            clientTasks = _clientTasks.ToArray();
        }

        var all = new List<Task>(clientTasks.Length + 1);
        all.Add(acceptTask ?? Task.CompletedTask);
        all.AddRange(clientTasks);
        Task.WaitAll(all.ToArray(), TimeSpan.FromSeconds(3));

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptTask = null;
        IsRunning = false;
        Status = "Stopped";
        StatusChanged?.Invoke();
        EmitLog("TCP server stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                EmitLog($"Client connected from {DescribeEndpoint(client)}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;                       // listener stopped
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(250, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            // The handler runs until the connection closes (normal) or Stop()
            // closes the socket. We track the task so Stop() can drain it.
            var task = Task.Run(() => HandleClientAsync(client));
            lock (_clientGate)
            {
                _clients.Add(client);
                _clientTasks.Add(task);
            }
            _ = task.ContinueWith(_ =>
            {
                lock (_clientGate)
                {
                    _clients.Remove(client);
                    _clientTasks.Remove(task);
                }
            }, TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        int total = 0;
        var capture = new MemoryStream();
        try
        {
            // new spooled connection = new print job
            _parser.NewJob();

            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];

                while (!ctStopped() && client.Connected)
                {
                    // read with an idle timeout so a half-open spooler cannot hang a thread
                    Task<int> readTask = stream.ReadAsync(buffer).AsTask();
                    Task timeout = Task.Delay(IdleTimeout);
                    Task done = await Task.WhenAny(readTask, timeout);

                    if (done == timeout)
                    {
                        EmitLog("Client idle for 60 s — closing connection (job treated as finished).");
                        break;
                    }

                    int n;
                    try { n = await readTask; }                    // surfaces socket errors
                    catch (Exception ex)
                    {
                        EmitLog($"Read error: {ex.Message}");
                        break;
                    }

                    if (n == 0) break;          // normal close → job finished

                    capture.Write(buffer, 0, n);
                    _parser.Feed(buffer.AsSpan(0, n));
                    total += n;
                }
            }
        }
        catch (Exception ex)
        {
            EmitLog($"Client error: {ex.Message}");
        }
        finally
        {
            // A receipt may end without a trailing LF — print the last line.
            if (total > 0) _parser.EndJob();

            lock (_clientGate) _clients.Remove(client);
            if (total > 0)
                EmitLog($"Job finished — {total} bytes received.");
            else
                EmitLog("Client disconnected (no data).");
            try { CaptureJob(capture.ToArray(), client); }
            catch (Exception ex) { EmitLog($"Job capture failed: {ex.Message}"); }
            JobCompleted?.Invoke();
        }
    }

    private bool ctStopped() => _cts == null || _cts.IsCancellationRequested;

    /// <summary>
    /// Saves the raw bytes of the finished job for diagnostics:
    /// %LOCALAPPDATA%\EscPosEmulator\jobs\job-<n>-<timestamp>.bin (+ a .txt annotation).
    /// </summary>
    private void CaptureJob(byte[] data, TcpClient client)
    {
        if (data.Length == 0) return;

        Directory.CreateDirectory(JobCaptureDir);
        lock (CaptureGate) _captureSeq++;
        int seq = _captureSeq;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        string binPath = Path.Combine(JobCaptureDir, $"job-{seq}-{stamp}.bin");
        File.WriteAllBytes(binPath, data);

        var sb = new StringBuilder();
        sb.AppendLine($"job {seq}  {DateTime.Now:O}  {data.Length} bytes  from {DescribeEndpoint(client)}");
        sb.AppendLine();
        sb.Append(ByteHexDump(data));
        var txtPath = Path.Combine(JobCaptureDir, $"job-{seq}-{stamp}.txt");
        File.WriteAllText(txtPath, sb.ToString());

        EmitLog($"Job captured → {binPath}");
    }

    private static string DescribeEndpoint(TcpClient c)
    {
        try
        {
            var ep = c.Client.RemoteEndPoint as IPEndPoint;
            if (ep == null) return "unknown";
            return IPAddress.IsLoopback(ep.Address) ? $"127.0.0.1 (local spooler)" : $"{ep.Address}:{ep.Port}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void SafeClose(TcpClient c)
    {
        try { c.Close(); } catch { /* ignore */ }
    }

    /// <summary>Classic 16-bytes-per-line hex dump with a printable-text column.</summary>
    private static string ByteHexDump(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 8 + 64);
        int perLine = 16;
        for (int i = 0; i < data.Length; i += perLine)
        {
            sb.Append(((uint)i).ToString("X8").PadLeft(8));
            sb.Append("  ");

            for (int j = 0; j < perLine; j++)
            {
                if (i + j < data.Length)
                    sb.Append(data[i + j].ToString("X2")).Append(' ');
                else
                    sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append("| ");

            for (int j = 0; j < perLine && i + j < data.Length; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public void Dispose() => Stop();
}
