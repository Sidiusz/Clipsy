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
    private static bool _legacyChecked;
    private static bool _legacyPresent;

    public static bool IsEnabled()
    {
        try { return HasRunEntry() || CachedLegacyTaskExists(); }
        catch (Exception ex) { Diagnostics.Log("AutostartService.IsEnabled", ex); return false; }
    }

    public static void MigrateLegacyScheduledTask()
    {
        try
        {
            if (!CachedLegacyTaskExists()) return;
            bool optedOut = IsOptedOut();
            var path = optedOut ? null : GetExePath();
            if (!optedOut && string.IsNullOrEmpty(path)) return;

            // Never create the Run entry before the legacy task is gone, or both
            // mechanisms would launch Clipsy at the next sign-in. A one-time UAC
            // prompt may be required because old builds created /RL HIGHEST tasks.
            bool removed = RemoveLegacyScheduledTask(elevateIfNeeded: true);
            _legacyPresent = !removed;
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

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var path = GetExePath();
                if (string.IsNullOrEmpty(path)) return;
                SetRunEntry(path);
                SetOptOut(false);
                _legacyChecked = true;
                _legacyPresent = !RemoveLegacyScheduledTask(elevateIfNeeded: false);
            }
            else
            {
                DeleteRunEntry();
                SetOptOut(true);
                _legacyChecked = true;
                _legacyPresent = !RemoveLegacyScheduledTask(elevateIfNeeded: true);
            }
        }
        catch (Exception ex) { Diagnostics.Log("AutostartService.SetEnabled", ex); }
    }

    private static bool HasRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    private static void SetRunEntry(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key?.SetValue(AppName, $"\"{path}\"", RegistryValueKind.String);
    }

    private static void DeleteRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
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
        if (_legacyChecked) return _legacyPresent;
        _legacyPresent = LegacyTaskExists();
        _legacyChecked = true;
        return _legacyPresent;
    }

    private static bool LegacyTaskExists()
        => RunSchtasks($"/Query /TN \"{LegacyTaskName}\"") == 0;

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
            p.WaitForExit();
            return p.ExitCode == 0 || !LegacyTaskExists();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("AutostartService.RemoveLegacyScheduledTask elevated", ex);
            return false;
        }
    }

    private static int RunSchtasks(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return -1;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return p.HasExited ? p.ExitCode : -1;
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
