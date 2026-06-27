using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace Clipsy.Services;

/// <summary>Named-pipe liveness handshake so a second launch can tell a healthy
/// running instance (hand off, exit) from a hung one (kill it, take over).</summary>
public static class SingleInstanceService
{
    private const string PipeName = "Clipsy.SingleInstance.Pipe.v1";
    private static volatile bool _running;

    public static void StartServer()
    {
        if (_running) return;
        _running = true;
        new Thread(ServerLoop) { IsBackground = true, Name = "Clipsy.SingleInstancePipe" }.Start();
    }

    private static void ServerLoop()
    {
        while (_running)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();
                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };
                if (reader.ReadLine() == "PING") writer.WriteLine("PONG");
            }
            catch { /* recycle the server on any error */ }
        }
    }

    /// <summary>True if an existing instance answered — caller should exit.</summary>
    public static bool TryPingExisting()
    {
        // Two attempts to avoid a false negative during the server's recycle gap.
        for (int i = 0; i < 2; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(1500);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                using var reader = new StreamReader(client);
                writer.WriteLine("PING");
                if (reader.ReadLine() == "PONG") return true;
            }
            catch { /* no server / timeout → try again, then treat as hung */ }
        }
        return false;
    }

    /// <summary>Kill stale Clipsy processes from the same image (not us).</summary>
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
                    if (p.Id == me.Id) continue;
                    if (myPath != null && !string.Equals(p.MainModule?.FileName, myPath,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
                catch { /* already gone or access denied */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* best effort */ }
    }
}
