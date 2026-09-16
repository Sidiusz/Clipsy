using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Clipsy.Services;

internal static class CliInstallerService
{
    internal static CliResult Install(string[] args)
    {
        string? setup = null, dir = null, log = null;
        bool silent = false, verySilent = false, desktop = false, noAutostart = false, noLaunch = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--silent": silent = true; break;
                case "--very-silent": verySilent = true; break;
                case "--desktop": desktop = true; break;
                case "--no-autostart": noAutostart = true; break;
                case "--no-launch": noLaunch = true; break;
                case "--dir": if (!Read(args, ref i, out dir)) return Missing("--dir"); break;
                case "--log": if (!Read(args, ref i, out log)) return Missing("--log"); break;
                case "--setup": if (!Read(args, ref i, out setup)) return Missing("--setup"); break;
                default:
                    if (args[i].StartsWith('-')) return new CliResult(2, $"Unknown install option: {args[i]}");
                    if (setup != null) return new CliResult(2, "Only one setup executable may be specified.");
                    setup = args[i];
                    break;
            }
        }
        if (silent && verySilent)
            return new CliResult(2, "Use only one of --silent or --very-silent.");

        setup ??= FindSetupExecutable();
        if (string.IsNullOrWhiteSpace(setup) || !File.Exists(setup))
            return new CliResult(2, "Setup executable not found. Pass its path or --setup PATH.");

        var switches = BuildCommonSwitches(silent, verySilent, log);
        if (!string.IsNullOrWhiteSpace(dir)) switches.Add($"/DIR={Quote(dir)}");
        if (desktop) switches.Add("/TASKS=desktopicon");
        if (noAutostart) switches.Add("/NOAUTOSTART");
        if (noLaunch) switches.Add("/NOLAUNCH");

        return LaunchDetached(Path.GetFullPath(setup), switches, "installer");
    }

    internal static CliResult Uninstall(string[] args)
    {
        string? log = null;
        bool silent = false, verySilent = false, keepData = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--silent": silent = true; break;
                case "--very-silent": verySilent = true; break;
                case "--keep-data": keepData = true; break;
                case "--log": if (!Read(args, ref i, out log)) return Missing("--log"); break;
                default: return new CliResult(2, $"Unknown uninstall option: {args[i]}");
            }
        }
        if (silent && verySilent)
            return new CliResult(2, "Use only one of --silent or --very-silent.");

        var uninstaller = ResolveUninstaller();
        if (uninstaller == null)
            return new CliResult(3, "Clipsy uninstaller was not found.");

        var switches = BuildCommonSwitches(silent, verySilent, log);
        if (keepData) switches.Add("/KEEPDATA");
        return LaunchDetached(uninstaller, switches, "uninstaller");
    }

    private static string? FindSetupExecutable()
    {
        foreach (var folder in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var found = Directory.GetFiles(folder, "Clipsy-Setup-*.exe")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            catch { }
        }
        return null;
    }

    private static List<string> BuildCommonSwitches(bool silent, bool verySilent, string? log)
    {
        var switches = new List<string>();
        if (verySilent)
        {
            switches.Add("/VERYSILENT");
            switches.Add("/SUPPRESSMSGBOXES");
            switches.Add("/NORESTART");
            switches.Add("/SP-");
        }
        else if (silent)
        {
            switches.Add("/SILENT");
            switches.Add("/SUPPRESSMSGBOXES");
            switches.Add("/NORESTART");
            switches.Add("/SP-");
        }
        if (!string.IsNullOrWhiteSpace(log)) switches.Add($"/LOG={Quote(log)}");
        return switches;
    }

    private static CliResult LaunchDetached(string file, List<string> switches, string kind)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = string.Join(' ', switches),
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(file) ?? Environment.CurrentDirectory,
        });
        if (process == null) return new CliResult(4, $"Could not launch {kind}.");
        return new CliResult(0, $"{kind} launched (PID {process.Id})", new
        {
            launched = true,
            pid = process.Id,
            file,
            detached = true,
        });
    }

    private static string? ResolveUninstaller()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "unins000.exe");
        if (File.Exists(local)) return local;

        var locations = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        };
        foreach (var location in locations)
        {
            var found = FindRegisteredUninstaller(location.Item1, location.Item2);
            if (found != null) return found;
        }
        return null;
    }

    private static string? FindRegisteredUninstaller(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root == null) return null;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (!string.Equals(key?.GetValue("DisplayName") as string, "Clipsy", StringComparison.OrdinalIgnoreCase))
                    continue;
                var path = ExtractExecutablePath(key?.GetValue("UninstallString") as string);
                if (path != null && File.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractExecutablePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw[0] == '"')
        {
            int end = raw.IndexOf('"', 1);
            return end > 1 ? raw[1..end] : null;
        }
        int space = raw.IndexOf(' ');
        return space > 0 ? raw[..space] : raw;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static bool Read(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length) { value = null; return false; }
        value = args[++index];
        return true;
    }

    private static CliResult Missing(string option) => new(2, $"Missing value for {option}.");
}
