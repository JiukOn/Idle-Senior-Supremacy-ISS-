using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdleSenior.Core.Models;

namespace IdleSenior.Core.Services
{
    /// <summary>
    /// Persists application settings to %AppData%\IdleSeniorSupremacy\settings.json.
    /// All sensitive fields (API key, session cookies) are encrypted on disk using
    /// Windows DPAPI (DataProtectionScope.CurrentUser) so no other Windows user or
    /// process can read them from the file.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class AppSettingsService
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IdleSeniorSupremacy",
            "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static AppSettingsService? _instance;
        public static AppSettingsService Instance => _instance ??= new AppSettingsService();

        /// <summary>In-memory settings (always plain text).</summary>
        public AppSettings Current { get; private set; } = new();

        /// <summary>Fired whenever settings are saved or updated.</summary>
        public event Action<AppSettings>? SettingsChanged;

        /// <summary>Fired specifically when the pagination size changes.</summary>
        public event Action<int>? GamesPerPageChanged;

        [SupportedOSPlatform("windows")]
        public AppSettingsService() => Load();

        // ── Persistence ──────────────────────────────────────────────────────

        [SupportedOSPlatform("windows")]
        public void Load()
        {
            if (!File.Exists(SettingsPath)) return;
            try
            {
                var json = File.ReadAllText(SettingsPath);
                var raw  = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                // Decrypt sensitive fields; fall back to plain text for legacy files
                Current = new AppSettings
                {
                    ProfileUrl          = raw.ProfileUrl,
                    SteamId64           = raw.SteamId64,
                    AppTheme            = string.IsNullOrWhiteSpace(raw.AppTheme) ? "Dark" : raw.AppTheme,
                    Language            = string.IsNullOrWhiteSpace(raw.Language) ? "pt-BR" : raw.Language,
                    Currency            = string.IsNullOrWhiteSpace(raw.Currency) ? "BRL" : raw.Currency,
                    GamesPerPage        = raw.GamesPerPage > 0 ? raw.GamesPerPage : 50,
                    HoursFarmApps       = raw.HoursFarmApps ?? new(),
                    HoursFarmAutoStart  = raw.HoursFarmAutoStart,
                    HoursFarmMode       = string.IsNullOrWhiteSpace(raw.HoursFarmMode) ? "Simultaneous" : raw.HoursFarmMode,
                    HoursFarmTargets    = raw.HoursFarmTargets ?? new(),
                    CardFarmMode        = string.IsNullOrWhiteSpace(raw.CardFarmMode) ? "Simultaneous" : raw.CardFarmMode,
                    CardFarmMinHours    = raw.CardFarmMinHours,
                    CardFarmBlacklist   = raw.CardFarmBlacklist ?? new(),
                    CardFarmSortBy      = string.IsNullOrWhiteSpace(raw.CardFarmSortBy) ? "Cards" : raw.CardFarmSortBy,
                    TotalCardsIdled     = raw.TotalCardsIdled,
                    TotalMinutesIdled   = raw.TotalMinutesIdled,
                    SteamApiKey         = Unprotect(raw.SteamApiKey),
                    SessionId           = Unprotect(raw.SessionId),
                    SteamLoginSecure    = Unprotect(raw.SteamLoginSecure),
                    SteamParental       = Unprotect(raw.SteamParental),
                };

                NormalizeIdentity();
            }
            catch
            {
                Current = new AppSettings();
            }
        }

        [SupportedOSPlatform("windows")]
        public void Save()
        {
            try
            {
                NormalizeIdentity();

                // Write encrypted copy to disk — Current always stays plain text
                var toSave = new AppSettings
                {
                    ProfileUrl          = Current.ProfileUrl,
                    SteamId64           = Current.SteamId64,
                    AppTheme            = Current.AppTheme,
                    Language            = Current.Language,
                    Currency            = Current.Currency,
                    GamesPerPage        = Current.GamesPerPage > 0 ? Current.GamesPerPage : 50,
                    HoursFarmApps       = Current.HoursFarmApps,
                    HoursFarmAutoStart  = Current.HoursFarmAutoStart,
                    HoursFarmMode       = Current.HoursFarmMode,
                    HoursFarmTargets    = Current.HoursFarmTargets,
                    CardFarmMode        = Current.CardFarmMode,
                    CardFarmMinHours    = Current.CardFarmMinHours,
                    CardFarmBlacklist   = Current.CardFarmBlacklist,
                    CardFarmSortBy      = Current.CardFarmSortBy,
                    TotalCardsIdled     = Current.TotalCardsIdled,
                    TotalMinutesIdled   = Current.TotalMinutesIdled,
                    SteamApiKey         = Protect(Current.SteamApiKey),
                    SessionId           = Protect(Current.SessionId),
                    SteamLoginSecure    = Protect(Current.SteamLoginSecure),
                    SteamParental       = Protect(Current.SteamParental),
                };

                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(toSave, JsonOptions));

                SettingsChanged?.Invoke(Current);
                GamesPerPageChanged?.Invoke(Current.GamesPerPage);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Save failed: {ex.Message}");
            }
        }

        // ── Identity auto-resolution & statistics ────────────────────────────

        /// <summary>
        /// Auto-populates SteamId64 from steamLoginSecure or ProfileUrl, and
        /// auto-populates ProfileUrl from SteamId64 when omitted by the user.
        /// </summary>
        public void NormalizeIdentity()
        {
            // 1. Extract SteamId64 from steamLoginSecure cookie ("76561198...%7C%7C..." or "76561198...||...")
            if (string.IsNullOrWhiteSpace(Current.SteamId64) && !string.IsNullOrWhiteSpace(Current.SteamLoginSecure))
            {
                var cookieMatch = Regex.Match(Current.SteamLoginSecure.Trim(), @"^(\d{17})(?:%7C%7C|\|\|)", RegexOptions.IgnoreCase);
                if (cookieMatch.Success)
                {
                    Current.SteamId64 = cookieMatch.Groups[1].Value;
                }
            }

            // 2. Extract SteamId64 from ProfileUrl if it uses /profiles/<id64>
            if (string.IsNullOrWhiteSpace(Current.SteamId64) && !string.IsNullOrWhiteSpace(Current.ProfileUrl))
            {
                var urlMatch = Regex.Match(Current.ProfileUrl, @"steamcommunity\.com/profiles/(\d{17})", RegexOptions.IgnoreCase);
                if (urlMatch.Success)
                {
                    Current.SteamId64 = urlMatch.Groups[1].Value;
                }
            }

            // 3. Normalize or infer ProfileUrl
            if (string.IsNullOrWhiteSpace(Current.ProfileUrl) && !string.IsNullOrWhiteSpace(Current.SteamId64))
            {
                Current.ProfileUrl = $"https://steamcommunity.com/profiles/{Current.SteamId64.Trim()}";
            }
            else if (!string.IsNullOrWhiteSpace(Current.ProfileUrl))
            {
                string trimmed = Current.ProfileUrl.Trim().TrimEnd('/');
                if (Regex.IsMatch(trimmed, @"^\d{17}$"))
                {
                    Current.ProfileUrl = $"https://steamcommunity.com/profiles/{trimmed}";
                }
                else if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                         !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (trimmed.StartsWith("steamcommunity.com/", StringComparison.OrdinalIgnoreCase))
                        Current.ProfileUrl = $"https://{trimmed}";
                    else
                        Current.ProfileUrl = $"https://steamcommunity.com/id/{trimmed}";
                }
                else
                {
                    Current.ProfileUrl = trimmed;
                }
            }
        }

        public string GetEffectiveProfileUrl()
        {
            NormalizeIdentity();
            return Current.ProfileUrl.TrimEnd('/');
        }

        [SupportedOSPlatform("windows")]
        public void AddIdledStats(uint minutes, uint cardsDropped = 0)
        {
            if (minutes == 0 && cardsDropped == 0) return;
            Current.TotalMinutesIdled += minutes;
            Current.TotalCardsIdled   += cardsDropped;
            Save();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Current.SteamApiKey) &&
            !string.IsNullOrWhiteSpace(Current.SteamId64);

        public bool IsLoggedIn =>
            !string.IsNullOrWhiteSpace(Current.SteamLoginSecure) &&
            !string.IsNullOrWhiteSpace(GetEffectiveProfileUrl());

        // ── DPAPI encryption (Windows CurrentUser scope) ─────────────────────

        [SupportedOSPlatform("windows")]
        private static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            try
            {
                var bytes     = Encoding.UTF8.GetBytes(plainText);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch
            {
                return plainText;
            }
        }

        [SupportedOSPlatform("windows")]
        private static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            try
            {
                var bytes     = Convert.FromBase64String(stored);
                var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return stored;
            }
        }
    }
}
