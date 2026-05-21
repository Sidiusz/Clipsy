# Build errors → fixes (2 files, 3 edits)

After dropping the new XAML files in, the compiler will complain about
references to elements that no longer exist. Apply these three edits
manually and the build goes green.

---

## 1. Clipsy/Views/CaptureOverlayWindow.xaml.cs · line 110

The hint pill is now a `Border` (so the whole pill can show/hide), and
the visible label is a child `TextBlock` named `HintText`.

`Hint.Visibility = ...` on lines 394, 1013, 1261 keeps working — `Hint`
is still the Border. Only the `.Text` assignment needs to move.

**Before:**
```csharp
Hint.Text = Strings.Get("HintSelectArea");
```

**After:**
```csharp
HintText.Text = Strings.Get("HintSelectArea");
```

---

## 2. Clipsy/Views/Settings/SettingsWindow.xaml.cs · lines 74–78

The five `TabViewItem`s (`TabGeneral`, `TabVideo`, ...) became sidebar
list items, and the localized label is now an `x:Name`'d `TextBlock`
inside each one (`NavGeneralLabel`, `NavVideoLabel`, ...). The right-side
content panes also got localizable headers (`HdrGeneral`, ...).

**Before:**
```csharp
TabGeneral.Header = Strings.Get("TabGeneral");
TabVideo.Header   = Strings.Get("TabVideo");
TabGif.Header     = Strings.Get("TabGif");
TabHotkeys.Header = Strings.Get("TabHotkeys");
TabInfo.Header    = Strings.Get("TabInfo");
```

**After:**
```csharp
NavGeneralLabel.Text = Strings.Get("TabGeneral");
NavVideoLabel.Text   = Strings.Get("TabVideo");
NavGifLabel.Text     = Strings.Get("TabGif");
NavHotkeysLabel.Text = Strings.Get("TabHotkeys");
NavInfoLabel.Text    = Strings.Get("TabInfo");

// Optional but recommended — the right-pane header also gets localized:
HdrGeneral.Text = Strings.Get("TabGeneral");
HdrVideo.Text   = Strings.Get("TabVideo");
HdrGif.Text     = Strings.Get("TabGif");
HdrHotkeys.Text = Strings.Get("TabHotkeys");
```

---

## 3. Clipsy/Views/Settings/SettingsWindow.xaml.cs · add nav handler

The sidebar `ListView` fires `SelectionChanged="OnNavSelectionChanged"`
in XAML but no such method exists yet. Add this method anywhere inside
the `SettingsWindow` class (e.g. right after `OnFirstActivated`):

```csharp
private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (NavList?.SelectedItem is not ListViewItem item) return;
    var key = item.Tag as string;

    if (PaneGeneral != null) PaneGeneral.Visibility = key == "general" ? Visibility.Visible : Visibility.Collapsed;
    if (PaneVideo   != null) PaneVideo.Visibility   = key == "video"   ? Visibility.Visible : Visibility.Collapsed;
    if (PaneGif     != null) PaneGif.Visibility     = key == "gif"     ? Visibility.Visible : Visibility.Collapsed;
    if (PaneHotkeys != null) PaneHotkeys.Visibility = key == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
    if (PaneInfo    != null) PaneInfo.Visibility    = key == "info"    ? Visibility.Visible : Visibility.Collapsed;
}
```

That's it. Your existing `HotkeyRow` class, `OnHotkeyRebindClick`,
`OnRebindKeyDown`, and `FinishListening` already implement the inline
rebind flow and work as-is with the new XAML — no changes there.

---

## Notes

- Your old `RowBackground` / `BindingBackground` template bindings on
  the hotkey rows aren't present on `HotkeyRow` — that's fine, missing
  bindings are silent. The button still swaps its `Content` to
  "Press keys..." while listening, which is what you had before.
  If you want the full accent-tinted listening row from the mockup,
  add the props from `SettingsWindow.xaml.cs.partial` (deleted, but I
  can re-emit on request) to `HotkeyRow` and implement
  `INotifyPropertyChanged`.
- The bitrate label `Text` assignment in `UpdateBitrateLabel` writes
  `"8 Mbps"`; mockup shows the unit separately. Either works.
