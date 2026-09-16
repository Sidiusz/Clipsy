using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Clipsy.Drawing;
using WinRect = Windows.Foundation.Rect;

namespace Clipsy.Services;

internal static class CliScreenshotService
{
    internal static CliResult Execute(string[] args)
    {
        bool all = false, clipboard = false, noSave = false;
        bool? cursor = null;
        string? monitor = null, region = null, outPath = null, formatArg = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            switch (a)
            {
                case "--all": all = true; break;
                case "--clipboard": clipboard = true; break;
                case "--no-save": noSave = true; break;
                case "--cursor": cursor = true; break;
                case "--no-cursor": cursor = false; break;
                case "--monitor":
                    if (!TryRead(args, ref i, out monitor)) return Missing(a);
                    break;
                case "--region":
                    if (!TryRead(args, ref i, out region)) return Missing(a);
                    break;
                case "--out":
                    if (!TryRead(args, ref i, out outPath)) return Missing(a);
                    break;
                case "--format":
                    if (!TryRead(args, ref i, out formatArg)) return Missing(a);
                    break;
                default:
                    return new CliResult(2, $"Unknown screenshot option: {args[i]}");
            }
        }

        int targetModes = (all ? 1 : 0) + (monitor != null ? 1 : 0) + (region != null ? 1 : 0);
        if (targetModes > 1) return new CliResult(2, "Use only one of --all, --monitor or --region.");
        if (noSave && !clipboard) return new CliResult(2, "--no-save requires --clipboard.");

        try { global::WinRT.ComWrappersSupport.InitializeComWrappers(); } catch { }
        var frame = new ScreenFreezeService().Capture(cursor);
        var target = ResolveTarget(frame, all, monitor, region);
        if (target.Width <= 0 || target.Height <= 0)
            return new CliResult(2, "Capture target is outside the virtual desktop.");
        var settings = SettingsService.Instance.Settings;
        var format = ResolveFormat(formatArg, outPath, settings.ScreenshotFormat, out var formatError);
        if (formatError != null) return new CliResult(2, formatError);
        var ext = ScreenshotRenderer.ExtensionFor(format);

        string? finalPath = null;
        if (!noSave)
        {
            finalPath = ResolveOutputPath(outPath, ext);
            var dir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        var selection = new WinRect(
            target.X - frame.VirtualBounds.X,
            target.Y - frame.VirtualBounds.Y,
            target.Width,
            target.Height);
        var bytes = ScreenshotRenderer.RenderEncoded(
            frame, selection, Array.Empty<DrawElement>(), 1.0, format, settings.JpgQuality);

        if (finalPath != null) File.WriteAllBytes(finalPath, bytes);
        if (clipboard) ClipboardService.SetImageAsync(bytes).GetAwaiter().GetResult();

        return new CliResult(0, finalPath ?? "copied to clipboard", new
        {
            path = finalPath,
            width = target.Width,
            height = target.Height,
            format = ext.TrimStart('.'),
            clipboard,
        });
    }
    private static Rectangle ResolveTarget(ScreenFreezeService.FrozenFrame frame, bool all, string? monitor, string? region)
    {
        Rectangle target;
        if (all)
        {
            target = frame.VirtualBounds;
        }
        else if (region != null)
        {
            if (!TryParseRegion(region, out target))
                throw new ArgumentException("Region must be x,y,width,height using integer screen coordinates.");
        }
        else
        {
            target = ResolveMonitor(frame, monitor ?? "cursor").Bounds;
        }
        return Rectangle.Intersect(target, frame.VirtualBounds);
    }

    private static ScreenFreezeService.MonitorInfo ResolveMonitor(ScreenFreezeService.FrozenFrame frame, string value)
    {
        if (value.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return frame.Monitors.FirstOrDefault(m => m.IsPrimary) ?? frame.Monitors[0];
        if (value.Equals("cursor", StringComparison.OrdinalIgnoreCase))
        {
            if (GetCursorPos(out var p))
            {
                var hit = frame.Monitors.FirstOrDefault(m => m.Bounds.Contains(p.X, p.Y));
                if (hit != null) return hit;
            }
            return frame.Monitors.FirstOrDefault(m => m.IsPrimary) ?? frame.Monitors[0];
        }
        if (int.TryParse(value, out int index))
        {
            var found = frame.Monitors.FirstOrDefault(m => m.Index == index);
            if (found != null) return found;
        }
        throw new ArgumentException($"Unknown monitor: {value}. Use cursor, primary or a zero-based monitor index.");
    }
    private static ScreenshotRenderer.OutputFormat ResolveFormat(
        string? explicitFormat, string? outPath, string fallback, out string? error)
    {
        error = null;
        string? fromExtension = null;
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var ext = Path.GetExtension(outPath).TrimStart('.').ToLowerInvariant();
            if (ext is "png" or "jpg" or "jpeg" or "webp") fromExtension = ext;
            else if (!string.IsNullOrEmpty(ext)) error = $"Unsupported output extension: .{ext}";
        }
        if (error != null) return ScreenshotRenderer.OutputFormat.Png;

        string chosen = explicitFormat?.ToLowerInvariant() ?? fromExtension ?? fallback;
        if (chosen is not ("png" or "jpg" or "jpeg" or "webp"))
        {
            error = $"Unsupported format: {chosen}. Use png, jpg or webp.";
            return ScreenshotRenderer.OutputFormat.Png;
        }
        if (explicitFormat != null && fromExtension != null)
        {
            var a = ScreenshotRenderer.ParseFormat(explicitFormat);
            var b = ScreenshotRenderer.ParseFormat(fromExtension);
            if (a != b) error = "--format conflicts with the --out file extension.";
        }
        return ScreenshotRenderer.ParseFormat(chosen);
    }

    private static string ResolveOutputPath(string? requested, string extension)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            var folder = SettingsService.Instance.GetEffectiveScreenshotFolder();
            return Path.Combine(folder, SaveDialogService.MakeTimestampName("Clipsy", extension));
        }
        string expanded = Environment.ExpandEnvironmentVariables(requested);
        if (Directory.Exists(expanded) || Path.EndsInDirectorySeparator(expanded))
            return Path.Combine(Path.GetFullPath(expanded), SaveDialogService.MakeTimestampName("Clipsy", extension));
        string full = Path.GetFullPath(expanded);
        return string.IsNullOrEmpty(Path.GetExtension(full)) ? full + extension : full;
    }

    private static bool TryParseRegion(string text, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, out _))) return false;
        int[] n = parts.Select(int.Parse).ToArray();
        if (n[2] <= 0 || n[3] <= 0) return false;
        rect = new Rectangle(n[0], n[1], n[2], n[3]);
        return true;
    }

    private static bool TryRead(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length) { value = null; return false; }
        value = args[++index];
        return true;
    }

    private static CliResult Missing(string option) => new(2, $"Missing value for {option}.");

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
