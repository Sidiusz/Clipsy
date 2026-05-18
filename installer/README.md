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
2. PowerShell 7 — `pwsh --version`
3. Inno Setup 6 installed; the default location
   `C:\Program Files\Inno Setup 6\ISCC.exe` is auto-detected by `build.ps1`
4. Repo at a clean state — the script wipes `Clipsy\bin\publish\win-x64\`
   before publishing

## Build

```
pwsh -File installer\build.ps1
pwsh -File installer\build.ps1 -Version 0.2.0
pwsh -File installer\build.ps1 -Configuration Debug
```

Output goes to `installer\output\Clipsy-Setup-<version>.exe`.

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
