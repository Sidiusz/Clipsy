# Clipsy

Screenshot and screen recording tool for Windows. Lives in the tray, pops up an overlay when you hit a hotkey, and gets out of your way.

<img width="1074" height="917" alt="image" src="https://github.com/user-attachments/assets/0fa0da84-7bcf-4067-9d8b-86a12910e965" />

## What it does

- Capture a region, window or full screen with a hotkey
- Draw on screenshots: pencil, shapes, lines, arrows, text
- Color picker and eyedropper with magnifier
- Screen recording including a region border and floating HUD
- GIF export
- Mic toggle while recording
- OCR text recognition on captures (Windows OCR, Tesseract or PP-OCRv5), with translation
- Copy to clipboard, save to file, or open the saved folder right away
- Light and dark theme
- Customizable hotkeys
- Multiple languages

## Install

Download the installer from the [Releases](https://github.com/Sidiusz/Clipsy/releases) page, or grab the portable zip if you don't want to install anything.

## Privacy

Captures, recordings and OCR stay on your PC. Translation is the exception: the recognized text is sent to the service chosen in Settings (Google Translate or MyMemory). Update checks contact GitHub; FFmpeg, Tesseract languages and PP-OCRv5 models are downloaded only when you install them, and are verified by checksum.

## Command line

`Clipsy.exe` is both the desktop app and the CLI. Because it is a GUI program, shells don't wait for it; use `clipsy-cli.exe` (installed next to it) in scripts to get the output and exit code. Headless commands do not open the capture UI.

```text
clipsy-cli status [--json]
clipsy-cli version [--json]
clipsy-cli capture
clipsy-cli open-settings
clipsy-cli quit
clipsy-cli screenshot [--monitor cursor|primary|N | --all | --region x,y,w,h]
                      [--out PATH] [--format png|jpg|webp] [--clipboard] [--no-save]
                      [--cursor|--no-cursor] [--json]
clipsy-cli config list [--json]
clipsy-cli config get KEY [--json]
clipsy-cli config set KEY VALUE [--json]
clipsy-cli install [SETUP.exe] [--silent|--very-silent] [--dir PATH] [--desktop]
                   [--no-autostart] [--no-launch] [--log PATH] [--json]
clipsy-cli uninstall [--silent|--very-silent] [--keep-data] [--log PATH] [--json]
```

`capture`, `open-settings`, `quit` and `config` talk to the running Clipsy through a named pipe private to the current user and session. If Clipsy is not running, `capture` and `open-settings` start it first; `config` then edits the settings file directly. `quit` exits gracefully (a recording in progress is saved first) and waits for the process to end. `screenshot` is headless and can return machine-readable metadata with `--json`.

CLI exit codes are `0` for success, `2` for invalid arguments, `3` when the requested app/installer resource is unavailable or not ready, and `4` for an operation failure. Installer and uninstaller wrappers are detached because they may replace or remove the executable that launched them; use the setup/uninstaller executable directly when you need its final Inno Setup process exit code.

## Building from source

```
dotnet test Clipsy.Tests\Clipsy.Tests.csproj -p:Platform=x64
installer\build.ps1
```

The tests include an OCR quality corpus (skipped unless PP-OCRv5 models are installed) and video conversion checks (skipped without FFmpeg). `build.ps1` publishes the app and `clipsy-cli`, syncs the version, and writes the installer, a portable zip and `SHA256SUMS.txt` into `installer\output`.

## License

See [LICENSE](LICENSE) — personal and internal use only.

<img width="926" height="633" alt="image" src="https://github.com/user-attachments/assets/9c13cc4f-4337-4726-85fa-1fa54367c28c" />
