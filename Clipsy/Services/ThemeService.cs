using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace Clipsy.Services;

public static class ThemeService
{
    private static readonly object _gate = new();
    private static readonly List<WeakReference<FrameworkElement>> _roots = new();

    static ThemeService()
    {
        try
        {
            SettingsService.Instance.SettingsChanged += ApplyToRegistered;
        }
        catch
        {
            // Settings may not be available yet during very early startup.
        }
    }

    public static ElementTheme ResolveTheme(string themeSetting)
    {
        return themeSetting?.ToLowerInvariant() switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    public static void Register(FrameworkElement? element)
    {
        if (element == null) return;
        lock (_gate)
        {
            _roots.Add(new WeakReference<FrameworkElement>(element));
        }
        ApplyTo(element);
    }

    public static void ApplyTo(FrameworkElement? element)
    {
        if (element == null) return;
        element.RequestedTheme = ResolveTheme(SettingsService.Instance.Settings.Theme);
    }

    /// <summary>Theme-aware brush lookup for code-behind: pass the element whose
    /// actual theme should win (app resources resolve against the app theme).</summary>
    public static Microsoft.UI.Xaml.Media.Brush GetBrush(string key, FrameworkElement? context = null)
    {
        var theme = context?.ActualTheme ?? ResolveTheme(SettingsService.Instance.Settings.Theme);
        if (theme == ElementTheme.Default)
        {
            theme = Application.Current.RequestedTheme == ApplicationTheme.Light
                ? ElementTheme.Light : ElementTheme.Dark;
        }
        var dictKey = theme == ElementTheme.Light ? "Light" : "Default";
        if (TryGetThemed(Application.Current.Resources, dictKey, key, out var v)
            && v is Microsoft.UI.Xaml.Media.Brush b)
        {
            return b;
        }
        return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
    }

    private static bool TryGetThemed(ResourceDictionary rd, string dictKey, string key, out object? value)
    {
        value = null;
        if (rd.ThemeDictionaries.TryGetValue(dictKey, out var td)
            && td is ResourceDictionary themed
            && themed.TryGetValue(key, out value))
        {
            return true;
        }
        foreach (var merged in rd.MergedDictionaries)
        {
            if (TryGetThemed(merged, dictKey, key, out value)) return true;
        }
        return false;
    }

    private static void ApplyToRegistered()
    {
        List<FrameworkElement> live = new();
        lock (_gate)
        {
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                if (_roots[i].TryGetTarget(out var el) && el != null)
                {
                    live.Add(el);
                }
                else
                {
                    _roots.RemoveAt(i);
                }
            }
        }

        foreach (var el in live)
        {
            // One broken window (e.g. mid-teardown) must not abort theming
            // the rest or crash the settings save.
            try { ApplyTo(el); }
            catch (Exception ex) { Diagnostics.Log("ThemeService.ApplyToRegistered", ex); }
        }
    }
}
