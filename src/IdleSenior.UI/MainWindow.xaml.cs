using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;
using IdleSenior.Core.Models;
using IdleSenior.Core.Services;
using IdleSenior.UI.Services;
using IdleSenior.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace IdleSenior.UI
{
    public partial class MainWindow : FluentWindow
    {
        private readonly GameInfoService _gameInfoService;
        private readonly AppSettingsService _settingsService;
        private readonly SteamWebService _steamWebService;
        private readonly SelectionService _selectionService;
        private readonly SteamIdleService _idleService;
        private readonly IdleSenior.UI.Services.OrganicAchievementSchedulerService _schedulerService;
        private readonly System.Windows.Threading.DispatcherTimer _activeStatusTimer;
        private readonly Dictionary<int, string> _appNameCache = new();
        private int _currentDetailsAppId;
        private GameDetails? _currentDetails;

        public MainWindow(
            GameInfoService gameInfoService,
            AppSettingsService settingsService,
            SteamWebService steamWebService,
            SelectionService selectionService,
            SteamIdleService idleService,
            IdleSenior.UI.Services.OrganicAchievementSchedulerService schedulerService,
            Wpf.Ui.INavigationService navigationService,
            Wpf.Ui.Abstractions.INavigationViewPageProvider pageService)
        {
            _gameInfoService = gameInfoService;
            _settingsService = settingsService;
            _steamWebService = steamWebService;
            _selectionService = selectionService;
            _idleService = idleService;
            _schedulerService = schedulerService;
            InitializeComponent();

            RootNavigation.SetPageProviderService(pageService);
            navigationService.SetNavigationControl(RootNavigation);

            _activeStatusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _activeStatusTimer.Tick += ActiveStatusTimer_Tick;
            _activeStatusTimer.Start();

            _idleService.WorkerStopped += (appId, unexpected, errMsg) =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (unexpected)
                    {
                        string gameName = GetGameName(appId);
                        string details = !string.IsNullOrWhiteSpace(errMsg)
                            ? $"\n\nMotivo:\n{errMsg}"
                            : "";
                        System.Windows.MessageBox.Show(
                            $"O farm de horas do jogo '{gameName}' foi interrompido.{details}\n\nCertifique-se de que o cliente Steam está aberto e logado na mesma conta.",
                            "Farm de Horas - Alerta",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                    UpdateHoursButtonState(_currentDetailsAppId);
                });
            };

            // Set Initial Page & preload game name cache
            Loaded += (s, e) => {
                RootNavigation.Navigate(typeof(DashboardPage));
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var games = await _steamWebService.GetOwnedGamesAsync(forceRefresh: false);
                        lock (_appNameCache)
                        {
                            foreach (var g in games)
                                _appNameCache[g.AppId] = g.Name;
                        }
                    }
                    catch { }
                });
            };
        }

        private string GetGameName(int appId)
        {
            if (_currentDetailsAppId == appId && _currentDetails != null)
                return _currentDetails.Name;
            lock (_appNameCache)
            {
                if (_appNameCache.TryGetValue(appId, out var name))
                    return name;
            }
            return $"App {appId}";
        }

        private void ActiveStatusTimer_Tick(object? sender, EventArgs e)
        {
            var activeHoursIds = _idleService.GetActiveAppIds(IdlePurpose.Hours);
            var activeCardsIds = _idleService.GetActiveAppIds(IdlePurpose.Cards);
            bool hasHoursActive = activeHoursIds.Count > 0;
            bool hasCardsActive = activeCardsIds.Count > 0;
            bool hasAchActive = _schedulerService.IsRunning;
            bool hasAnyActive = hasHoursActive || hasCardsActive || hasAchActive;

            // 1. Process hours target completion
            bool targetCompleted = false;
            foreach (var appId in activeHoursIds.ToList())
            {
                var start = _idleService.GetStartTime(appId);
                if (start.HasValue && _settingsService.Current.HoursFarmTargets.TryGetValue(appId, out double targetHours) && targetHours > 0)
                {
                    double elapsed = (DateTime.UtcNow - start.Value).TotalHours;
                    if (elapsed >= targetHours)
                    {
                        _idleService.StopIdle(appId, IdlePurpose.Hours);
                        _settingsService.Current.HoursFarmApps.Remove(appId);
                        _settingsService.Save();
                        targetCompleted = true;
                        if (_currentDetailsAppId == appId)
                        {
                            UpdateHoursButtonState(appId);
                            if (PanelHoursFeedback != null)
                                PanelHoursFeedback.Text = $"🎉 Meta de {targetHours:0.0}h atingida! Farm concluído.";
                        }
                    }
                }
            }

            // Advance sequential hours queue when a target completes
            if (targetCompleted &&
                string.Equals(_settingsService.Current.HoursFarmMode, "Sequential", StringComparison.OrdinalIgnoreCase) &&
                _idleService.GetActiveAppIds(IdlePurpose.Hours).Count == 0 &&
                _settingsService.Current.HoursFarmApps.Count > 0)
            {
                int nextAppId = _settingsService.Current.HoursFarmApps[0];
                _idleService.StartIdle(nextAppId, IdlePurpose.Hours, out _);
                activeHoursIds = _idleService.GetActiveAppIds(IdlePurpose.Hours);
                hasHoursActive = activeHoursIds.Count > 0;
                hasAnyActive = hasHoursActive || hasCardsActive || hasAchActive;
            }

            // 2. Active Hours Card Row
            if (hasHoursActive && ActiveHoursRow != null)
            {
                ActiveHoursRow.Visibility = Visibility.Visible;
                if (activeHoursIds.Count == 1)
                {
                    int appId = activeHoursIds.First();
                    var start = _idleService.GetStartTime(appId);
                    var elapsed = start.HasValue ? (DateTime.UtcNow - start.Value) : TimeSpan.Zero;
                    ActiveHoursTitle.Text = GetGameName(appId);
                    ActiveHoursTimer.Text = elapsed.ToString(@"hh\:mm\:ss");
                }
                else
                {
                    ActiveHoursTitle.Text = $"{activeHoursIds.Count} jogos farmando";
                    var firstStart = activeHoursIds.Select(id => _idleService.GetStartTime(id)).Where(dt => dt.HasValue).Min();
                    var elapsed = firstStart.HasValue ? (DateTime.UtcNow - firstStart.Value) : TimeSpan.Zero;
                    ActiveHoursTimer.Text = elapsed.ToString(@"hh\:mm\:ss");
                }
            }
            else if (ActiveHoursRow != null)
            {
                ActiveHoursRow.Visibility = Visibility.Collapsed;
            }

            // 3. Active Organic Achievements Card Row
            if (hasAchActive && ActiveAchievementsRow != null)
            {
                ActiveAchievementsRow.Visibility = Visibility.Visible;
                ActiveAchTitle.Text = !string.IsNullOrEmpty(_schedulerService.ActiveGameName)
                    ? _schedulerService.ActiveGameName
                    : "Conquistas";
                var nextEntry = _schedulerService.NextPendingEntry;
                if (nextEntry != null)
                {
                    var rem = nextEntry.TargetOffset - _schedulerService.ElapsedTime;
                    ActiveAchCountdown.Text = rem.TotalSeconds > 0 ? rem.ToString(@"mm\:ss") : "Desbloqueando...";
                }
                else
                {
                    ActiveAchCountdown.Text = "Concluindo...";
                }
            }
            else if (ActiveAchievementsRow != null)
            {
                ActiveAchievementsRow.Visibility = Visibility.Collapsed;
            }

            // 4. Active Card Farm Row
            if (hasCardsActive && ActiveCardsRow != null)
            {
                ActiveCardsRow.Visibility = Visibility.Visible;
                if (activeCardsIds.Count == 1)
                {
                    int appId = activeCardsIds.First();
                    var start = _idleService.GetStartTime(appId);
                    var elapsed = start.HasValue ? (DateTime.UtcNow - start.Value) : TimeSpan.Zero;
                    ActiveCardsTitle.Text = GetGameName(appId);
                    ActiveCardsCount.Text = elapsed.ToString(@"hh\:mm\:ss");
                }
                else
                {
                    ActiveCardsTitle.Text = $"{activeCardsIds.Count} jogos (cartas)";
                    var firstStart = activeCardsIds.Select(id => _idleService.GetStartTime(id)).Where(dt => dt.HasValue).Min();
                    var elapsed = firstStart.HasValue ? (DateTime.UtcNow - firstStart.Value) : TimeSpan.Zero;
                    ActiveCardsCount.Text = elapsed.ToString(@"hh\:mm\:ss");
                }
            }
            else if (ActiveCardsRow != null)
            {
                ActiveCardsRow.Visibility = Visibility.Collapsed;
            }

            if (ActiveTasksCard != null)
            {
                ActiveTasksCard.Visibility = hasAnyActive ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_currentDetailsAppId > 0)
            {
                UpdateHoursButtonState(_currentDetailsAppId);
            }
        }

        private void ActiveHoursRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RootNavigation.Navigate(typeof(HoursFarmQueuePage));
        }

        private void ActiveAchievementsRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RootNavigation.Navigate(typeof(AchievementEditorPage));
        }

        private void ActiveCardsRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RootNavigation.Navigate(typeof(CardFarmPage));
        }

        public async Task ShowGameDetails(int appId)
        {
            _currentDetailsAppId = appId;
            GameInfoPanel.Visibility = Visibility.Visible;
            UpdateHoursButtonState(appId);

            var details = await _gameInfoService.GetGameDetailsAsync(appId);
            if (details != null && _currentDetailsAppId == appId)
            {
                _currentDetails = details;
                GameName.Text = details.Name;
                GameDev.Text = details.Developer;

                // Store Price & Discount Badge
                if (details.IsFree)
                {
                    GamePriceText.Text = "Gratuito";
                    GameInitialPriceText.Visibility = Visibility.Collapsed;
                    GameDiscountBadge.Visibility = Visibility.Collapsed;
                }
                else if (!string.IsNullOrWhiteSpace(details.PriceFormatted))
                {
                    GamePriceText.Text = details.PriceFormatted;
                    if (details.DiscountPercent > 0)
                    {
                        GameDiscountText.Text = $"-{details.DiscountPercent}%";
                        GameDiscountBadge.Visibility = Visibility.Visible;
                        GameInitialPriceText.Text = details.InitialPriceFormatted;
                        GameInitialPriceText.Visibility = string.IsNullOrWhiteSpace(details.InitialPriceFormatted)
                            ? Visibility.Collapsed
                            : Visibility.Visible;
                    }
                    else
                    {
                        GameDiscountBadge.Visibility = Visibility.Collapsed;
                        GameInitialPriceText.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    GamePriceText.Text = "Indisponível";
                    GameInitialPriceText.Visibility = Visibility.Collapsed;
                    GameDiscountBadge.Visibility = Visibility.Collapsed;
                }
                GameDescription.Text = details.Description;
                HLTBMain.Text = details.MainStoryHours;
                HLTB100.Text = details.CompletionistHours;
                HLTBMainSource.Text = details.MainStorySource;
                HLTB100Source.Text = details.CompletionistSource;

                // Real Steam Stats
                SteamPlayers.Text = details.PlayerCount > 0 ? details.PlayerCount.ToString("N0") : "N/A";
                SteamRating.Text = details.Rating > 0
                    ? $"{details.Rating}%"
                    : (details.MetacriticScore > 0 ? $"{details.MetacriticScore} Meta" : "N/A");
                SteamRating.ToolTip = !string.IsNullOrEmpty(details.RatingDesc)
                    ? $"{details.RatingDesc} ({details.Rating}% de aprovação na Steam)"
                    : (details.MetacriticScore > 0 ? $"Metacritic: {details.MetacriticScore}" : null);

                if (GameMetaFootnote != null)
                {
                    GameMetaFootnote.Text = $"AppID {appId} • Metadados via Steam Store & SteamHunters";
                }

                // Security / Anti-Cheat Advisory Badge
                if (details.Security != null)
                {
                    SecurityBadgeTitle.Text = details.Security.AdvisoryBadgeText;
                    SecurityBadgeDesc.Text = details.Security.AdvisoryMessage;

                    if (details.Security.Level == SecurityRiskLevel.HighRisk)
                    {
                        SecurityBadgeTitle.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 85, 85));
                        SecurityBadgeBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 85, 85));
                    }
                    else if (details.Security.Level == SecurityRiskLevel.Caution)
                    {
                        SecurityBadgeTitle.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 184, 108));
                        SecurityBadgeBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 184, 108));
                    }
                    else
                    {
                        SecurityBadgeTitle.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 250, 123));
                        SecurityBadgeBorder.BorderBrush = null;
                    }
                }

                // Load banner using fallback chain + disk cache
                _ = IdleSenior.UI.Helpers.SmartImageLoader.LoadAppBannerAsync(GameBanner, appId, details.BannerUrl, 380);
            }
        }

        private void UpdateHoursButtonState(int appId)
        {
            if (PanelStartHoursBtn == null) return;
            bool isRunning = _idleService.IsIdling(appId, IdlePurpose.Hours);
            bool inQueue = _settingsService.Current.HoursFarmApps.Contains(appId);

            if (isRunning)
            {
                PanelStartHoursBtn.Content = "Parar";
                PanelStartHoursBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Danger;
                PanelStartHoursBtn.IsEnabled = true;
                if (PanelHoursFeedback != null)
                {
                    double target = _settingsService.Current.HoursFarmTargets.TryGetValue(appId, out var t) ? t : 0;
                    PanelHoursFeedback.Text = target > 0 ? $"⚡ Farmando agora (meta: {target:0.0}h)..." : "⚡ Farmando agora...";
                    PanelHoursFeedback.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 250, 123));
                }
            }
            else if (inQueue)
            {
                PanelStartHoursBtn.Content = "Farmar";
                PanelStartHoursBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
                PanelStartHoursBtn.IsEnabled = true;
                if (PanelHoursFeedback != null)
                {
                    PanelHoursFeedback.Text = "✓ Jogo na fila de farm de horas.";
                    PanelHoursFeedback.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 250, 123));
                }
            }
            else
            {
                PanelStartHoursBtn.Content = "Farmar";
                PanelStartHoursBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
                PanelStartHoursBtn.IsEnabled = true;
                if (PanelHoursFeedback != null) PanelHoursFeedback.Text = "";
            }
        }

        private async void PanelManageAchievements_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDetailsAppId <= 0) return;

            var owned = await _steamWebService.GetOwnedGamesAsync();
            var game = owned.FirstOrDefault(g => g.AppId == _currentDetailsAppId)
                ?? new GameModel
                {
                    AppId = _currentDetailsAppId,
                    Name = _currentDetails?.Name ?? $"App {_currentDetailsAppId}",
                    CustomBannerUrl = _currentDetails?.BannerUrl
                };

            _selectionService.SelectedGame = game;
            RootNavigation.Navigate(typeof(AchievementEditorPage));
        }

        private void PanelStartHours_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDetailsAppId <= 0) return;

            // If already running for hours: Stop
            if (_idleService.IsIdling(_currentDetailsAppId, IdlePurpose.Hours))
            {
                _idleService.StopIdle(_currentDetailsAppId, IdlePurpose.Hours);
                _settingsService.Current.HoursFarmApps.Remove(_currentDetailsAppId);
                _settingsService.Save();
                UpdateHoursButtonState(_currentDetailsAppId);
                return;
            }

            // Parse target hours
            double targetHours = 1.0;
            if (PanelHoursTargetInput != null &&
                double.TryParse(PanelHoursTargetInput.Text.Trim().Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed) &&
                parsed > 0)
            {
                targetHours = parsed;
            }

            bool wasInQueue = _settingsService.Current.HoursFarmApps.Contains(_currentDetailsAppId);
            _settingsService.Current.HoursFarmTargets[_currentDetailsAppId] = targetHours;
            if (!wasInQueue)
            {
                _settingsService.Current.HoursFarmApps.Add(_currentDetailsAppId);
            }
            _settingsService.Save();

            bool isSequential = string.Equals(_settingsService.Current.HoursFarmMode, "Sequential", StringComparison.OrdinalIgnoreCase);
            bool anotherHoursRunning = _idleService.GetActiveAppIds(IdlePurpose.Hours).Count > 0;

            if (isSequential && anotherHoursRunning)
            {
                UpdateHoursButtonState(_currentDetailsAppId);
                if (PanelHoursFeedback != null)
                {
                    PanelHoursFeedback.Text = "✓ Na fila sequencial (aguardando jogo atual concluir).";
                    PanelHoursFeedback.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 250, 123));
                }
                return;
            }

            // Auto-start when added (if enabled) or explicit start when already queued
            if (_settingsService.Current.HoursFarmAutoStart || wasInQueue)
            {
                bool started = _idleService.StartIdle(_currentDetailsAppId, IdlePurpose.Hours, out string? err);
                if (!started)
                {
                    System.Windows.MessageBox.Show(err ?? "Erro ao iniciar idle. Verifique se a Steam está aberta.",
                        "Farm de Horas", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }

            UpdateHoursButtonState(_currentDetailsAppId);
        }

        private void OpenInSteam_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDetailsAppId <= 0) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"steam://store/{_currentDetailsAppId}")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://store.steampowered.com/app/{_currentDetailsAppId}/")
                    {
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        private void CloseGameInfo_Click(object sender, RoutedEventArgs e)
        {
            GameInfoPanel.Visibility = Visibility.Collapsed;
        }
    }
}
