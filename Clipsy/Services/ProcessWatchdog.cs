using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Clipsy.Services;

public static class ProcessWatchdog
{
    private const int HeartbeatTimeoutSeconds = 90;
    private const int MaxRestartsPerHour = 3;
    private const int CleanExitGraceSeconds = 10;
    private const uint CorFailFast = 0x80131623; // Environment.FailFast
    private static EventWaitHandle? _cleanExit;
    private static EventWaitHandle? _heartbeat;
    private static EventWaitHandle? _restart;
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
            _restart = new EventWaitHandle(false, EventResetMode.ManualReset, RestartEventName(pid));

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

    /// <summary>Call right before ending the process on purpose so the watchdog starts Clipsy again.</summary>
    public static void RequestRestart()
    {
        try { _restart?.Set(); } catch { }
    }

    private static string RestartEventName(int pid) => $"Local\\Clipsy.Restart.{pid}";

    private static int Run(int parentPid, string cleanName, string heartbeatName)
    {
        try
        {
            using var clean = EventWaitHandle.OpenExisting(cleanName);
            using var heartbeat = EventWaitHandle.OpenExisting(heartbeatName);
            using var restartRequested = OpenEvent(RestartEventName(parentPid));
            using var parent = Process.GetProcessById(parentPid);
            // Hold a handle so the exit code is still readable after the parent is gone.
            try { _ = parent.SafeHandle; } catch (Exception ex) { Log($"parent handle failed: {ex.Message}"); }
            int missedHeartbeatSeconds = 0;
            bool killedHung = false;

            while (true)
            {
                if (clean.WaitOne(0)) return FinishCleanExit(parent, parentPid);
                missedHeartbeatSeconds = heartbeat.WaitOne(1000) ? 0 : missedHeartbeatSeconds + 1;
                if (clean.WaitOne(0)) return FinishCleanExit(parent, parentPid);
                if (parent.HasExited) break;

                if (missedHeartbeatSeconds >= HeartbeatTimeoutSeconds)
                {
                    Log($"heartbeat timeout; terminating pid={parentPid}");
                    killedHung = true;
                    try
                    {
                        parent.Kill();
                        parent.WaitForExit(5000);
                    }
                    catch (Exception ex) { Log($"terminate failed: {ex.Message}"); }
                    break;
                }
            }

            if (clean.WaitOne(0)) return FinishCleanExit(parent, parentPid);
            // Ended from Task Manager / taskkill is the user's choice: only crashes, our own hang kill
            // and an explicit request bring Clipsy back.
            bool crashed = IsCrashExit(parent, out string exitCode);
            string? reason = killedHung ? "hang"
                : restartRequested?.WaitOne(0) == true ? "requested"
                : crashed ? "crash" : null;
            if (reason == null)
            {
                Log($"pid={parentPid} ended without a crash (exit code {exitCode}); not restarting");
                return 0;
            }
            if (!AllowRestart()) return 0;
            Thread.Sleep(750);

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return 1;
            Log($"restarting after unexpected exit pid={parentPid} ({reason}, exit code {exitCode})");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            return 0;
        }
        catch (Exception ex)
        {
            Log($"watchdog failed: {ex}");
            return 1;
        }
    }

    private static int FinishCleanExit(Process parent, int parentPid)
    {
        try
        {
            if (!parent.HasExited && !parent.WaitForExit(CleanExitGraceSeconds * 1000))
            {
                Log($"clean exit timeout; terminating pid={parentPid}");
                parent.Kill();
                parent.WaitForExit(5000);
            }
        }
        catch (Exception ex) { Log($"clean exit enforcement failed: {ex.Message}"); }
        return 0;
    }

    // NTSTATUS errors (access violation, fail-fast, stowed and CLR exceptions) are crashes.
    // TerminateProcess from Task Manager / taskkill exits with 1, Process.Kill / Stop-Process with -1.
    private static bool IsCrashExit(Process parent, out string exitCode)
    {
        exitCode = "unknown";
        try
        {
            uint code = (uint)parent.ExitCode;
            exitCode = $"0x{code:X8}";
            return (code >= 0xC0000000 && code != 0xFFFFFFFF) || code == CorFailFast;
        }
        catch { return false; }
    }

    private static EventWaitHandle? OpenEvent(string name)
    {
        try { return EventWaitHandle.OpenExisting(name); }
        catch { return null; }
    }

    // Timestamps of recent restarts, persisted across watchdog generations so a crash loop stops.
    private static bool AllowRestart()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipsy");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "watchdog-restart.txt");
            DateTime now = DateTime.UtcNow;
            var recent = new List<DateTime>();
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (DateTime.TryParse(line, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                        && now - t.ToUniversalTime() < TimeSpan.FromHours(1))
                        recent.Add(t.ToUniversalTime());
                }
            }
            if (recent.Count >= MaxRestartsPerHour)
            {
                Log($"restart suppressed: {recent.Count} restarts in the last hour");
                return false;
            }
            recent.Add(now);
            File.WriteAllLines(path, recent.Select(t => t.ToString("O", CultureInfo.InvariantCulture)));
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
            string path = Path.Combine(dir, "watchdog.log");
            Diagnostics.RotateIfLarge(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
