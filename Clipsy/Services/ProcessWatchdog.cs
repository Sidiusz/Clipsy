using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Clipsy.Services;

public static class ProcessWatchdog
{
    private const int HeartbeatTimeoutSeconds = 90;
    private const int RestartLoopGuardSeconds = 20;
    private static EventWaitHandle? _cleanExit;
    private static EventWaitHandle? _heartbeat;
    private static int _started;

    public static bool TryRunWatchdog(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 3 || !string.Equals(args[0], "--watchdog", StringComparison.Ordinal))
            return false;
        if (!int.TryParse(args[1], out int parentPid)) return true;
        exitCode = Run(parentPid, args[2], args.Length > 3 ? args[3] : string.Empty);
        return true;
    }

    public static void StartForCurrentProcess()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            int pid = Environment.ProcessId;
            string cleanName = $"Local\\Clipsy.CleanExit.{pid}";
            string heartbeatName = $"Local\\Clipsy.Heartbeat.{pid}";
            _cleanExit = new EventWaitHandle(false, EventResetMode.ManualReset, cleanName);
            _heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, heartbeatName);

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--watchdog");
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add(cleanName);
            psi.ArgumentList.Add(heartbeatName);
            Process.Start(psi);
        }
        catch (Exception ex) { Log($"start failed: {ex}"); }
    }

    public static void MarkCleanExit()
    {
        try { _cleanExit?.Set(); } catch { }
    }

    public static void Pulse()
    {
        try { _heartbeat?.Set(); } catch { }
    }

    private static int Run(int parentPid, string cleanName, string heartbeatName)
    {
        try
        {
            using var clean = EventWaitHandle.OpenExisting(cleanName);
            using var heartbeat = EventWaitHandle.OpenExisting(heartbeatName);
            using var parent = Process.GetProcessById(parentPid);
            int missedHeartbeatSeconds = 0;

            while (true)
            {
                if (clean.WaitOne(0)) return 0;
                missedHeartbeatSeconds = heartbeat.WaitOne(1000) ? 0 : missedHeartbeatSeconds + 1;
                if (clean.WaitOne(0)) return 0;
                if (parent.HasExited) break;

                if (missedHeartbeatSeconds >= HeartbeatTimeoutSeconds)
                {
                    Log($"heartbeat timeout; terminating pid={parentPid}");
                    try
                    {
                        parent.Kill();
                        parent.WaitForExit(5000);
                    }
                    catch (Exception ex) { Log($"terminate failed: {ex.Message}"); }
                    break;
                }
            }

            if (clean.WaitOne(0)) return 0;
            if (!AllowRestart()) return 0;
            Thread.Sleep(750);

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return 1;
            Log($"restarting after unexpected exit pid={parentPid}");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            return 0;
        }
        catch (Exception ex)
        {
            Log($"watchdog failed: {ex}");
            return 1;
        }
    }

    private static bool AllowRestart()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipsy");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "watchdog-restart.txt");
            DateTime now = DateTime.UtcNow;
            if (File.Exists(path) && DateTime.TryParse(File.ReadAllText(path), out var last)
                && now - last.ToUniversalTime() < TimeSpan.FromSeconds(RestartLoopGuardSeconds))
            {
                Log("restart suppressed by loop guard");
                return false;
            }
            File.WriteAllText(path, now.ToString("O"));
            return true;
        }
        catch { return true; }
    }

    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipsy");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "watchdog.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
