using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Clipsy.Services;

/// <summary>Named-pipe liveness and command channel for the current user session.</summary>
public static class SingleInstanceService
{
    private const string PipeName = "Clipsy.SingleInstance.Pipe.v1";
    private static volatile bool _running;
    private static Func<string, string>? _requestHandler;

    public static void StartServer(Func<string, string>? requestHandler = null)
    {
        _requestHandler = requestHandler;
        if (_running) return;
        _running = true;
        new Thread(ServerLoop) { IsBackground = true, Name = "Clipsy.SingleInstancePipe" }.Start();
    }

    private static void ServerLoop()
    {
        while (_running)
        {
            try            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                server.WaitForConnection();
                using var reader = new StreamReader(server, Encoding.UTF8, true, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                };
                var request = reader.ReadLine();
                if (request == "PING")
                {
                    writer.WriteLine("PONG");
                }
                else if (!string.IsNullOrWhiteSpace(request) && _requestHandler != null)
                {
                    writer.WriteLine(_requestHandler(request));
                }
            }
            catch { }
        }
    }

    public static bool TryPingExisting()
        => TrySendRequest("PING", out var response) && response == "PONG";

    public static bool TrySendRequest(string request, out string response, int timeoutMs = 1500)
    {        response = string.Empty;
        for (int i = 0; i < 2; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(timeoutMs);
                using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                };
                using var reader = new StreamReader(client, Encoding.UTF8, true, 1024, leaveOpen: true);
                writer.WriteLine(request);
                response = reader.ReadLine() ?? string.Empty;
                return response.Length > 0;
            }
            catch { }
        }
        return false;
    }

    public static void KillStaleInstances()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            string? myPath = me.MainModule?.FileName;
            foreach (var p in Process.GetProcessesByName("Clipsy"))
            {                try
                {
                    if (p.Id == me.Id) continue;
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
}
