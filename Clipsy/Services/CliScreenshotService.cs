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

        // Validate everything before touching the screen; bad arguments are exit code 2.
        var settings = SettingsService.Instance.Settings;
        var format = ResolveFormat(formatArg, outPath, settings.ScreenshotFormat, out var formatError);
        if (formatError != null) return new CliResult(2, formatError);
        var ext = ScreenshotRenderer.ExtensionFor(format);
        Rectangle target;
        try
        {
            target = ResolveTarget(ScreenFreezeService.GetVirtualScreenBounds(), ScreenFreezeService.EnumerateMonitors(), all, monitor, region);
        }
        catch (ArgumentException ex)
        {
            return new CliResult(2, ex.Message);
        }
        if (target.Width <= 0 || target.Height <= 0)
            return new CliResult(2, "Capture target is outside the virtual desktop.");

        try { global::WinRT.ComWrappersSupport.InitializeComWrappers(); } catch { }
        var frame = new ScreenFreezeService().Capture(cursor);
        target = Rectangle.Intersect(target, frame.VirtualBounds);

        string? finalPath = null, saveFolder = null;
        if (!noSave)
        {
            finalPath = ResolveOutputPath(outPath, ext, out saveFolder);
            var dir = saveFolder ?? Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        var selection = new WinRect(
            target.X - frame.VirtualBounds.X,
            target.Y - frame.VirtualBounds.Y,
            target.Width,
            target.Height);
        var image = ScreenshotRenderer.Render(frame, selection, Array.Empty<DrawElement>(), 1.0);

        if (!noSave)
        {
            // WebP can't hold a side over 16383 px: such an image is written as PNG, and named so.
            var actual = ScreenshotRenderer.EffectiveFormat(format, image.Width, image.Height);
            ext = ScreenshotRenderer.ExtensionFor(actual);
            var bytes = ScreenshotRenderer.Encode(image.Bgra, image.Width, image.Height, actual, settings.JpgQuality);
            if (saveFolder != null)
                finalPath = SaveDialogService.WriteNewFile(saveFolder, "Clipsy", ext, bytes);
            else
            {
                if (actual != format) finalPath = Path.ChangeExtension(finalPath, ext);
                SaveDialogService.WriteReplacing(finalPath!, bytes);
            }
        }
        if (clipboard) ClipboardService.SetImage(image);

        return new CliResult(0, finalPath ?? "copied to clipboard", new
        {
            path = finalPath,
            width = target.Width,
            height = target.Height,
            format = ext.TrimStart('.'),
            clipboard,
        });
    }
    private static Rectangle ResolveTarget(Rectangle virtualBounds, IReadOnlyList<ScreenFreezeService.MonitorInfo> monitors,
        bool all, string? monitor, string? region)
    {
        Rectangle target;
        if (all)
        {
            target = virtualBounds;
        }
        else if (region != null)
        {
            if (!TryParseRegion(region, out target))
                throw new ArgumentException("Region must be x,y,width,height using integer screen coordinates.");
        }
        else
        {
            target = ResolveMonitor(monitors, monitor ?? "cursor").Bounds;
        }
        return Rectangle.Intersect(target, virtualBounds);
    }

    private static ScreenFreezeService.MonitorInfo ResolveMonitor(IReadOnlyList<ScreenFreezeService.MonitorInfo> monitors, string value)
    {
        if (monitors.Count == 0) throw new ArgumentException("No monitors are available.");
        if (value.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        if (value.Equals("cursor", StringComparison.OrdinalIgnoreCase))
            return ScreenFreezeService.MonitorUnderCursor(monitors)!;
        if (int.TryParse(value, out int index))
        {
            var found = monitors.FirstOrDefault(m => m.Index == index);
            if (found != null) return found;
        }
        throw new ArgumentException($"Unknown monitor: {value}. Use cursor, primary or a zero-based monitor index (0-{monitors.Count - 1}).");
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

    // An explicit file path, or null with the folder a new unique name is created in at write time.
    private static string? ResolveOutputPath(string? requested, string extension, out string? folder)
    {
        folder = null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            folder = SettingsService.Instance.GetEffectiveScreenshotFolder();
            return null;
        }
        string expanded = Environment.ExpandEnvironmentVariables(requested);
        if (Directory.Exists(expanded) || Path.EndsInDirectorySeparator(expanded))
        {
            folder = Path.GetFullPath(expanded);
            return null;
        }
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
}
