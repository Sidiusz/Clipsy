using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Clipsy.exe is a GUI-subsystem app: cmd and PowerShell don't wait for it, so its output lands after
// the prompt and $LASTEXITCODE is never set. This console shim runs it, relays output and exit code.
var exe = Path.Combine(AppContext.BaseDirectory, "Clipsy.exe");
if (!File.Exists(exe))
{
    Console.Error.WriteLine($"Clipsy.exe not found next to {Path.GetFileName(Environment.ProcessPath)}.");
    return 3;
}

var psi = new ProcessStartInfo(exe)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
foreach (var a in args.Length == 0 ? new[] { "help" } : args) psi.ArgumentList.Add(a);

uint originalCp = GetConsoleOutputCP();
if (originalCp != 0) SetConsoleOutputCP(65001); // Clipsy writes UTF-8
try
{
    using var p = Process.Start(psi)!;
    AllowSetForegroundWindow((uint)p.Id); // lets `capture` bring the overlay to the front
    using var stdout = Console.OpenStandardOutput();
    using var stderr = Console.OpenStandardError();
    var o = p.StandardOutput.BaseStream.CopyToAsync(stdout);
    var e = p.StandardError.BaseStream.CopyToAsync(stderr);
    await p.WaitForExitAsync();
    await Task.WhenAll(o, e);
    return p.ExitCode;
}
finally
{
    if (originalCp != 0) SetConsoleOutputCP(originalCp);
}

[DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint processId);
[DllImport("kernel32.dll")] static extern uint GetConsoleOutputCP();
[DllImport("kernel32.dll")] static extern bool SetConsoleOutputCP(uint codePage);
