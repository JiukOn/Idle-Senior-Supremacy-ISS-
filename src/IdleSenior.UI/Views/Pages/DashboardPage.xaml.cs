using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdleSenior.Core.Models;
using IdleSenior.Core.Services;
using IdleSenior.UI.Helpers;
using IdleSenior.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IdleSenior.UI.Views.Pages
{
    public partial class DashboardPage : Page
    {
        private readonly SteamWebService _steamWebService;
        private readonly SteamLibraryService _libraryService;
        private readonly AppSettingsService _settingsService;
        private readonly SteamIdleService _idleService;
        private readonly OrganicAchievementSchedulerService _schedulerService;
        private readonly AccountAnalyticsService _analyticsService;

        private AccountAnalyticsModel _currentModel = new();
        private List<GameModel> _allOwnedGames = new();
        private GameModel? _suggestedGame;

        private static readonly string[] _triviaFacts = new[]
        {
            "Em 2003, a Steam foi lançada originalmente pela Valve apenas como um cliente de atualização obrigatório para o Counter-Strike 1.6.",
            "O primeiro jogo de terceiros lançado na plataforma Steam foi 'Rag Doll Kung Fu', em outubro de 2005.",
            "Gabe Newell trabalhou na Microsoft por 13 anos e foi produtor das primeiras três versões do Windows antes de fundar a Valve.",
            "O recorde histórico de jogadores simultâneos em um único jogo na Steam pertence ao PUBG: Battlegrounds, com mais de 3.25 milhões de jogadores.",
            "O sistema de Cartas Colecionáveis da Steam (Trading Cards) estreou em 2013 e hoje movimenta milhões de transações diárias no Mercado da Comunidade.",
            "A Steam conta atualmente com mais de 100.000 jogos em seu catálogo e alcançou o pico de mais de 38 milhões de usuários online simultâneos em 2024.",
            "Team Fortress 2 passou por mais de 9 anos de desenvolvimento ('Vaporware') e trocou de estilo realista para o estilo artístico 'cartoon' icônico.",
            "A Valve recusou ofertas bilionárias de aquisição de grandes conglomerados como a EA, mantendo-se 100% independente e privada até hoje.",
            "O Steam Deck opera com SteamOS baseado em Arch Linux, executando jogos do Windows sem modificação através da camada Proton (Wine + DXVK/VKD3D).",
            "O 'Steam Summer Sale' de 2011 foi a primeira grande promoção a introduzir minigames interativos com insígnias e itens cosméticos de evento."
        };
        private int _currentTriviaIndex = 0;

        public DashboardPage(
            SteamWebService steamWebService,
            SteamLibraryService libraryService,
            AppSettingsService settingsService,
            SteamIdleService idleService,
            OrganicAchievementSchedulerService schedulerService,
            AccountAnalyticsService analyticsService)
        {
            InitializeComponent();
            _steamWebService = steamWebService;
            _libraryService = libraryService;
            _settingsService = settingsService;
            _idleService = idleService;
            _schedulerService = schedulerService;
            _analyticsService = analyticsService;

            Loaded += DashboardPage_Loaded;
        }

        private async void DashboardPage_Loaded(object sender, RoutedEventArgs e) =>
            await LoadDashboardAsync(forceRefresh: false);

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshBtn.IsEnabled = false;
            try { await LoadDashboardAsync(forceRefresh: true); }
            finally { RefreshBtn.IsEnabled = true; }
        }

        private async Task LoadDashboardAsync(bool forceRefresh)
        {
            if (forceRefresh)
                _steamWebService.InvalidateCache();

            // 1. Instant Cache Load
            _currentModel = await _analyticsService.GetOrLoadCachedAnalyticsAsync();
            ApplyModelToUi(_currentModel);

            // 2. Load recent games & owned games concurrently
            var recentTask = LoadRecentSectionAsync(forceRefresh);
            var ownedGamesTask = _steamWebService.GetOwnedGamesAsync(forceRefresh);

            await Task.WhenAll(recentTask, ownedGamesTask);
            var recentGames = await recentTask;
            var ownedGames = await ownedGamesTask;

            _allOwnedGames = ownedGames ?? new List<GameModel>();

            // 3. Pick Suggested Game
            PickRandomSuggestedGame();

            // 4. Update Library Diagnosis
            _analyticsService.CalculateLibraryDiagnosis(_allOwnedGames, _currentModel);
            ApplyDiagnosisToUi(_currentModel);

            // 5. Fetch News for recent / top games
            _ = Task.Run(async () =>
            {
                try
                {
                    var targetForNews = (recentGames != null && recentGames.Count > 0)
                        ? recentGames
                        : _allOwnedGames.Take(5).ToList();

                    var news = await _analyticsService.GetRecentGamesNewsAsync(targetForNews);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (SteamNewsItemsControl != null)
                        {
                            SteamNewsItemsControl.ItemsSource = news;
                        }
                    });
                }
                catch { }
            });

            // 6. Fetch Profile & Achievements in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await _analyticsService.FetchProfileSummaryAsync(_currentModel);
                    await _analyticsService.FetchAchievementsSummaryAsync(_currentModel, forceRefresh: false);
                    await _analyticsService.GetOrFetchAccountValueAsync(_allOwnedGames, _currentModel, forceRefresh: false);
                    _analyticsService.SaveAnalyticsCache(_currentModel);

                    await Dispatcher.InvokeAsync(() => ApplyModelToUi(_currentModel));
                }
                catch { }
            });
        }

        private void ApplyModelToUi(AccountAnalyticsModel m)
        {
            // Profile Header
            AccountPersonaName.Text = !string.IsNullOrWhiteSpace(m.PersonaName) ? m.PersonaName : "Jogador Steam";
            AccountLevelText.Text = $"Nível {m.SteamLevel}";
            AccountSubtitleText.Text = $"{m.TotalOwnedGames} jogos • {m.TotalPlaytimeHours:N0}h registradas (~{m.TotalPlaytimeDays:N0} dias)";

            if (!string.IsNullOrWhiteSpace(m.AvatarUrl))
            {
                try
                {
                    AccountAvatarImg.Source = new BitmapImage(new Uri(m.AvatarUrl));
                }
                catch { }
            }

            // VAC Status
            if (VacStatusBadge != null && VacStatusText != null)
            {
                if (m.VacBanned)
                {
                    VacStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xff, 0x55, 0x55));
                    VacStatusText.Text = "🔴 Banimento VAC";
                    VacStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0x55, 0x55));
                }
                else
                {
                    VacStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x50, 0xfa, 0x7b));
                    VacStatusText.Text = "🟢 VAC Limpo";
                    VacStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0xfa, 0x7b));
                }
            }

            // Trade Ban Status
            if (TradeStatusBadge != null && TradeStatusText != null)
            {
                if (m.TradeBanState?.Equals("None", StringComparison.OrdinalIgnoreCase) == true || string.IsNullOrWhiteSpace(m.TradeBanState))
                {
                    TradeStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x8b, 0xe9, 0xfd));
                    TradeStatusText.Text = "🟢 Comércio Ativo";
                    TradeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x8b, 0xe9, 0xfd));
                }
                else
                {
                    TradeStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xff, 0xb8, 0x6c));
                    TradeStatusText.Text = "🟡 Comércio Restrito";
                    TradeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0xb8, 0x6c));
                }
            }

            // Member Since
            if (AccountMemberSinceText != null)
            {
                if (!string.IsNullOrWhiteSpace(m.MemberSince))
                {
                    AccountMemberSinceText.Text = $"📅 {m.MemberSince}";
                    AccountMemberSinceText.Visibility = Visibility.Visible;
                }
                else
                {
                    AccountMemberSinceText.Visibility = Visibility.Collapsed;
                }
            }

            // Level XP Progress
            if (AccountXpProgressBar != null && AccountXpText != null)
            {
                if (m.XpToNextLevel > 0 || m.CurrentLevelXp > 0)
                {
                    int totalXpBand = m.CurrentLevelXp + m.XpToNextLevel;
                    AccountXpProgressBar.Maximum = totalXpBand > 0 ? totalXpBand : 100;
                    AccountXpProgressBar.Value = m.CurrentLevelXp;
                    AccountXpText.Text = m.TotalXp > 0
                        ? $"{m.CurrentLevelXp} / {totalXpBand} XP ({m.TotalXp:N0} XP)"
                        : $"{m.XpToNextLevel:N0} XP para o Nível {m.SteamLevel + 1}";
                    AccountXpProgressBar.Visibility = Visibility.Visible;
                    AccountXpText.Visibility = Visibility.Visible;
                }
                else
                {
                    AccountXpProgressBar.Visibility = Visibility.Collapsed;
                    AccountXpText.Visibility = Visibility.Collapsed;
                }
            }

            ApplyDiagnosisToUi(m);
            ApplyAchievementsToUi(m);
            ApplyPricesToUi(m);
        }

        private void ApplyDiagnosisToUi(AccountAnalyticsModel m)
        {
            DiagnosisTotalGames.Text = m.TotalOwnedGames.ToString("N0");
            DiagnosisTotalHours.Text = $"{m.TotalPlaytimeHours:N0}h jogadas";

            DiagnosisNeverPlayedText.Text = $"{m.NeverPlayedCount} ({m.NeverPlayedPercent:F1}%)";
            DiagnosisUnder5hText.Text = $"{m.PlayedUnder5hCount} ({m.PlayedUnder5hPercent:F1}%)";
            DiagnosisOver5hText.Text = $"{m.PlayedOver5hCount} ({m.PlayedOver5hPercent:F1}%)";

            // Visual bar proportions
            if (m.TotalOwnedGames <= 0)
            {
                ColBarNever.Width = new GridLength(1, GridUnitType.Star);
                ColBarUnder5.Width = new GridLength(0.001, GridUnitType.Star);
                ColBarOver5.Width = new GridLength(0.001, GridUnitType.Star);
            }
            else
            {
                ColBarNever.Width = new GridLength(m.NeverPlayedCount > 0 ? m.NeverPlayedCount : 0.001, GridUnitType.Star);
                ColBarUnder5.Width = new GridLength(m.PlayedUnder5hCount > 0 ? m.PlayedUnder5hCount : 0.001, GridUnitType.Star);
                ColBarOver5.Width = new GridLength(m.PlayedOver5hCount > 0 ? m.PlayedOver5hCount : 0.001, GridUnitType.Star);
            }
        }

        private void ApplyAchievementsToUi(AccountAnalyticsModel m)
        {
            AchUnlockedBig.Text = m.TotalAchievementsUnlocked.ToString("N0");
            AchCompletionRateText.Text = m.CompletionRate > 0 ? $"{m.CompletionRate:F1}% conc." : "";
            AchProgressBar.Value = Math.Clamp(m.CompletionRate, 0, 100);

            AchPerfectGamesText.Text = $"{m.PerfectGamesCount} platinas";
            AchUnlockedOfTotalText.Text = $"{m.TotalAchievementsUnlocked:N0} conquistas";
            AchHunterPointsText.Text = $"{m.HunterPoints:N0} pts";
        }

        private void ApplyPricesToUi(AccountAnalyticsModel m)
        {
            string sym = string.IsNullOrWhiteSpace(m.CurrencySymbol) ? "R$" : m.CurrencySymbol;
            if (m.TotalStoreValueBrl > 0 || m.TotalCurrentDiscountValueBrl > 0)
            {
                PriceStoreTotalText.Text = $"{sym} {m.EffectiveTodayValueBrl:N2}";
                PriceDiscountTotalText.Text = $"{sym} {m.TotalStoreValueBrl:N2}";
                PriceAverageText.Text = $"{sym} {m.AverageGameValueBrl:N2}";
                if (PricePerHourText != null)
                {
                    PricePerHourText.Text = m.AveragePricePerHourPaidBrl > 0
                        ? $"{sym} {m.AveragePricePerHourPaidBrl:N2}/h"
                        : $"{sym} —";
                }
                if (PricePaidCountText != null)
                {
                    PricePaidCountText.Text = $"{m.PaidGamesCount} jogos";
                }
            }
            else
            {
                PriceStoreTotalText.Text = $"{sym} —";
                PriceDiscountTotalText.Text = $"{sym} —";
                PriceAverageText.Text = $"{sym} —";
                if (PricePerHourText != null)
                {
                    PricePerHourText.Text = $"{sym} —";
                }
                if (PricePaidCountText != null)
                {
                    PricePaidCountText.Text = "—";
                }
            }
        }

        private void PickRandomSuggestedGame()
        {
            if (_allOwnedGames == null || _allOwnedGames.Count == 0) return;

            // Prefer unplayed games (0h) or lightly played (< 5h) to encourage discovery, but fallback to any
            var candidates = _allOwnedGames.Where(g => g.PlaytimeHours == 0).ToList();
            if (candidates.Count == 0)
                candidates = _allOwnedGames.Where(g => g.PlaytimeHours < 5).ToList();
            if (candidates.Count == 0)
                candidates = _allOwnedGames;

            var rng = Random.Shared;
            _suggestedGame = candidates[rng.Next(candidates.Count)];

            if (SuggestedGameTitle != null) SuggestedGameTitle.Text = _suggestedGame.Name;
            if (SuggestedGameHours != null)
            {
                SuggestedGameHours.Text = _suggestedGame.PlaytimeHours == 0
                    ? "Nunca jogado (0h registradas)"
                    : $"{_suggestedGame.PlaytimeHours:N1}h registradas";
            }
            if (SuggestedGameYear != null)
            {
                SuggestedGameYear.Text = _suggestedGame.ReleaseYear > 0
                    ? $"Lançamento: {_suggestedGame.ReleaseYear}"
                    : $"AppID: {_suggestedGame.AppId}";
            }

            if (SuggestedGameBanner != null)
            {
                SmartImageLoader.SetDecodeWidth(SuggestedGameBanner, 220);
                SmartImageLoader.SetFallbackUrls(SuggestedGameBanner, _suggestedGame.BannerFallbackUrls);
            }
        }

        private void RerollSuggestion_Click(object sender, RoutedEventArgs e)
        {
            PickRandomSuggestedGame();
        }

        private void ViewSuggestedGame_Click(object sender, RoutedEventArgs e)
        {
            if (_suggestedGame != null && Window.GetWindow(this) is MainWindow mainWindow)
            {
                _ = mainWindow.ShowGameDetails(_suggestedGame.AppId);
            }
        }

        private void NewsItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is SteamNewsItem newsItem && !string.IsNullOrWhiteSpace(newsItem.Url))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = newsItem.Url,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        private void NextTrivia_Click(object sender, RoutedEventArgs e)
        {
            _currentTriviaIndex = (_currentTriviaIndex + 1) % _triviaFacts.Length;
            if (GamerTriviaText != null)
            {
                GamerTriviaText.Text = _triviaFacts[_currentTriviaIndex];
            }
        }

        private async void SyncAchievements_Click(object sender, RoutedEventArgs e)
        {
            if (SyncAchievementsBtn != null) SyncAchievementsBtn.IsEnabled = false;
            if (AchCacheStatusText != null) AchCacheStatusText.Text = "Sincronizando conquistas e platinas...";

            try
            {
                await _analyticsService.FetchAchievementsSummaryAsync(_currentModel, forceRefresh: true);
                _analyticsService.SaveAnalyticsCache(_currentModel);
                ApplyAchievementsToUi(_currentModel);
                if (AchCacheStatusText != null)
                    AchCacheStatusText.Text = $"✅ Atualizado! {_currentModel.PerfectGamesCount} platinas encontradas.";
            }
            catch (Exception ex)
            {
                if (AchCacheStatusText != null)
                    AchCacheStatusText.Text = $"Erro: {ex.Message}";
            }
            finally
            {
                if (SyncAchievementsBtn != null) SyncAchievementsBtn.IsEnabled = true;
            }
        }

        private async Task<List<GameModel>> LoadRecentSectionAsync(bool forceRefresh)
        {
            try
            {
                var recentGames = await _steamWebService.GetRecentGamesAsync(forceRefresh);
                RecentGamesCarousel.ItemsSource = recentGames;
                return recentGames ?? new List<GameModel>();
            }
            catch
            {
                RecentGamesCarousel.ItemsSource = null;
                return new List<GameModel>();
            }
        }

        private void GameCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameModel game)
            {
                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    _ = mainWindow.ShowGameDetails(game.AppId);
                }
            }
        }

        private void GoToLibrary_Click(object sender, RoutedEventArgs e)
        {
            var nav = App.ServiceProvider.GetRequiredService<Wpf.Ui.INavigationService>();
            nav.Navigate(typeof(LibraryPage));
        }

        private async void SyncPrices_Click(object sender, RoutedEventArgs e)
        {
            if (SyncProgressCard == null) return;

            if (CalcPricesBtn != null) CalcPricesBtn.IsEnabled = false;

            SyncProgressCard.Visibility = Visibility.Visible;
            SyncProgressBar.Value = 0;
            SyncPercentText.Text = "0%";
            SyncStatusText.Text = "Consultando preços atuais da Steam Store...";

            try
            {
                var ownedGames = await _steamWebService.GetOwnedGamesAsync(forceRefresh: false);
                var progress = new Progress<(int Current, int Total)>(p =>
                {
                    int pct = p.Total > 0 ? (int)Math.Round((double)p.Current / p.Total * 100) : 0;
                    SyncProgressBar.Value = pct;
                    SyncPercentText.Text = $"{pct}%";
                    SyncStatusText.Text = $"Atualizando valores da loja: {p.Current} / {p.Total} jogos...";
                });

                await _analyticsService.GetOrFetchAccountValueAsync(ownedGames, _currentModel, forceRefresh: true, progress);
                _analyticsService.SaveAnalyticsCache(_currentModel);

                ApplyPricesToUi(_currentModel);

                SyncStatusText.Text = $"✅ Preços atualizados! Total: R$ {_currentModel.TotalStoreValueBrl:N2}";
                SyncProgressBar.Value = 100;
                SyncPercentText.Text = "100%";
                await Task.Delay(3000);
                SyncProgressCard.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                SyncStatusText.Text = $"Erro ao calcular preços: {ex.Message}";
                await Task.Delay(3500);
                SyncProgressCard.Visibility = Visibility.Collapsed;
            }
            finally
            {
                if (CalcPricesBtn != null) CalcPricesBtn.IsEnabled = true;
            }
        }

        private async void SyncOffline_Click(object sender, RoutedEventArgs e)
        {
            if (SyncOfflineBtn == null || SyncProgressCard == null) return;
            SyncOfflineBtn.IsEnabled = false;
            SyncProgressCard.Visibility = Visibility.Visible;
            SyncProgressBar.Value = 0;
            SyncPercentText.Text = "0%";
            SyncStatusText.Text = "Obtendo jogos da conta Steam...";

            try
            {
                var games = await _steamWebService.GetOwnedGamesAsync(forceRefresh: true);
                int total = games.Count;
                if (total == 0)
                {
                    SyncStatusText.Text = "Nenhum jogo encontrado para sincronizar.";
                    await Task.Delay(3000);
                    SyncProgressCard.Visibility = Visibility.Collapsed;
                    return;
                }

                int completed = 0;
                var semaphore = new System.Threading.SemaphoreSlim(8);

                var tasks = games.Select(async game =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await SmartImageLoader.PreloadImageAsync(game.BannerFallbackUrls);
                    }
                    finally
                    {
                        semaphore.Release();
                        int current = System.Threading.Interlocked.Increment(ref completed);
                        int pct = (int)Math.Round((double)current / total * 100);
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            SyncProgressBar.Value = pct;
                            SyncPercentText.Text = $"{pct}%";
                            SyncStatusText.Text = $"Baixando capas offline: {current} / {total} jogos...";
                        });
                    }
                }).ToList();

                await Task.WhenAll(tasks);

                SyncStatusText.Text = $"✅ Sincronização concluída! {total} jogos salvos no cache local.";
                SyncProgressBar.Value = 100;
                SyncPercentText.Text = "100%";
                await Task.Delay(3500);
                SyncProgressCard.Visibility = Visibility.Collapsed;
                await LoadDashboardAsync(forceRefresh: false);
            }
            catch (Exception ex)
            {
                SyncStatusText.Text = $"Erro na sincronização: {ex.Message}";
                await Task.Delay(4000);
                SyncProgressCard.Visibility = Visibility.Collapsed;
            }
            finally
            {
                SyncOfflineBtn.IsEnabled = true;
            }
        }
    }
}
