using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using IdleSenior.Core.Services;
using IdleSenior.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using IdleSenior.UI.Services;
using static IdleSenior.UI.Services.LocalizationService;

namespace IdleSenior.UI.Views.Pages
{
    public partial class AchievementsPage : Page
    {
        private readonly SteamWebService _steamWebService;
        private readonly AppSettingsService _settingsService;
        private List<GameModel> _allGames = new List<GameModel>();
        private List<GameModel> _filteredGames = new List<GameModel>();
        private bool _isAscending = true;
        private int _currentPage = 1;

        // Cancelled when the page is unloaded so in-flight badge-page scraping stops.
        private CancellationTokenSource _loadCts = new CancellationTokenSource();
        // Guard against re-entrant loads (e.g. fast tab switching).
        private bool _loading;

        public AchievementsPage(SteamWebService steamWebService, AppSettingsService settingsService)
        {
            _steamWebService = steamWebService;
            _settingsService = settingsService;
            InitializeComponent();
            Loaded   += AchievementsPage_Loaded;
            Unloaded += AchievementsPage_Unloaded;
        }

        private void AchievementsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Cancel any ongoing HTTP scraping started by this page instance.
            _loadCts.Cancel();
        }

        private async void AchievementsPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Prevent a second concurrent load if the Loaded event fires again
            // before the first finishes (e.g. rapid tab switching in WPF-UI).
            if (_loading) return;
            _loading = true;

            // Reset the token in case the page was unloaded and re-loaded.
            _loadCts.Dispose();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;

            if (LoadingRing != null) LoadingRing.Visibility = Visibility.Visible;
            if (GamesGrid   != null) GamesGrid.Visibility   = Visibility.Collapsed;

            try
            {
                // Step 1 – game list (fast: uses singleton cache after first load)
                _allGames = await _steamWebService.GetOwnedGamesAsync();

                if (ct.IsCancellationRequested) return;

                ApplyFilters();

                if (LoadingRing != null) LoadingRing.Visibility = Visibility.Collapsed;
                if (GamesGrid   != null) GamesGrid.Visibility   = Visibility.Visible;

                // Step 2 – enrich with achievement % in background so the list
                // appears immediately; stats fill in once scraping completes.
                await _steamWebService.EnrichWithAchievementStatsAsync();

                if (ct.IsCancellationRequested) return;

                ApplyFilters();
            }
            catch (OperationCanceledException)
            {
                // Normal: page was navigated away while loading.
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                if (LoadingRing != null) LoadingRing.Visibility = Visibility.Collapsed;
                if (GamesGrid   != null) GamesGrid.Visibility   = Visibility.Visible;
                MessageBox.Show(
                    $"{Get("Err_LoadAchievements")}: {ex.Message}\n\n{Get("Err_SteamOpen")}",
                    Get("Word_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _loading = false;
                if (LoadingRing != null && LoadingRing.Visibility == Visibility.Visible)
                    LoadingRing.Visibility = Visibility.Collapsed;
                if (GamesGrid != null && GamesGrid.Visibility == Visibility.Collapsed)
                    GamesGrid.Visibility = Visibility.Visible;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void SortDirection_Click(object sender, RoutedEventArgs e)
        {
            _isAscending = !_isAscending;
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (_allGames == null || SortByCombo == null || SortDirectionButton == null || SearchBox == null || GamesGrid == null) return;

            string filter = SearchBox.Text.ToLower();
            var query = _allGames.Where(g => g.Name.ToLower().Contains(filter));

            // Sorting
            bool isAscending = _isAscending;
            string? sortCriteria = (SortByCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

            query = sortCriteria switch
            {
                "Playtime" => isAscending
                    ? query.OrderBy(g => g.PlaytimeHours)
                    : query.OrderByDescending(g => g.PlaytimeHours),
                "LastPlayed" => isAscending
                    ? query.OrderBy(g => g.LastPlayedUnix)
                    : query.OrderByDescending(g => g.LastPlayedUnix),
                // Games with no achievement data always at the end
                "Completion" => isAscending
                    ? query.OrderBy(g => g.TotalAchievements == 0).ThenBy(g => g.CompletionPercentage)
                    : query.OrderBy(g => g.TotalAchievements == 0).ThenByDescending(g => g.CompletionPercentage),
                _ => isAscending ? query.OrderBy(g => g.Name) : query.OrderByDescending(g => g.Name)
            };

            _filteredGames = query.ToList();
            UpdatePagedView();
        }

        private void UpdatePagedView()
        {
            if (GamesGrid == null || PageInfoText == null) return;

            int pageSize = _settingsService?.Current.GamesPerPage ?? 50;
            if (pageSize <= 0) pageSize = 50;

            int totalItems = _filteredGames.Count;
            int totalPages = (int)Math.Ceiling((double)totalItems / pageSize);
            if (totalPages < 1) totalPages = 1;

            if (_currentPage < 1) _currentPage = 1;
            if (_currentPage > totalPages) _currentPage = totalPages;

            var paged = _filteredGames.Skip((_currentPage - 1) * pageSize).Take(pageSize).ToList();
            GamesGrid.ItemsSource = paged;

            PageInfoText.Text = $"Página {_currentPage} de {totalPages} ({paged.Count} exibidos)";
            if (PrevPageBtn != null) PrevPageBtn.IsEnabled = _currentPage > 1;
            if (NextPageBtn != null) NextPageBtn.IsEnabled = _currentPage < totalPages;

            AchievementsScrollViewer?.ScrollToTop();
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                UpdatePagedView();
            }
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            int pageSize = _settingsService?.Current.GamesPerPage ?? 50;
            if (pageSize <= 0) pageSize = 50;
            int totalPages = (int)Math.Ceiling((double)_filteredGames.Count / pageSize);

            if (_currentPage < totalPages)
            {
                _currentPage++;
                UpdatePagedView();
            }
        }

        private void GameCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameModel game)
            {
                var selectionService = App.ServiceProvider.GetRequiredService<SelectionService>();
                selectionService.SelectedGame = game;

                var navigationService = App.ServiceProvider.GetRequiredService<Wpf.Ui.INavigationService>();
                navigationService.Navigate(typeof(AchievementEditorPage));
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                if (scrollViewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
                {
                    scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
                    e.Handled = true;
                }
            }
        }
    }
}
