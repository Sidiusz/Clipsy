# Clipsy

Screenshot and screen recording tool for Windows. Lives in the tray, pops up an overlay when you hit a hotkey, and gets out of your way.

## What it does

- Capture a region, window or full screen with a hotkey
- Draw on screenshots: pencil, shapes, lines, arrows, text
- Color picker and eyedropper with magnifier
- Screen recording (with ffmpeg if installed) including a region border and floating HUD
- GIF export, with a built-in encoder if ffmpeg is missing
- Mic toggle while recording
- OCR text recognition on captures, with translation
- Copy to clipboard, save to file, or open the saved folder right away
- Light and dark theme
- Customizable hotkeys
- Multiple languages (UI localization)
- Auto-update check from GitHub releases
- Runs from the system tray, autostart on login

## Install

Download the installer from the [Releases](https://github.com/Sidiusz/Clipsy/releases) page, or grab the portable zip if you don't want to install anything.

## Building from source

```
installer\build.ps1
```

This publishes the app, syncs the version everywhere, builds the installer and a portable zip into `installer\output`.

## License

See [LICENSE](LICENSE) — personal and internal use only.
