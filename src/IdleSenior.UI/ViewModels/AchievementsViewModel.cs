using System.Collections.ObjectModel;
using System.Windows.Input;
using IdleSenior.Core.Models;
using IdleSenior.Core.Services;

namespace IdleSenior.UI.ViewModels
{
    public class AchievementItem : ViewModelBase
    {
        private bool _isUnlocked;
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string ApiName { get; set; } = "";
        public bool IsUnlocked { get => _isUnlocked; set => SetField(ref _isUnlocked, value); }
    }

    public class AchievementsViewModel : ViewModelBase
    {
        private readonly GameInfoService _gameInfoService;

        private string _appIdInput = "";
        private string _gameTitle = "";
        private bool _isLoading;
        private string _statusMessage = "Insira um AppID e clique em Carregar.";
        private GameDetails? _currentGame;

        public ObservableCollection<AchievementItem> Achievements { get; } = new();

        public string AppIdInput { get => _appIdInput; set => SetField(ref _appIdInput, value); }
        public string GameTitle { get => _gameTitle; set => SetField(ref _gameTitle, value); }
        public bool IsLoading { get => _isLoading; set => SetField(ref _isLoading, value); }
        public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

        public ICommand LoadGameCommand { get; }
        public ICommand UnlockAllCommand { get; }
        public ICommand LockAllCommand { get; }

        public AchievementsViewModel(GameInfoService gameInfoService)
        {
            _gameInfoService = gameInfoService;
            LoadGameCommand = new RelayCommand(async () => await LoadGameAsync(), () => !IsLoading && int.TryParse(AppIdInput, out _));
            UnlockAllCommand = new RelayCommand(UnlockAll, () => Achievements.Count > 0);
            LockAllCommand = new RelayCommand(LockAll, () => Achievements.Count > 0);
        }

        private async Task LoadGameAsync()
        {
            if (!int.TryParse(AppIdInput, out var appId)) return;

            IsLoading = true;
            Achievements.Clear();
            StatusMessage = "Carregando informações do jogo...";

            try
            {
                _currentGame = await _gameInfoService.GetGameDetailsAsync(appId);
                if (_currentGame != null)
                {
                    GameTitle = _currentGame.Name;
                    StatusMessage = $"Jogo carregado: {_currentGame.Name}. Conquistas requerem steam_api.dll para listar.";
                }
                else
                {
                    GameTitle = "";
                    StatusMessage = "Jogo não encontrado. Verifique o AppID.";
                }
            }
            catch
            {
                StatusMessage = "Erro ao carregar o jogo.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void UnlockAll()
        {
            foreach (var a in Achievements)
                a.IsUnlocked = true;
            StatusMessage = $"{Achievements.Count} conquista(s) desbloqueadas.";
        }

        private void LockAll()
        {
            foreach (var a in Achievements)
                a.IsUnlocked = false;
            StatusMessage = "Conquistas bloqueadas.";
        }
    }
}
