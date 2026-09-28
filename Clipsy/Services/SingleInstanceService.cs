using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>Named-pipe liveness and command channel, scoped to the current user and session.</summary>
public static class SingleInstanceService
{
    public enum ProbeResult { Alive, Hung, NoAnswer }

    private const int MaxServerInstances = 4;
    private const int HungAfterMs = 30_000;
    private static readonly string PipeName = BuildPipeName();
    private static volatile bool _running;
    private static Func<string, string>? _requestHandler;
    private static long _uiHeartbeat;

    // SID + session in the name: other users can't collide with or squat on it,
    // and each RDP / fast-user-switch session gets its own instance.
    private static string BuildPipeName()
    {
        string user;
        try { user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { user = Environment.UserName; }
        int session = 0;
        try { session = Process.GetCurrentProcess().SessionId; } catch { }
        return $"Clipsy.SingleInstance.v2.{user}.{session}";
    }

    public static void StartServer()
    {
        if (_running) return;
        _running = true;
        new Thread(AcceptLoop) { IsBackground = true, Name = "Clipsy.SingleInstancePipe" }.Start();
    }

    public static void SetRequestHandler(Func<string, string>? handler) => _requestHandler = handler;

    public static void StopServer()
    {
        _running = false;
        _requestHandler = null;
    }

    /// <summary>Called from the UI thread's heartbeat; a stale value makes PING report HUNG.</summary>
    public static void MarkUiAlive() => Interlocked.Exchange(ref _uiHeartbeat, Environment.TickCount64);

    private static void AcceptLoop()
    {
        while (_running)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, MaxServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                server.WaitForConnection();
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleConnectionAsync(connected));
            }
            catch (Exception ex)
            {
                server?.Dispose();
                if (!_running) break;
                Diagnostics.Log($"Single-instance pipe accept failed: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private static async Task HandleConnectionAsync(NamedPipeServerStream server)
    {
        try
        {
            using (server)
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                using var reader = new StreamReader(server, Encoding.UTF8, true, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                var request = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (!_running || string.IsNullOrWhiteSpace(request)) return;

                string response;
                if (request == "PING")
                {
                    long hb = Interlocked.Read(ref _uiHeartbeat);
                    response = hb != 0 && Environment.TickCount64 - hb > HungAfterMs ? "HUNG" : "PONG";
                }
                else
                {
                    var handler = _requestHandler;
                    response = handler != null
                        ? handler(request)
                        : CliService.SerializeResult(new CliResult(3, "Clipsy is starting."));
                }
                await writer.WriteLineAsync(response.AsMemory(), cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex)
        {
            Diagnostics.Log("Single-instance request failed", ex);
        }
    }

    public static bool TryPingExisting() => Probe(out _) == ProbeResult.Alive;

    public static ProbeResult Probe(out int serverPid, int connectTimeoutMs = 1500)
    {
        serverPid = 0;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            client.Connect(connectTimeoutMs);
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid)) serverPid = (int)pid;
            var response = Exchange(client, "PING", 3000);
            return response switch
            {
                "PONG" => ProbeResult.Alive,
                "HUNG" => ProbeResult.Hung,
                // Connected but mute: the pipe thread itself is stuck.
                _ => serverPid != 0 ? ProbeResult.Hung : ProbeResult.NoAnswer,
            };
        }
        catch
        {
            return ProbeResult.NoAnswer;
        }
    }

    public static bool TrySendRequest(string request, out string response, int timeoutMs = 1500)
    {
        response = string.Empty;
        for (int i = 0; i < 2; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                client.Connect(timeoutMs);
                // Pass on our foreground right so windows the request opens (capture overlay) get focus.
                if (GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid)) AllowSetForegroundWindow(pid);
                response = Exchange(client, request, 15_000) ?? string.Empty;
                return response.Length > 0;
            }
            catch { }
        }
        return false;
    }

    private static string? Exchange(NamedPipeClientStream client, string request, int readTimeoutMs)
    {
        using var cts = new CancellationTokenSource(readTimeoutMs);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, true, 1024, leaveOpen: true);
        try
        {
            writer.WriteLineAsync(request.AsMemory(), cts.Token).GetAwaiter().GetResult();
            return reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public static void KillInstance(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!string.Equals(p.ProcessName, "Clipsy", StringComparison.OrdinalIgnoreCase)) return;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Kill instance pid={pid} failed: {ex.Message}");
        }
    }

    public static void KillStaleInstances()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            string? myPath = me.MainModule?.FileName;
            foreach (var p in Process.GetProcessesByName("Clipsy"))
            {
                try
                {
                    if (p.Id == me.Id || p.SessionId != me.SessionId) continue;
                    if (myPath != null && !string.Equals(p.MainModule?.FileName, myPath,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}
