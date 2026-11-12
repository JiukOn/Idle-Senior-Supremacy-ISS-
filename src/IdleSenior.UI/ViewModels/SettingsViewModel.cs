using System.Windows.Input;
using IdleSenior.Core.Services;

namespace IdleSenior.UI.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private readonly AppSettingsService _settingsService;
        private readonly SteamWebService    _steamWebService;

        private string _profileUrl        = "";
        private string _sessionId         = "";
        private string _steamLoginSecure  = "";
        private string _steamParental     = "";
        private string _steamId64         = "";
        private string _steamApiKey       = "";
        private string _selectedTheme     = "Dark";
        private string _selectedLanguage  = "pt-BR";
        private string _selectedCurrency  = "BRL";
        private string _gamesPerPage      = "50";
        private string _saveStatus        = "";
        private bool _hoursFarmAutoStart  = true;
        private string _hoursFarmMode     = "Simultaneous";
        private string _cardFarmMode      = "Simultaneous";
        private string _cardFarmMinHours  = "0.0";

        public string ProfileUrl
        {
            get => _profileUrl;
            set
            {
                if (SetField(ref _profileUrl, value))
                {
                    _settingsService.Current.ProfileUrl = value?.Trim() ?? "";
                    _settingsService.Save();
                }
            }
        }

        public string SessionId
        {
            get => _sessionId;
            set
            {
                if (SetField(ref _sessionId, value))
                {
                    // Only auto-save to persistent settings if non-empty, preventing accidental layout wipes
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _settingsService.Current.SessionId = value.Trim().Trim('"');
                        _settingsService.Save();
                    }
                }
            }
        }

        public string SteamLoginSecure
        {
            get => _steamLoginSecure;
            set
            {
                if (SetField(ref _steamLoginSecure, value))
                {
                    // Only auto-save to persistent settings if non-empty, preventing accidental layout wipes
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _settingsService.Current.SteamLoginSecure = value.Trim().Trim('"');
                        _settingsService.Save();
                    }
                }
            }
        }

        public string SteamParental
        {
            get => _steamParental;
            set
            {
                if (SetField(ref _steamParental, value))
                {
                    _settingsService.Current.SteamParental = value?.Trim() ?? "";
                    _settingsService.Save();
                }
            }
        }

        public string SteamId64
        {
            get => _steamId64;
            set
            {
                if (SetField(ref _steamId64, value))
                {
                    _settingsService.Current.SteamId64 = value?.Trim() ?? "";
                    _settingsService.Save();
                }
            }
        }

        public string SteamApiKey
        {
            get => _steamApiKey;
            set
            {
                if (SetField(ref _steamApiKey, value))
                {
                    _settingsService.Current.SteamApiKey = value?.Trim() ?? "";
                    _settingsService.Save();
                }
            }
        }

        public bool HoursFarmAutoStart
        {
            get => _hoursFarmAutoStart;
            set
            {
                if (SetField(ref _hoursFarmAutoStart, value))
                {
                    _settingsService.Current.HoursFarmAutoStart = value;
                    _settingsService.Save();
                }
            }
        }

        public string HoursFarmMode
        {
            get => _hoursFarmMode;
            set
            {
                if (SetField(ref _hoursFarmMode, value))
                {
                    _settingsService.Current.HoursFarmMode = value;
                    _settingsService.Save();
                }
            }
        }

        public string CardFarmMode
        {
            get => _cardFarmMode;
            set
            {
                if (SetField(ref _cardFarmMode, value))
                {
                    _settingsService.Current.CardFarmMode = value;
                    _settingsService.Save();
                }
            }
        }

        public string CardFarmMinHours
        {
            get => _cardFarmMinHours;
            set
            {
                if (SetField(ref _cardFarmMinHours, value))
                {
                    if (double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double h))
                    {
                        _settingsService.Current.CardFarmMinHours = h;
                        _settingsService.Save();
                    }
                }
            }
        }
        public string SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                if (SetField(ref _selectedTheme, value))
                {
                    ApplyTheme(value);
                    _settingsService.Current.AppTheme = value;
                    _settingsService.Save();
                }
            }
        }

        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (SetField(ref _selectedLanguage, value))
                {
                    ApplyLanguage(value);
                    _settingsService.Current.Language = value;
                    _settingsService.Save();
                }
            }
        }

        public string SelectedCurrency
        {
            get => _selectedCurrency;
            set
            {
                if (SetField(ref _selectedCurrency, value))
                {
                    _settingsService.Current.Currency = string.IsNullOrWhiteSpace(value) ? "BRL" : value;
                    _settingsService.Save();
                }
            }
        }

        public string GamesPerPage
        {
            get => _gamesPerPage;
            set
            {
                if (SetField(ref _gamesPerPage, value))
                {
                    if (int.TryParse(value, out int gpp) && gpp > 0)
                    {
                        _settingsService.Current.GamesPerPage = gpp;
                        _settingsService.Save();
                    }
                }
            }
        }
        public string SaveStatus       { get => _saveStatus;       set => SetField(ref _saveStatus,        value); }

        public ICommand SaveCommand  { get; }
        public ICommand ClearCommand { get; }

        public SettingsViewModel(AppSettingsService settingsService, SteamWebService steamWebService)
        {
            _settingsService  = settingsService;
            _steamWebService  = steamWebService;
            LoadFromSettings();

            SaveCommand  = new RelayCommand(SaveSettings);
            ClearCommand = new RelayCommand(ClearSettings);
        }

        private void LoadFromSettings()
        {
            var s = _settingsService.Current;
            ProfileUrl         = s.ProfileUrl;
            SessionId          = s.SessionId;
            SteamLoginSecure   = s.SteamLoginSecure;
            SteamParental      = s.SteamParental;
            SteamId64          = s.SteamId64;
            SteamApiKey        = s.SteamApiKey;
            SelectedTheme      = s.AppTheme;
            SelectedLanguage   = s.Language;
            SelectedCurrency   = string.IsNullOrWhiteSpace(s.Currency) ? "BRL" : s.Currency;
            GamesPerPage       = (s.GamesPerPage > 0 ? s.GamesPerPage : 50).ToString();
            HoursFarmAutoStart = s.HoursFarmAutoStart;
            HoursFarmMode      = s.HoursFarmMode;
            CardFarmMode       = s.CardFarmMode;
            CardFarmMinHours   = s.CardFarmMinHours.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void SaveSettings()
        {
            var s = _settingsService.Current;
            s.ProfileUrl         = ProfileUrl.Trim();
            s.SessionId          = SessionId.Trim().Trim('"');
            s.SteamLoginSecure   = SteamLoginSecure.Trim().Trim('"');
            s.SteamParental      = SteamParental.Trim();
            s.SteamId64          = SteamId64.Trim();
            s.SteamApiKey        = SteamApiKey.Trim();
            s.AppTheme           = SelectedTheme;
            s.Language           = SelectedLanguage;
            s.Currency           = string.IsNullOrWhiteSpace(SelectedCurrency) ? "BRL" : SelectedCurrency;
            s.GamesPerPage       = int.TryParse(GamesPerPage, out int gpp) && gpp > 0 ? gpp : 50;
            s.HoursFarmAutoStart = HoursFarmAutoStart;
            s.HoursFarmMode      = HoursFarmMode;
            s.CardFarmMode       = CardFarmMode;
            if (double.TryParse(CardFarmMinHours.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double minH))
            {
                s.CardFarmMinHours = minH;
            }

            _settingsService.Save();
            LoadFromSettings();

            // Credentials changed — force the game library to re-fetch
            // on next navigation so the new key/id is used.
            _steamWebService.InvalidateCache();

            SaveStatus = IdleSenior.UI.Services.LocalizationService.Get("Settings_Saved");
        }

        private void ClearSettings()
        {
            ProfileUrl = SessionId = SteamLoginSecure = SteamParental = SteamId64 = SteamApiKey = "";

            var s = _settingsService.Current;
            s.ProfileUrl = s.SessionId = s.SteamLoginSecure =
                s.SteamParental = s.SteamId64 = s.SteamApiKey = "";
            _settingsService.Save();

            _steamWebService.InvalidateCache();

            SaveStatus = IdleSenior.UI.Services.LocalizationService.Get("Settings_Cleared");
        }

        private static void ApplyTheme(string themeName) =>
            IdleSenior.UI.Services.ThemeService.ApplyTheme(themeName);

        private static void ApplyLanguage(string languageCode) =>
            IdleSenior.UI.Services.LocalizationService.SetLanguage(languageCode);
    }
}
