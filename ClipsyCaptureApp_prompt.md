# Clipsy

Name: Clipsy
Author: Sidiusz
Dir: D:\App Projects\ClipsyCaptureApp
Git: https://github.com/Sidiusz/Clipsy.git — push after each milestone
Stack: C# + WinUI 3

---

## Design

Minimalist, slightly rounded corners, smooth fast animations. Discord/Blender aesthetic.
Theme and language: auto-detected from system, user can override in Settings.
Tooltips on every button in capture panels and Settings.
All UI text: short, clean, no typos, no em dashes.

---

## Tray

- Left click: open capture overlay
- Right click: context menu — Capture Screen / Settings / Exit

---

## Capture overlay

PrintScreen (rebindable) opens the overlay.
Desktop is frozen (static screen). Black semi-transparent overlay applied.
Only hint shown: "Select area" — nothing else until selection is made.

### Selection

User drags to select region. Selected area becomes transparent, revealing frozen frame. Or ctrl+A for whole screen.
Single left mouse click without drag = minimum selection (100x100 px).

After selection is made:
- Drag inside = move selection
- Drag corners = resize
- Click outside + drag = new selection (previous clears instantly)

Right-click on empty area (before selection) or on selection = context menu:
- Select Screen 1, Select Screen 2, ... (one per monitor)
- Select All
- Cancel

Right-click on selection (after selection made) = same + Copy / Save / Save As / Clear (removes selection and all drawings)

---

## Bottom toolbar (appears after selection)

Icon-only buttons, centered below selection.

| Button | Icon | Action |
|---|---|---|
| Record | Red circle | Start recording |
| Screenshot | Camera | Save screenshot |
| Copy | Clipboard | Copy to clipboard and close |
| Cancel | X | Close overlay (also: Esc) |

All actions rebindable except Esc.
Esc during drawing: deactivates drawing tool, returns to move/resize mode. Drawing is NOT deleted.

---

## Right toolbar (appears after selection)

Icon-only tools. Same panel available during recording.

| Tool | Icon | Notes |
|---|---|---|
| Color | Filled circle (current color) | Click to open color palette |
| Pencil | Pencil icon | Click to activate. Shows size ring preview on cursor. Tooltip: LMB draw, RMB erase |
| Rectangle | Rectangle icon | Shape tool |
| Text | Letter T | Text input tool |
| Find Text | OCR button | Activates OCR on selection |

Left click = draw. Right click = erase. Ctrl+Z = undo. Ctrl+Y = redo.

Drawing before screenshot: burned into saved image.
Drawing before recording: burned into output, erasable during recording.

---

## Screenshot mode

Activated via camera button or hotkey.

- Ctrl+S: silent save. Saves to last Save As location if "remember last folder" is enabled in Settings, otherwise to default folder. Overlay closes.
- Camera button: opens Save As dialog, sets new default folder, overlay closes after save.
- Copy button: copies to clipboard, overlay closes.
- Default save folder: Documents\Clipsy\Screenshots

---

## Find Text mode (OCR)

Activated via OCR button in right toolbar. Works on frozen frame — no screenshot taken.

On activation: scanning animation plays over the selection.
If no text found: brief inline text message shown over the region, fades out automatically. No popup.

When recognition finishes:
- Each word/line gets a semi-transparent yellow rectangle overlay
- Recognized characters re-drawn on top as clean vector glyphs (not original pixels)
- User selects text with cursor or Select All button

Actions:
- Copy: copies selected text to clipboard
- Translate: slide-up panel below the region, original and translation side by side

OCR engine: Tesseract (default). WinRT as alternative (switchable in Settings).

---

## Record mode

Activated via record button or hotkey.
Overlay fades. Screen returns to normal. Capture region shown with red border.

### Recording HUD

Shown below the capture region (or nearest free edge if no space).
Auto-hides to semi-transparent when cursor moves away. Reappears on hover.
All HUD elements are icon-only.

HUD icons: Timer | Pause | Stop | Stop+Save | Draw toggle | Lock

### Region lock

Locked by default during recording.
Double-click lock icon = unlock (region can be moved/resized).
Double-click again = lock.

When unlocked: hold dedicated move button and drag to reposition region. Recording continues.

### Drawing during recording

Requires selecting a tool from the right toolbar first (same toolbar as in capture overlay).
Left click = draw. Right click = erase. Ctrl+Z = undo. Ctrl+Y = redo.
Drawings appear in real time and are burned into the output.

### Output formats

MP4 (chosen codec), GIF, AVI, WebM, MP3 (audio only).

### Saving

- Stop+Save: opens Save As dialog by default
- Ctrl+S equivalent for recording: configurable hotkey, disabled by default to prevent accidental saves
- Default save folder: Documents\Clipsy\Video
- After save (configurable in Settings): open file / open folder / nothing (default)

---

## Settings

Tabbed panel. Buttons: Save / Close / Reset.

### General tab

- Language: auto-detect from system, override dropdown (English / Russian)
- Theme: auto-detect from system, override dropdown (Dark / Light)
- OCR engine: Tesseract (default) / WinRT
- Screenshot folder: default Documents\Clipsy\Screenshots
- Video folder: default Documents\Clipsy\Video
- Remember last Save As folder: toggle (default on)
- Updates: check interval dropdown (hourly / daily / weekly / monthly / never) + Check now button

### Video tab

- Codec: H.264 (default), H.265, VP9, AV1 — dropdown, one-line description per codec
- Resolution: 480p / 720p / 1080p / 1440p / Original
  Note: resolution applies only when recording full screen. When recording a selected region, the region size is used as-is; bitrate setting still applies.
- Bitrate: slider in Mbps, max scales with resolution
- Estimated file size per minute: shown dynamically, rounded to nearest 10 (e.g. 120 MB, not 113 MB)

### GIF tab

- Color count slider (default: 256)
- Frame rate slider (default: 12 fps)
- Dithering toggle (default: on)

### Hotkeys tab

All hotkeys rebindable. Esc is reserved and not rebindable.
Table layout: action name / current keybind / click to rebind.

### Info tab

App name, version, author, GitHub link, manual update check button.

### Pro / Donations tab

Excluded from public v1 release. Noted as future feature.
All features fully available in v1. Implement monetization-sensitive features as code flags only — no paywall logic needed yet.

---

## Errors

Toast notifications for failures: save error, OCR failure, recording error, etc.
Short message, actionable where possible (e.g. "Could not save. Choose another location?").

---

## Updates

Check GitHub releases at configured interval.
Notify via tray balloon or in-app banner. Option to skip version.

---

## Installer

Standard installer. No special design requirements.

## Working approach

Work in phases. Complete each phase fully before moving to the next.
Don't ask what to do next — decide yourself and proceed.
Ask only if genuinely blocked or a decision affects the whole architecture.
Commit after each phase.

Phase 1: Project structure, tray icon, basic overlay with freeze + selection
Phase 2: Bottom toolbar + right toolbar, drawing tools
Phase 3: Screenshot mode (save, copy, Ctrl+S)
Phase 4: OCR / Find Text mode
Phase 5: Record mode + HUD
Phase 6: Settings window (all tabs)
Phase 7: Updates, error handling, localization
Phase 8: Installer