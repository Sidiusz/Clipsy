using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace Clipsy.Services;

/// <summary>Per-user sign-in autostart via the HKCU Run key.</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Clipsy";
    private const string LegacyTaskName = "ClipsyAutostart";
    private const string SettingsKey = @"Software\Clipsy";
    private const string OptOutValue = "AutostartOptOut";
    private const string MigrationDeclinedValue = "LegacyTaskElevationDeclined";
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string TaskCacheTreeKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\" + LegacyTaskName;
    private static readonly object _sync = new();
    private static bool _legacyChecked;
    private static bool _legacyPresent;

    public static bool IsEnabled()
    {
        try { return (HasRunEntry() && !IsDisabledInTaskManager()) || LegacyTaskPresentNoWait(); }
        catch (Exception ex) { Diagnostics.Log("AutostartService.IsEnabled", ex); return false; }
    }

    /// <summary>Off the UI thread: drops a stale Run entry and removes the legacy scheduled task (UAC at most once).</summary>
    public static void MigrateLegacyScheduledTaskInBackground()
        => System.Threading.Tasks.Task.Run(() =>
        {
            RemoveStaleRunEntry();
            MigrateLegacyScheduledTask();
        });

    // An uninstall run under another admin account can leave this user's Run entry pointing
    // at a deleted exe; drop it so sign-in doesn't try to start nothing.
    private static void RemoveStaleRunEntry()
    {
        try
        {
            string? path;
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                path = (key?.GetValue(AppName) as string)?.Trim().Trim('"');
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || File.Exists(path))
                return;
            Diagnostics.Log($"AutostartService: removing Run entry for missing '{path}'");
            DeleteRunEntry();
        }
        catch (Exception ex) { Diagnostics.Log("AutostartService.RemoveStaleRunEntry", ex); }
    }

    private static void MigrateLegacyScheduledTask()
    {
        try
        {
            if (!CachedLegacyTaskExists()) return;
            bool optedOut = IsOptedOut();
            var path = optedOut ? null : GetExePath();
            if (!optedOut && string.IsNullOrEmpty(path)) return;

            // Never create the Run entry before the legacy task is gone, or both
            // mechanisms would launch Clipsy at the next sign-in. Old builds created
            // /RL HIGHEST tasks, so deleting may need one UAC prompt.
            bool removed = RemoveLegacyScheduledTask(elevateIfNeeded: !ElevationDeclined());
            lock (_sync) _legacyPresent = !removed;
            if (!removed) return;

            if (optedOut) DeleteRunEntry();
            else
            {
                SetRunEntry(path!);
                SetOptOut(false);
            }
        }
        catch (Exception ex) { Diagnostics.Log("AutostartService.MigrateLegacyScheduledTask", ex); }
    }

    public static bool ApplyInstallerPreference(bool disabled, bool removeOnly = false)
    {
        try
        {
            if (removeOnly)
            {
                DeleteRunEntry();
                return !HasRunEntry();
            }
            if (disabled)
            {
                DeleteRunEntry();
                SetOptOut(true);
                return !HasRunEntry();
            }
            if (IsOptedOut())
            {
                DeleteRunEntry();
                return true;
            }
            var path = GetExePath();
            if (string.IsNullOrEmpty(path)) return false;
            // A Task Manager "Disabled" (StartupApproved) is the user's opt-out: it outlives the
            // rewritten entry, so updates don't silently turn autostart back on.
            SetRunEntry(path);
            SetOptOut(false);
            return HasRunEntry();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("AutostartService.ApplyInstallerPreference", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var path = GetExePath();
                if (string.IsNullOrEmpty(path)) return;
                SetRunEntry(path);
                // Enabling it here is explicit, so it overrides a Task Manager disable.
                ClearTaskManagerDisable();
                SetOptOut(false);
            }
            else
            {
                DeleteRunEntry();
                SetOptOut(true);
            }
            RemoveLegacyTaskInBackground(elevateIfNeeded: !enabled);
        }
        catch (Exception ex) { Diagnostics.Log("AutostartService.SetEnabled", ex); }
    }

    // Called from Settings on the UI thread: schtasks and a possible UAC prompt run in the background.
    private static void RemoveLegacyTaskInBackground(bool elevateIfNeeded)
    {
        // Shown as gone while the removal runs; the outcome corrects it.
        lock (_sync) { _legacyChecked = true; _legacyPresent = false; }
        System.Threading.Tasks.Task.Run(() =>
        {
            bool present = !RemoveLegacyScheduledTask(elevateIfNeeded);
            lock (_sync) { _legacyChecked = true; _legacyPresent = present; }
        });
    }

    // Entry must point at this exe; a stale path from a moved install doesn't count.
    private static bool HasRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(AppName) is not string value || string.IsNullOrWhiteSpace(value)) return false;
        var exe = GetExePath();
        return exe == null || string.Equals(value.Trim().Trim('"'), exe, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetRunEntry(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key?.SetValue(AppName, $"\"{path}\"", RegistryValueKind.String);
    }

    private static void DeleteRunEntry()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        ClearTaskManagerDisable();
    }

    // Task Manager's Startup tab disables entries via StartupApproved (odd first byte = disabled).
    private static bool IsDisabledInTaskManager()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
        return key?.GetValue(AppName) is byte[] data && data.Length > 0 && (data[0] & 1) == 1;
    }

    private static void ClearTaskManagerDisable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private static bool ElevationDeclined()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(MigrationDeclinedValue) is int value && value == 1;
    }

    private static void SetElevationDeclined()
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key?.SetValue(MigrationDeclinedValue, 1, RegistryValueKind.DWord);
    }

    private static bool IsOptedOut()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(OptOutValue) is int value && value == 1;
    }

    private static void SetOptOut(bool optedOut)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (optedOut) key?.SetValue(OptOutValue, 1, RegistryValueKind.DWord);
        else key?.DeleteValue(OptOutValue, throwOnMissingValue: false);
    }

    private static bool CachedLegacyTaskExists()
    {
        lock (_sync)
        {
            if (_legacyChecked) return _legacyPresent;
        }
        bool present = LegacyTaskExists();
        lock (_sync)
        {
            _legacyPresent = present;
            _legacyChecked = true;
            return present;
        }
    }

    // IsEnabled runs on the UI thread: never spawn schtasks there. Until the startup migration
    // has checked, the registry answers; if it can't, assume no legacy task.
    private static bool LegacyTaskPresentNoWait()
    {
        lock (_sync)
        {
            if (_legacyChecked) return _legacyPresent;
        }
        return LegacyTaskInRegistry() ?? false;
    }

    private static bool LegacyTaskExists()
        => LegacyTaskInRegistry() ?? RunSchtasks($"/Query /TN \"{LegacyTaskName}\"") == 0;

    // Task Scheduler mirrors every task under TaskCache\Tree; null when the key can't be read.
    private static bool? LegacyTaskInRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TaskCacheTreeKey);
            return key != null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool RemoveLegacyScheduledTask(bool elevateIfNeeded)
    {
        if (!LegacyTaskExists()) return true;
        var args = $"/Delete /TN \"{LegacyTaskName}\" /F";
        if (RunSchtasks(args) == 0) return true;
        if (!elevateIfNeeded)
        {
            Diagnostics.Log("AutostartService: legacy scheduled task still present");
            return false;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p == null) return false;
            if (!p.WaitForExit(120_000)) return false;
            return p.ExitCode == 0 || !LegacyTaskExists();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: user declined UAC; don't ask again on every start.
            Diagnostics.Log($"AutostartService: elevation declined ({ex.Message})");
            SetElevationDeclined();
            return false;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("AutostartService.RemoveLegacyScheduledTask elevated", ex);
            return false;
        }
    }

    // Output isn't redirected, so the wait stays bounded even if schtasks never finishes.
    private static int RunSchtasks(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (p == null) return -1;
        if (p.WaitForExit(5000)) return p.ExitCode;
        try { p.Kill(); } catch { }
        return -1;
    }

    private static string? GetExePath()
    {
        var loc = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(loc)) return null;
        if (!loc.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return loc;
        var exe = loc[..^4] + ".exe";
        return File.Exists(exe) ? exe : loc;
    }
}
