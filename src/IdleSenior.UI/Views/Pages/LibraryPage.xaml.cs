using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IdleSenior.Core.Models;
using IdleSenior.Core.Services;
using static IdleSenior.UI.Services.LocalizationService;

namespace IdleSenior.UI.Views.Pages
{
    public partial class LibraryPage : Page
    {
        private readonly SteamWebService _steamWebService;
        private readonly AppSettingsService _settingsService;
        private List<GameModel> _allGames = new List<GameModel>();
        private List<GameModel> _filteredGames = new List<GameModel>();
        private bool _isAscending = true;
        private int _currentPage = 1;

        public LibraryPage(SteamWebService steamWebService, AppSettingsService settingsService)
        {
            _steamWebService = steamWebService;
            _settingsService = settingsService;
            InitializeComponent();

            _settingsService.GamesPerPageChanged += (newSize) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _currentPage = 1;
                    UpdatePagedView();
                });
            };

            Loaded += LibraryPage_Loaded;
        }

        private async void LibraryPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (LoadingRing != null) LoadingRing.Visibility = Visibility.Visible;
            try
            {
                _allGames = await _steamWebService.GetOwnedGamesAsync();
                ApplyFilters();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Erro ao carregar biblioteca: {ex.Message}\n\nVerifique as configurações.",
                    "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (LoadingRing != null) LoadingRing.Visibility = Visibility.Collapsed;
            }

            // Enrich with achievement completion stats in background so % badges and Completion sort work
            _ = EnrichAchievementsInBackgroundAsync();
        }

        private async System.Threading.Tasks.Task EnrichAchievementsInBackgroundAsync()
        {
            try
            {
                await _steamWebService.EnrichWithAchievementStatsAsync();
                _ = Dispatcher.InvokeAsync(ApplyFilters);
            }
            catch { }
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
            if (_allGames == null || SortByCombo == null || SortDirectionButton == null || SearchBox == null || GamesGrid == null || GameCountText == null) return;

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
                // Games with no data (0%) always go to the end regardless of direction
                "Completion" => isAscending
                    ? query.OrderBy(g => g.TotalAchievements == 0).ThenBy(g => g.CompletionPercentage)
                    : query.OrderBy(g => g.TotalAchievements == 0).ThenByDescending(g => g.CompletionPercentage),
                // Games with unknown release year (0) always go to the end
                "Release" => isAscending
                    ? query.OrderBy(g => g.ReleaseYear == 0).ThenBy(g => g.ReleaseYear)
                    : query.OrderBy(g => g.ReleaseYear == 0).ThenByDescending(g => g.ReleaseYear),
                _ => isAscending ? query.OrderBy(g => g.Name) : query.OrderByDescending(g => g.Name)
            };

            _filteredGames = query.ToList();
            GameCountText.Text = $"{_filteredGames.Count} {Get("Library_GamesSuffix")}";
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

            LibraryScrollViewer?.ScrollToTop();
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
                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    _ = mainWindow.ShowGameDetails(game.AppId);
                }
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
