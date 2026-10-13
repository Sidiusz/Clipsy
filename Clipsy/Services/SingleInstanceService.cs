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
    /// <summary>Busy: alive and recording, so it must never be replaced.</summary>
    public enum ProbeResult { Alive, Busy, Hung, NoAnswer }

    private const int MaxServerInstances = 4;
    private const int HungAfterMs = 30_000;
    private const int PingTimeoutMs = 5000;
    private const int RequestReadTimeoutMs = 10_000;
    private const uint THREAD_TERMINATE = 0x0001;
    // Settable so tests don't talk to a Clipsy running on the same machine.
    internal static string PipeName { get; set; } = BuildPipeName();
    private static volatile bool _running;
    private static Func<string, string>? _requestHandler;
    private static Func<bool>? _busyCheck;
    private static long _uiHeartbeat;
    private static EventWaitHandle? _quitEvent;
    private static RegisteredWaitHandle? _quitWait;

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
        StartQuitListener();
    }

    public static void SetRequestHandler(Func<string, string>? handler) => _requestHandler = handler;

    /// <summary>True while something that must not be killed (a recording) is in progress; PING then answers BUSY.</summary>
    public static void SetBusyCheck(Func<bool>? check) => _busyCheck = check;

    public static void StopServer()
    {
        _running = false;
        _requestHandler = null;
        _busyCheck = null;
        _quitWait?.Unregister(null);
        _quitWait = null;
    }

    /// <summary>Called from the UI thread's heartbeat; a stale value makes PING report HUNG.</summary>
    public static void MarkUiAlive() => Interlocked.Exchange(ref _uiHeartbeat, UnbiasedMilliseconds());

    // Excludes time spent asleep / hibernated, so the heartbeat isn't stale right after resume.
    private static long UnbiasedMilliseconds()
    {
        QueryUnbiasedInterruptTime(out ulong time);
        return (long)(time / 10_000);
    }

    // An elevated uninstaller fails the pipe's owner check, but can still signal this
    // event (same user, same session) to ask for a clean quit.
    private static string QuitEventName => $"Local\\{PipeName}.Quit";

    private static void StartQuitListener()
    {
        try
        {
            _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) =>
            {
                if (_running) _requestHandler?.Invoke(CliService.SerializeRequest("quit", Array.Empty<string>()));
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) { Diagnostics.Log($"Quit event setup failed: {ex.Message}"); }
    }

    /// <summary>Signals a running instance to quit without the pipe; false when none is listening.</summary>
    public static bool TrySignalQuit()
    {
        try
        {
            using var quit = EventWaitHandle.OpenExisting(QuitEventName);
            return quit.Set();
        }
        catch { return false; }
    }

    private static void AcceptLoop()
    {
        while (_running)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Synchronous I/O on dedicated threads: async pipe completions and Task.Run both need
                // the thread pool, and a starved pool must never make a live instance look hung.
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, MaxServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                server.WaitForConnection();
                var connected = server;
                server = null;
                new Thread(() => HandleConnection(connected)) { IsBackground = true, Name = "Clipsy.SingleInstanceRequest" }.Start();
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

    // A client that connects and never sends a line must not hold one of the few pipe
    // instances forever (new launches would then find no answer); abort the read after a while.
    private static string? ReadRequestLine(StreamReader reader)
    {
        IntPtr thread = OpenThread(THREAD_TERMINATE, false, GetCurrentThreadId());
        if (thread == IntPtr.Zero) return reader.ReadLine();
        try
        {
            using var timer = new Timer(_ => CancelSynchronousIo(thread), null, RequestReadTimeoutMs, Timeout.Infinite);
            try { return reader.ReadLine(); }
            finally
            {
                // Wait out a callback already running before the handle is closed.
                using var done = new ManualResetEvent(false);
                if (timer.Dispose(done)) done.WaitOne();
            }
        }
        finally { CloseHandle(thread); }
    }

    // Clients close their end when they give up, which ends a blocked read here.
    private static void HandleConnection(NamedPipeServerStream server)
    {
        try
        {
            using (server)
            {
                using var reader = new StreamReader(server, Encoding.UTF8, true, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                var request = ReadRequestLine(reader);
                if (!_running || string.IsNullOrWhiteSpace(request)) return;

                string response;
                if (request == "PING")
                {
                    long hb = Interlocked.Read(ref _uiHeartbeat);
                    response = _busyCheck?.Invoke() == true ? "BUSY"
                        : hb != 0 && UnbiasedMilliseconds() - hb > HungAfterMs ? "HUNG"
                        : "PONG";
                }
                else
                {
                    var handler = _requestHandler;
                    response = handler != null
                        ? handler(request)
                        : CliService.SerializeResult(new CliResult(3, "Clipsy is starting."));
                }
                writer.WriteLine(response);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex)
        {
            Diagnostics.Log("Single-instance request failed", ex);
        }
    }

    public static bool TryPingExisting() => Probe(out _) is ProbeResult.Alive or ProbeResult.Busy;

    public static ProbeResult Probe(out int serverPid, int connectTimeoutMs = 1500)
    {
        serverPid = 0;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            client.Connect(connectTimeoutMs);
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid)) serverPid = (int)pid;
            var response = Exchange(client, "PING", PingTimeoutMs);
            return response switch
            {
                "PONG" => ProbeResult.Alive,
                "BUSY" => ProbeResult.Busy,
                "HUNG" => ProbeResult.Hung,
                // Connected but mute is not proof of a hang (heavy load, slow start): never kill on it.
                _ => ProbeResult.NoAnswer,
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

    [DllImport("kernel32.dll")]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelSynchronousIo(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
