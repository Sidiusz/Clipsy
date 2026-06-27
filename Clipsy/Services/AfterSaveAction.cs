using System;
using System.Diagnostics;
using System.IO;

namespace Clipsy.Services;

public static class AfterSaveAction
{
    /// <summary>
    /// Apply the user's "after save" preference to the just-written file.
    /// "open-file"   - launches the file with the default associated app.
    /// "open-folder" - opens Explorer and selects the file.
    /// "nothing"     - no-op.
    /// </summary>
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
