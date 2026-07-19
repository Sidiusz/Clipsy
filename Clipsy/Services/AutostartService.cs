using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace Clipsy.Services;

/// <summary>Sign-in autostart via a highest-privilege scheduled task: the app
/// runs elevated, so a Run-key entry cannot auto-elevate at login.</summary>
public static class AutostartService
{
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Clipsy";
    private const string TaskName = "ClipsyAutostart";
    // Installer reads this opt-out marker to decide whether to enable autostart
    // by default; set only when the user turns autostart off in the app.
    private const string SettingsKey = @"Software\Clipsy";
    private const string OptOutValue = "AutostartOptOut";

    public static bool IsEnabled()
    {
        try
        {
            return RunSchtasks($"/Query /TN \"{TaskName}\"") == 0; // 0 = task exists
        }
        catch (Exception ex)
        {
            Diagnostics.Log("AutostartService.IsEnabled", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            RemoveLegacyRunKey();
            if (enabled)
            {
                var path = GetExePath();
                if (string.IsNullOrEmpty(path)) return;
                // /RL HIGHEST elevated, /SC ONLOGON for this user, /F overwrite.
                var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
                var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{path}\\\"\" " +
                           $"/SC ONLOGON /RU \"{user}\" /RL HIGHEST /F";
                if (RunSchtasks(args) != 0)
                    Diagnostics.Log("AutostartService.SetEnabled: schtasks /Create non-zero exit");
                SetOptOut(false);
            }
            else
            {
                RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                SetOptOut(true);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("AutostartService.SetEnabled", ex);
        }
    }

    private static void SetOptOut(bool optedOut)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            if (optedOut) key?.SetValue(OptOutValue, 1, RegistryValueKind.DWord);
            else key?.DeleteValue(OptOutValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("AutostartService.SetOptOut", ex);
        }
    }

    private static void RemoveLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("AutostartService.RemoveLegacyRunKey", ex);
        }
    }

    private static int RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return -1;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string? GetExePath()
    {
        var loc = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(loc)) return null;
        if (loc.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var exe = loc.Substring(0, loc.Length - 4) + ".exe";
            if (File.Exists(exe)) return exe;
        }
        return loc;
    }
}
