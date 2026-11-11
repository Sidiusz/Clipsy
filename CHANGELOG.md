# Changelog

Detailed notes for every release (English and Russian) are in
[`.github/release-notes/`](.github/release-notes/) and on the
[Releases](https://github.com/Sidiusz/Clipsy/releases) page; the in-app changelog shows the same text.

## 1.1.1

Links to the website and GitHub on the Info page, and a fix for Settings that could stop opening.
See [1.1.1.md](.github/release-notes/1.1.1.md).

## 1.1.0

Fixes for a capture overlay that opened late on a heavily loaded PC, region recordings on single-monitor
PCs, selection and the eyedropper at display scaling above 100%, the recording toolbar, the tray
menu, notifications, hotkeys after sleep, and updates.
See [1.1.0.md](.github/release-notes/1.1.0.md).

## 1.0.13

Screen recordings through Windows Graphics Capture (no more drops to 2 fps), reliable saving under
load, a region that keeps its proportions and shows nothing around it when resized during recording,
VP9/AV1 in the chosen format, notifications kept out of recordings, and the PP-OCRv5 offer in Settings.
See [1.0.13.md](.github/release-notes/1.0.13.md).

## 1.0.12

Select app is back, the capture overlay works at display scaling other than 100%, a picked window is
captured without what covered it, smoother region resizing, new tips in Settings, and a Download button
in the changelog.
See [1.0.12.md](.github/release-notes/1.0.12.md).

## 1.0.11

Deselect in the overlay's right-click menu, Shift-constrained shapes, a single click that no longer
selects anything, removal of Select app, and more reliable capture of an app's sound.
See [1.0.11.md](.github/release-notes/1.0.11.md).

## 1.0.10

Recording a single window with only that app's sound, window picking in the capture overlay, hotkeys
for an instant screenshot and for starting or stopping recordings without the overlay, a Copy button
on the screenshot notification, and shapes that no longer grow on release.
See [1.0.10.md](.github/release-notes/1.0.10.md).

## 1.0.9

Screenshots through Windows Graphics Capture by default, which removes blocks in dark areas of video,
with a confirmation and a warning when switching to the legacy capture method.
See [1.0.9.md](.github/release-notes/1.0.9.md).

## 1.0.8

A responsive capture overlay under heavy load, stricter hotkey rules with reset and clear buttons,
translation only after confirmation, no more lost or resurrected recordings, a watchdog that respects
Task Manager, and many fixes to saving, settings and updates. See [1.0.8.md](.github/release-notes/1.0.8.md).

## 1.0.7

PP-OCRv5 as the default OCR engine with a one-time switch offer, Microsoft Translator as the default
translation service with automatic fallback, a clearer update flow in Settings, and Settings fixes.
See [1.0.7.md](.github/release-notes/1.0.7.md).

## 1.0.6

Working VP9/AV1 recording, exact-match screenshot export, undoable erase/clear/move, OCR accuracy
fixes, verified FFmpeg/Tesseract downloads, `clipsy-cli.exe`, no debug symbols in the install, and
many fixes to hotkeys, settings and recording safety. See [1.0.6.md](.github/release-notes/1.0.6.md).

## 1.0.0

Initial release.

- Region/window/fullscreen screenshot capture with hotkey
- Drawing tools: pencil, shapes, lines, arrows, text, color picker, eyedropper
- Screen recording with region border and floating HUD, mic toggle
- GIF export (ffmpeg or built-in encoder)
- OCR with translation
- Copy, save, and quick-open of saved folders
- Light and dark themes
- Customizable hotkeys
- Multi-language UI
- Tray icon, autostart, auto-update checks
