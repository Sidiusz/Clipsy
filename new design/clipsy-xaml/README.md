# Clipsy · XAML drop-in

This folder mirrors the new unified UI from `Clipsy UI.html` as real WinUI 3
XAML. Every window — settings, capture overlay, recording HUD — pulls from
the same `Clipsy.Tokens.xaml` + `Clipsy.Styles.xaml` so changing a brush
or radius once cascades through the whole app.

## What's here

```
clipsy-xaml/
├── App.xaml                                  # Application-level merged dictionaries
├── Themes/
│   ├── Clipsy.Tokens.xaml                    # Colors, brushes, type tokens, radii
│   └── Clipsy.Styles.xaml                    # Button, IconButton, TextBox, ToggleSwitch,
│                                               toolbar, card, kbd, label, helper, etc.
└── Views/
    ├── CaptureOverlayWindow.xaml             # Same names, theme-driven brushes
    ├── Settings/
    │   ├── SettingsWindow.xaml               # Sidebar nav + dense group cards
    │   └── SettingsWindow.xaml.cs.partial    # New code-behind additions
    └── Recording/
        └── RecordingHudWindow.xaml           # Pill + REC dot + lock/move/draw
```

## Drop-in steps

1. Copy `Themes/` to `Clipsy/Themes/`.
2. Open `Clipsy/App.xaml` and add the two `<ResourceDictionary>` source
   entries from `clipsy-xaml/App.xaml` to its merged dictionaries, after
   `<XamlControlsResources />`.
3. Replace `Clipsy/Views/CaptureOverlayWindow.xaml`,
   `Clipsy/Views/Recording/RecordingHudWindow.xaml`, and
   `Clipsy/Views/Settings/SettingsWindow.xaml` with the new versions.
4. Merge `SettingsWindow.xaml.cs.partial` into your existing
   `Clipsy/Views/Settings/SettingsWindow.xaml.cs`:
   - Add `OnNavSelectionChanged` so the sidebar swaps panes.
   - Replace your existing `HotkeyEntry` record with the new
     `INotifyPropertyChanged` version — it exposes `IsRebinding` plus
     the derived bindings the row template renders against.
   - Add `OnHotkeyRebindClick` + the `OnRebindKeyDown` / `CancelRebind`
     helpers. The old modal `RebindKeyWindow.xaml` (if you have one) can
     be deleted.

## Code-behind churn vs. existing app

All `x:Name`s from the previous XAML are preserved — `LangBox`, `BitrateSlider`,
`RememberFolderSwitch`, `ScreenshotFolderBox`, etc. — so the data-loading and
save methods in your current `SettingsWindow.xaml.cs` keep compiling.

Things that changed:

- `TabGeneral` / `TabVideo` / `TabGif` / `TabHotkeys` / `TabInfo` (the old
  `TabViewItem` x:Names) are gone. They're replaced by `PaneGeneral`,
  `PaneVideo`, etc., toggled by `Visibility`. If your existing code does
  `TabGeneral.Header = Strings.Get(...)`, change it to set the
  `NavGeneralLabel.Text` (and matching `NavVideoLabel`, `NavGifLabel`,
  `NavHotkeysLabel`, `NavInfoLabel`) instead.
- The capture-overlay `Hint` element is now a `Border`, not a `TextBlock`.
  If your code does `Hint.Text = "..."` you'll need to drop that or move
  the bottom text into a named `TextBlock` inside the Border.
- `BitrateSlider` is now full-width inside its column — no behavior change.

## Localization

The pane titles (`HdrGeneral`, `HdrVideo`, etc.), sidebar labels
(`NavGeneralLabel`, etc.), the tip card (`LblTip`), and every per-field
helper text (`HelperLanguage`, `HelperTheme`, `HelperOcr`, ...) are
all `x:Named` so you can pipe `Strings.Get(...)` into them from your
existing `ApplyLocalization` pass.

## Accent swap (Discord blurple ↔ Blender orange)

If you want to expose the same accent options as the design mockup, change
the `ClipsyAccent` family of colors in `Clipsy.Tokens.xaml`:

```
Blurple : #FF5865F2 / #FF4752C4 / #285865F2
Orange  : #FFE87D0D / #FFC66A0A / #28E87D0D
Green   : #FF23A55A / #FF1D8B4D / #2823A55A
Pink    : #FFEB459E / #FFC93A85 / #28EB459E
```

Loading them at runtime from a setting:

```csharp
var theme = (string)settings.Get("Accent", "blurple");
var palette = theme switch
{
    "orange" => ("#FFE87D0D", "#FFC66A0A", "#28E87D0D"),
    "green"  => ("#FF23A55A", "#FF1D8B4D", "#2823A55A"),
    "pink"   => ("#FFEB459E", "#FFC93A85", "#28EB459E"),
    _        => ("#FF5865F2", "#FF4752C4", "#285865F2"),
};
Application.Current.Resources["ClipsyAccent"]      = ParseColor(palette.Item1);
Application.Current.Resources["ClipsyAccentHover"] = ParseColor(palette.Item2);
Application.Current.Resources["ClipsyAccentDim"]   = ParseColor(palette.Item3);
// Brushes that reference the colors via StaticResource re-resolve on next render.
```
