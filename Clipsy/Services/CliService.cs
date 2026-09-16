using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Clipsy.Services;

internal sealed record CliResult(int Code, string Message, object? Data = null)
{
    public bool Ok => Code == 0;
}

internal sealed record CliIpcRequest(string Command, string[] Args);
internal sealed record CliIpcResponse(bool Ok, int Code, string Message, JsonElement Data);

public static class CliService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || string.Equals(args[0], "--watchdog", StringComparison.Ordinal))
            return false;

        var command = args[0].Trim().ToLowerInvariant();
        if (!KnownCommands.Contains(command))
        {
            AttachParentConsole();
            WriteResult(new CliResult(2, $"Unknown command: {args[0]}"), false);
            WriteHelp();
            exitCode = 2;
            return true;
        }

        AttachParentConsole();
        bool json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));
        var tail = args.Skip(1).Where(a => !string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase)).ToArray();
        var result = Execute(command, tail);
        WriteResult(result, json);
        exitCode = result.Code;
        return true;
    }

    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "--help", "-h", "version", "status", "capture", "open-settings",
        "screenshot", "config", "install", "uninstall", "autostart-init",
    };
    private static CliResult Execute(string command, string[] args)
    {
        try
        {
            return command switch
            {
                "help" or "--help" or "-h" => HelpResult(),
                "version" => new CliResult(0, UpdateService.CurrentVersion(), new { version = UpdateService.CurrentVersion() }),
                "status" => Status(),
                "capture" => ExecuteUiCommand("capture"),
                "open-settings" => ExecuteUiCommand("open-settings"),
                "screenshot" => CliScreenshotService.Execute(args),
                "config" => ExecuteConfig(args),
                "install" => CliInstallerService.Install(args),
                "uninstall" => CliInstallerService.Uninstall(args),
                "autostart-init" => ExecuteAutostartInit(args),
                _ => new CliResult(2, $"Unknown command: {command}"),
            };
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"CLI command '{command}' failed", ex);
            return new CliResult(4, ex.Message);
        }
    }

    private static CliResult ExecuteAutostartInit(string[] args)
    {
        bool disabled = args.Length == 1 && string.Equals(args[0], "--disabled", StringComparison.OrdinalIgnoreCase);
        bool removeOnly = args.Length == 1 && string.Equals(args[0], "--remove", StringComparison.OrdinalIgnoreCase);
        if (args.Length > 1 || (args.Length == 1 && !disabled && !removeOnly))
            return new CliResult(2, "Usage: autostart-init [--disabled|--remove]");
        bool ok = AutostartService.ApplyInstallerPreference(disabled, removeOnly);
        string message = removeOnly ? "autostart entry removed" : disabled ? "autostart disabled" : "autostart initialized";
        return ok ? new CliResult(0, message) : new CliResult(4, "Failed to update autostart.");
    }

    private static CliResult Status()
    {
        bool running = SingleInstanceService.TryPingExisting();
        if (running && SingleInstanceService.TrySendRequest(
                SerializeRequest("status", Array.Empty<string>()), out var response, 500))
        {
            var live = DeserializeResponse(response);
            if (live.Ok) return live;
        }

        return new CliResult(0, running ? "running" : "stopped", new
        {
            running,
            version = running ? null : UpdateService.CurrentVersion(),
            executable = running ? null : Environment.ProcessPath,
        });
    }
    private static CliResult ExecuteConfig(string[] args)
    {
        if (args.Length == 0)
            return new CliResult(2, "Usage: Clipsy.exe config list|get|set ...");

        if (SingleInstanceService.TrySendRequest(SerializeRequest("config", args), out var response))
            return DeserializeResponse(response);

        return CliConfigService.Execute(args);
    }

    private static CliResult ExecuteUiCommand(string command)
    {
        string request = SerializeRequest(command, Array.Empty<string>());
        if (SingleInstanceService.TrySendRequest(request, out var response))
            return DeserializeResponse(response);

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return new CliResult(4, "Cannot resolve Clipsy executable path.");
        Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });

        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(100);
            if (SingleInstanceService.TrySendRequest(request, out response, 250))
                return DeserializeResponse(response);
        }
        return new CliResult(3, "Clipsy started but did not become ready for commands.");
    }

    internal static string SerializeRequest(string command, string[] args)
        => JsonSerializer.Serialize(new CliIpcRequest(command, args), JsonOptions);
    internal static bool TryParseRequest(string json, out CliIpcRequest? request)
    {
        try
        {
            request = JsonSerializer.Deserialize<CliIpcRequest>(json, JsonOptions);
            return request != null && !string.IsNullOrWhiteSpace(request.Command);
        }
        catch
        {
            request = null;
            return false;
        }
    }

    internal static string SerializeResult(CliResult result)
        => JsonSerializer.Serialize(new
        {
            ok = result.Ok,
            code = result.Code,
            message = result.Message,
            data = result.Data,
        }, JsonOptions);

    private static CliResult DeserializeResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<CliIpcResponse>(json, JsonOptions);
            return response == null
                ? new CliResult(4, "Empty IPC response.")
                : new CliResult(response.Code, response.Message, response.Data.ValueKind == JsonValueKind.Undefined ? null : response.Data);
        }
        catch (Exception ex)
        {
            return new CliResult(4, $"Invalid IPC response: {ex.Message}");
        }
    }
    private static CliResult HelpResult()
    {
        WriteHelp();
        return new CliResult(0, string.Empty);
    }

    private static void WriteHelp()
    {
        Console.Out.WriteLine("Clipsy command line");
        Console.Out.WriteLine("  Clipsy.exe status [--json]");
        Console.Out.WriteLine("  Clipsy.exe version [--json]");
        Console.Out.WriteLine("  Clipsy.exe capture");
        Console.Out.WriteLine("  Clipsy.exe open-settings");
        Console.Out.WriteLine("  Clipsy.exe screenshot [--monitor cursor|primary|N | --all | --region x,y,w,h]");
        Console.Out.WriteLine("                       [--out PATH] [--format png|jpg|webp] [--clipboard] [--no-save]");
        Console.Out.WriteLine("                       [--cursor|--no-cursor] [--json]");
        Console.Out.WriteLine("  Clipsy.exe config list [--json]");
        Console.Out.WriteLine("  Clipsy.exe config get KEY [--json]");
        Console.Out.WriteLine("  Clipsy.exe config set KEY VALUE [--json]");
        Console.Out.WriteLine("  Clipsy.exe install [SETUP.exe] [--silent|--very-silent] [--dir PATH] [--desktop]");
        Console.Out.WriteLine("                     [--no-autostart] [--no-launch] [--log PATH] [--json]");
        Console.Out.WriteLine("  Clipsy.exe uninstall [--silent|--very-silent] [--keep-data] [--log PATH] [--json]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Exit codes: 0 success, 2 invalid arguments, 3 unavailable/not ready, 4 operation failed.");
    }

    private static void WriteResult(CliResult result, bool json)
    {
        if (string.IsNullOrEmpty(result.Message) && !json) return;
        if (json)
        {
            Console.Out.WriteLine(SerializeResult(result));
            return;
        }
        var writer = result.Ok ? Console.Out : Console.Error;
        writer.WriteLine(result.Message);
    }
    private static void AttachParentConsole()
    {
        try
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = Encoding.UTF8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { }
    }

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}
