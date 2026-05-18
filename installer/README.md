# Clipsy installer

The Clipsy installer is built with [Inno Setup 6](https://jrsoftware.org/isdl.php).
The script is plain text — no IDE required.

## Layout

```
installer/
  Clipsy.iss      Inno Setup script
  build.ps1       Orchestrator: dotnet publish + ISCC compile
  output/         Generated .exe lands here
  README.md       This file
```

## Prerequisites

1. .NET 8 SDK (or newer) on PATH — `dotnet --version`
2. Windows PowerShell 5.1 (ships with Windows) or PowerShell 7+
3. Internet access — Inno Setup 6 is fetched automatically if missing
4. The script wipes `Clipsy\bin\publish\win-x64\` before publishing,
   so don't park anything important there

## Build

Easiest — double-click `BuildInstaller.cmd` at the repo root. The
wrapper resolves its own path, so it works from any directory.

From a shell:

```
BuildInstaller.cmd
BuildInstaller.cmd -Version 0.2.0
BuildInstaller.cmd -Configuration Debug
BuildInstaller.cmd -SkipInnoInstall      :: refuse auto-install
```

Or call PowerShell directly:

```
powershell -ExecutionPolicy Bypass -File installer\build.ps1
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 0.2.0
```

Output goes to `installer\output\Clipsy-Setup-<version>.exe`.

## Inno Setup auto-install

If `ISCC.exe` is not present in `Program Files\Inno Setup 6\` or the
(x86) equivalent, the script will fetch and install it for you:

1. Tries `winget install JRSoftware.InnoSetup` first (silent, scope
   machine). Most Windows 11 / recent Windows 10 already have winget.
2. Falls back to downloading the official installer from
   `https://jrsoftware.org/download.php/is.exe` and running it with
   `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`.

Both paths trigger one UAC prompt. Pass `-SkipInnoInstall` to refuse
the auto-install — the script will publish and exit with code 2, then
you can install Inno Setup manually and re-run.

## What the installer does

- Installs to `C:\Program Files\Clipsy` (per-machine, admin) or the
  appropriate per-user folder if admin is declined
- Creates a Start Menu entry for Clipsy + uninstaller
- Optional desktop shortcut (tasks page, unchecked by default)
- Optional "run at sign-in" registry entry under HKCU\…\Run (unchecked)
- Registers an Add/Remove Programs entry that points the uninstaller at
  the installed directory
- Uninstall removes the install folder plus `%LOCALAPPDATA%\Clipsy`
  (settings.json, cached state)

## Publish settings

`build.ps1` calls `dotnet publish` with:

- `-r win-x64`
- `--self-contained true` — bundles the .NET 8 runtime, no separate
  install needed on the target machine
- `-p:WindowsAppSDKSelfContained=true` — bundles the Windows App SDK
  bootstrapper and Microsoft.UI.Xaml DLLs, no WinAppSDK runtime
  prerequisite

Result is ~80–120 MB of files in `Clipsy\bin\publish\win-x64\`. Inno
Setup compresses it with LZMA2/ultra64 down to ~30–50 MB.

## Notes

- Tessdata for the Tesseract OCR engine is not bundled in this phase.
  When tessdata files ship, drop them into
  `Clipsy\Assets\tessdata\` before publishing and the `Files` section
  in `Clipsy.iss` (`recursesubdirs`) will pick them up automatically.
- The installer is per-machine by default. To switch to per-user, change
  `PrivilegesRequired=admin` to `lowest` in `Clipsy.iss`.
- Signing is not configured. For a signed installer, add
  `SignTool=mysigntool` plus a `[SignTool]` entry that calls signtool
  with your code-signing certificate.
