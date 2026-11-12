using IdleSenior.Core.Services;

namespace IdleSenior.UI.ViewModels
{
    public class DashboardViewModel : ViewModelBase
    {
        private readonly AppSettingsService _settingsService;

        private string _profileUrl = "";
        private string _steamId = "";
        private uint _totalCardsIdled;
        private uint _totalMinutesIdled;
        private bool _isLoggedIn;
        private string _loginStatus = "";

        public string ProfileUrl { get => _profileUrl; set => SetField(ref _profileUrl, value); }
        public string SteamId { get => _steamId; set => SetField(ref _steamId, value); }
        public uint TotalCardsIdled { get => _totalCardsIdled; set => SetField(ref _totalCardsIdled, value); }
        public uint TotalMinutesIdled { get => _totalMinutesIdled; set => SetField(ref _totalMinutesIdled, value); }
        public bool IsLoggedIn { get => _isLoggedIn; set => SetField(ref _isLoggedIn, value); }
        public string LoginStatus { get => _loginStatus; set => SetField(ref _loginStatus, value); }

        public string TotalHoursIdled => $"{_totalMinutesIdled / 60.0:F1}h";

        public DashboardViewModel(AppSettingsService settingsService)
        {
            _settingsService = settingsService;
            Refresh();
        }

        public void Refresh()
        {
            var s = _settingsService.Current;
            ProfileUrl = s.ProfileUrl;
            SteamId = s.SteamId64;
            TotalCardsIdled = s.TotalCardsIdled;
            TotalMinutesIdled = s.TotalMinutesIdled;
            IsLoggedIn = _settingsService.IsLoggedIn;
            LoginStatus = IsLoggedIn ? "Conectado ao Steam" : "Não conectado — configure os cookies em Configurações";
            OnPropertyChanged(nameof(TotalHoursIdled));
        }
    }
}
