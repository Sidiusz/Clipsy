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
- OCR text recognition on captures, with translation
- Copy to clipboard, save to file, or open the saved folder right away
- Light and dark theme
- Customizable hotkeys
- Multiple languages

## Install

Download the installer from the [Releases](https://github.com/Sidiusz/Clipsy/releases) page, or grab the portable zip if you don't want to install anything.

## Command line

The same `Clipsy.exe` is both the desktop app and the CLI. Headless commands do not open the capture UI.

```text
Clipsy.exe status [--json]
Clipsy.exe version [--json]
Clipsy.exe capture
Clipsy.exe open-settings
Clipsy.exe screenshot [--monitor cursor|primary|N | --all | --region x,y,w,h]
                      [--out PATH] [--format png|jpg|webp] [--clipboard] [--no-save]
                      [--cursor|--no-cursor] [--json]
Clipsy.exe config list [--json]
Clipsy.exe config get KEY [--json]
Clipsy.exe config set KEY VALUE [--json]
Clipsy.exe install [SETUP.exe] [--silent|--very-silent] [--dir PATH] [--desktop]
                   [--no-autostart] [--no-launch] [--log PATH] [--json]
Clipsy.exe uninstall [--silent|--very-silent] [--keep-data] [--log PATH] [--json]
```

`capture` and `open-settings` reuse the running Clipsy instance through a current-user-only named pipe. If Clipsy is not running, those UI commands start it and then forward the request. `screenshot` is headless and can return machine-readable metadata with `--json`.

CLI exit codes are `0` for success, `2` for invalid arguments, `3` when the requested app/installer resource is unavailable or not ready, and `4` for an operation failure. Installer and uninstaller wrappers are detached because they may replace or remove the executable that launched them; use the setup/uninstaller executable directly when you need its final Inno Setup process exit code.

## Building from source

```
installer\build.ps1
```

This publishes the app, syncs the version everywhere, builds the installer and a portable zip into `installer\output`.

## License

See [LICENSE](LICENSE) — personal and internal use only.

<img width="926" height="633" alt="image" src="https://github.com/user-attachments/assets/9c13cc4f-4337-4726-85fa-1fa54367c28c" />
