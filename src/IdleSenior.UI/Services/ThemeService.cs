using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace IdleSenior.UI.Services
{
    public static class ThemeService
    {
        private static readonly HashSet<object> _injectedKeys = new();

        public static void ApplyTheme(string themeName)
        {
            if (Application.Current == null) return;

            var baseTheme = themeName switch
            {
                "Light" => ApplicationTheme.Light,
                "Spring" => ApplicationTheme.Light,
                _ => ApplicationTheme.Dark
            };

            var rootResources = Application.Current.Resources;
            var dictionaries = rootResources.MergedDictionaries;

            // 1. Remove any previously injected custom theme dictionaries & direct keys
            var customDicts = dictionaries
                .Where(d => d.Source?.OriginalString != null &&
                           (d.Source.OriginalString.Contains("DraculaTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                            d.Source.OriginalString.Contains("MidnightTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                            d.Source.OriginalString.Contains("SpringTheme.xaml", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var d in customDicts)
                dictionaries.Remove(d);

            foreach (var key in _injectedKeys)
                rootResources.Remove(key);
            _injectedKeys.Clear();

            // 2. Apply base WPF-UI theme (reloads clean Dark.xaml or Light.xaml in Wpf.Ui)
            ApplicationThemeManager.Apply(baseTheme);

            // 3. Apply theme-specific accent colors via Wpf.Ui's ApplicationAccentColorManager
            switch (themeName)
            {
                case "Dracula":
                    ApplicationAccentColorManager.Apply(
                        Color.FromRgb(0xC8, 0x1E, 0x45), // Primary Burgundy/Crimson
                        Color.FromRgb(0xE1, 0x1D, 0x48), // Secondary Ruby
                        Color.FromRgb(0x9F, 0x12, 0x39), // Tertiary Deep Wine
                        Color.FromRgb(0x88, 0x13, 0x37));
                    ApplyCustomDictionary("Resources/Themes/DraculaTheme.xaml");
                    break;

                case "Midnight":
                    ApplicationAccentColorManager.Apply(
                        Color.FromRgb(0x8B, 0x5C, 0xF6), // Primary Electric Violet
                        Color.FromRgb(0xA8, 0x55, 0xF7), // Secondary Neon Purple
                        Color.FromRgb(0x6D, 0x28, 0xD9), // Tertiary Deep Indigo
                        Color.FromRgb(0x5B, 0x21, 0xB6));
                    ApplyCustomDictionary("Resources/Themes/MidnightTheme.xaml");
                    break;

                case "Spring":
                    ApplicationAccentColorManager.Apply(
                        Color.FromRgb(0xF9, 0x73, 0x16), // Primary Solar Orange
                        Color.FromRgb(0xEA, 0x58, 0x0C), // Secondary Warm Tangerine
                        Color.FromRgb(0xC2, 0x41, 0x0C), // Tertiary Terracotta
                        Color.FromRgb(0xFB, 0x92, 0x3C));
                    ApplyCustomDictionary("Resources/Themes/SpringTheme.xaml");
                    break;

                default:
                    try { ApplicationAccentColorManager.ApplySystemAccent(); } catch { }
                    break;
            }
        }

        private static void ApplyCustomDictionary(string relativePath)
        {
            try
            {
                var rootResources = Application.Current.Resources;
                var dictionaries = rootResources.MergedDictionaries;

                var uri = new Uri($"pack://application:,,,/IdleSenior.UI;component/{relativePath}", UriKind.Absolute);
                var customDict = new ResourceDictionary { Source = uri };
                dictionaries.Add(customDict);

                // Also overwrite matching keys in Wpf.Ui's active theme dictionary and root resources
                // so every internal Wpf.Ui control template and DynamicResource updates immediately.
                var wpfUiThemeDict = dictionaries.FirstOrDefault(d =>
                    d.Source?.OriginalString.Contains("Resources/Theme/", StringComparison.OrdinalIgnoreCase) == true);

                foreach (var key in customDict.Keys)
                {
                    var val = customDict[key];
                    if (wpfUiThemeDict != null)
                        wpfUiThemeDict[key] = val;

                    rootResources[key] = val;
                    _injectedKeys.Add(key);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThemeService] Failed to apply custom theme '{relativePath}': {ex.Message}");
            }
        }
    }
}
