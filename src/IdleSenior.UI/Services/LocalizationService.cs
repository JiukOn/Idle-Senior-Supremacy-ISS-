using System;
using System.Linq;
using System.Windows;

namespace IdleSenior.UI.Services
{
    /// <summary>
    /// Manages the active UI language by swapping the string ResourceDictionary
    /// that is merged into <see cref="Application.Current.Resources"/>.
    ///
    /// All XAML TextBlock/Button/etc. strings bound via
    /// <c>Text="{DynamicResource Key}"</c> update automatically when the
    /// dictionary is swapped, without restarting the application.
    /// </summary>
    public static class LocalizationService
    {
        private const string ResourceBase =
            "pack://application:,,,/IdleSenior.UI;component/Resources/";

        /// <summary>The language code currently in effect ("pt-BR" or "en-US").</summary>
        public static string CurrentLanguage { get; private set; } = "pt-BR";

        /// <summary>
        /// Switches the application language.  Safe to call from any thread —
        /// the dictionary swap is dispatched to the UI thread if needed.
        /// </summary>
        public static void SetLanguage(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode)) return;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => SetLanguage(languageCode));
                return;
            }

            CurrentLanguage = languageCode;

            var newDict = new ResourceDictionary
            {
                Source = new Uri($"{ResourceBase}Strings.{languageCode}.xaml", UriKind.Absolute)
            };

            var merged = Application.Current.Resources.MergedDictionaries;

            // Remove the previously loaded strings dictionary (identified by its Source URI).
            var old = merged.FirstOrDefault(d =>
                d.Source != null &&
                d.Source.OriginalString.Contains("/Resources/Strings."));

            if (old != null)
                merged.Remove(old);

            merged.Add(newDict);
        }

        /// <summary>
        /// Returns the localized string for <paramref name="key"/>, falling back
        /// to the key itself if the resource is not found.  Use this in
        /// code-behind where DynamicResource binding is not available.
        /// </summary>
        public static string Get(string key)
        {
            if (Application.Current?.Resources?.Contains(key) == true)
                return Application.Current.Resources[key]?.ToString() ?? key;
            return key;
        }
    }
}
