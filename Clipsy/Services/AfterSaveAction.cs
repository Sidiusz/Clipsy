using System;
using System.Diagnostics;
using System.IO;

namespace Clipsy.Services;

public static class AfterSaveAction
{
    /// <summary>Apply the "after save" preference: open-file, open-folder
    /// (select in Explorer), or nothing.</summary>
    public static void Run(string filePath, string? action)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
        try
        {
            switch (action)
            {
                case "open-file":
                    // Launch via explorer so the handler runs at the shell's
                    // (non-elevated) integrity, not inheriting our admin token.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{filePath}\"",
                        UseShellExecute = true,
                    });
                    break;
                case "open-folder":
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        UseShellExecute = true,
                    });
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] AfterSaveAction.Run failed: {ex.Message}");
        }
    }
}
