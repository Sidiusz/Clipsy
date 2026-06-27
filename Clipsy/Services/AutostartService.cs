using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace Clipsy.Services;

/// <summary>
/// Manages sign-in autostart. Clipsy runs elevated (requireAdministrator),
/// and a per-user Run-key entry cannot auto-elevate at login — UAC blocks it.
/// The only mechanism Windows allows for a silent elevated logon launch is a
/// Scheduled Task with the highest run level, so autostart is registered that
/// way. Any legacy Run-key value is removed on toggle to migrate old installs.
/// </summary>
public static class AutostartService
{
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Clipsy";
    private const string TaskName = "ClipsyAutostart";

    public static bool IsEnabled()
    {
        try
        {
            // Exit code 0 from /Query means the task exists.
            return RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;
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
                // /RL HIGHEST: run elevated. /SC ONLOGON + /RU current user:
                // fire when this user signs in, as this user. /F overwrites.
                var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
                var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{path}\\\"\" " +
                           $"/SC ONLOGON /RU \"{user}\" /RL HIGHEST /F";
                if (RunSchtasks(args) != 0)
                    Diagnostics.Log("AutostartService.SetEnabled: schtasks /Create non-zero exit");
            }
            else
            {
                RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("AutostartService.SetEnabled", ex);
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
