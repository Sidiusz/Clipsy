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

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) { Diagnostics.Log("AutostartService.IsEnabled", ex); return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var path = GetExePath();
                if (string.IsNullOrEmpty(path)) return;
                key?.SetValue(AppName, $"\"{path}\"", RegistryValueKind.String);
                SetOptOut(false);
            }
            else
            {
                key?.DeleteValue(AppName, throwOnMissingValue: false);
                SetOptOut(true);
            }
            RemoveLegacyScheduledTask();
        }
        catch (Exception ex) { Diagnostics.Log("AutostartService.SetEnabled", ex); }
    }

    private static void SetOptOut(bool optedOut)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            if (optedOut) key?.SetValue(OptOutValue, 1, RegistryValueKind.DWord);
            else key?.DeleteValue(OptOutValue, throwOnMissingValue: false);
        }
        catch (Exception ex) { Diagnostics.Log("AutostartService.SetOptOut", ex); }
    }

    private static void RemoveLegacyScheduledTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{LegacyTaskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(3000);
        }
        catch { }
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
