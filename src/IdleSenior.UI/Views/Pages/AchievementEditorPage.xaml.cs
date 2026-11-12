using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using IdleSenior.Core.Models;
using IdleSenior.Core.Services;
using IdleSenior.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Controls;
using static IdleSenior.UI.Services.LocalizationService;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace IdleSenior.UI.Views.Pages
{
    public partial class AchievementEditorPage : Page, INavigableView<object>
    {
        private readonly SteamAchievementService _achievementService;
        private readonly SelectionService _selectionService;
        private readonly SteamHuntersService _steamHuntersService;
        private readonly HowLongToBeatService _hltbService;
        private readonly AchievementTimelineEngine _timelineEngine;
        private readonly OrganicAchievementSchedulerService _schedulerService;
        private readonly SteamGuidesService _guidesService;

        private List<AchievementModel> _allAchievements = new();
        private List<AchievementModel> _filteredAchievements = new();
        private int _currentAchPage = 1;
        private int _achievementsPerPage = 50;
        private SteamHuntersAppInfo? _shAppInfo;
        private HltbResult? _hltbInfo;
        private int _appId;
        private bool _isAscending = true;
        private bool _suppressPacingEvents;

        // Tracks which game was last loaded so we only reload when the selection changes.
        private GameModel? _loadedGame;

        public AchievementEditorPage(
            SteamAchievementService achievementService,
            SelectionService selectionService,
            SteamHuntersService steamHuntersService,
            HowLongToBeatService hltbService,
            AchievementTimelineEngine timelineEngine,
            OrganicAchievementSchedulerService schedulerService,
            SteamGuidesService guidesService)
        {
            _achievementService = achievementService;
            _selectionService = selectionService;
            _steamHuntersService = steamHuntersService;
            _hltbService = hltbService;
            _timelineEngine = timelineEngine;
            _schedulerService = schedulerService;
            _guidesService = guidesService;

            InitializeComponent();

            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                {
                    ReloadIfGameChanged();
                    UpdateSchedulerUi();
                }
            };
        }

        public object ViewModel { get; } = new object();

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _schedulerService.StateChanged -= OnSchedulerStateChanged;
            _schedulerService.StateChanged += OnSchedulerStateChanged;

            _schedulerService.AchievementUnlocked -= OnSchedulerAchievementUnlocked;
            _schedulerService.AchievementUnlocked += OnSchedulerAchievementUnlocked;

            _schedulerService.ScheduleCompleted -= OnSchedulerCompleted;
            _schedulerService.ScheduleCompleted += OnSchedulerCompleted;

            ReloadIfGameChanged();
            UpdateSchedulerUi();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            _schedulerService.StateChanged -= OnSchedulerStateChanged;
            _schedulerService.AchievementUnlocked -= OnSchedulerAchievementUnlocked;
            _schedulerService.ScheduleCompleted -= OnSchedulerCompleted;
        }

        private void OnSchedulerStateChanged()
        {
            Dispatcher.InvokeAsync(UpdateSchedulerUi);
        }

        private void OnSchedulerAchievementUnlocked(int appId, string achievementId, DateTime unlockTime)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (appId != _appId) return;

                var ach = _allAchievements.FirstOrDefault(a =>
                    a.Id.Equals(achievementId, StringComparison.OrdinalIgnoreCase));

                if (ach != null)
                {
                    ach.IsAchieved = true;
                    ach.UnlockTime = unlockTime;
                    ach.ScheduledOffset = null;
                    ach.UpdateScheduleBadgeText();
                }

                UpdateStats();
                UpdateSchedulerUi();
            });
        }

        private void OnSchedulerCompleted(int appId, string gameName)
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateSchedulerUi();
                MessageBox.Show(
                    $"🎉 Cronograma orgânico concluído para {gameName}!\nTodas as conquistas planejadas foram desbloqueadas e o farm de horas foi encerrado.",
                    Get("Word_Success"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
        }

        private void ReloadIfGameChanged()
        {
            var game = _selectionService.SelectedGame;
            if (game == null || game == _loadedGame) return;
            _loadedGame = game;

            _appId = game.AppId;
            if (GameNameText != null) GameNameText.Text = game.Name;
            if (AppIdText != null) AppIdText.Text = $"AppID: {game.AppId}";

            _allAchievements.Clear();
            _filteredAchievements.Clear();
            _currentAchPage = 1;
            _shAppInfo = null;
            _hltbInfo = null;
            if (AchievementsList != null) AchievementsList.ItemsSource = null;
            if (StatsText != null) StatsText.Text = "? / ?";

            var settingsService = App.ServiceProvider.GetRequiredService<AppSettingsService>();
            settingsService.NormalizeIdentity();
            var settings = settingsService.Current;

            bool hasApiIdentity = !string.IsNullOrWhiteSpace(settings.SteamApiKey) && !string.IsNullOrWhiteSpace(settings.SteamId64);
            bool hasProfileFallback = !string.IsNullOrWhiteSpace(settingsService.GetEffectiveProfileUrl());

            if (!hasApiIdentity && !hasProfileFallback)
            {
                if (StatsText != null) StatsText.Text = "?";
                if (AchievementsList != null) AchievementsList.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    Get("Err_ConfigRequiredMsg"),
                    Get("Err_ConfigRequired"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _ = LoadAchievementsAndMetadataAsync();
        }

        public void SetParameter(object? parameter)
        {
        }

        private async Task LoadAchievementsAndMetadataAsync()
        {
            if (LoadingRing == null || AchievementsList == null) return;

            LoadingRing.Visibility = Visibility.Visible;
            AchievementsList.Visibility = Visibility.Collapsed;

            try
            {
                string gameName = _loadedGame?.Name ?? "";

                // Fetch Achievements + SteamHunters App Info + HowLongToBeat in parallel
                var achsTask = _achievementService.GetAchievementsAsync(_appId);
                var shAppTask = _steamHuntersService.GetAppInfoAsync(_appId);
                var hltbTask = _hltbService.SearchAsync(gameName);

                await Task.WhenAll(achsTask, shAppTask, hltbTask);

                _allAchievements = achsTask.Result;
                _shAppInfo = shAppTask.Result;
                _hltbInfo = hltbTask.Result;

                foreach (var ach in _allAchievements)
                    ach.PropertyChanged += (_, _) => UpdateStats();

                UpdateMetricsSummaryText();

                // Restore any saved/running schedule for this game, or build a fresh preview
                var restored = _schedulerService.TryRestoreSavedSchedule(_appId, _allAchievements);
                if (restored != null)
                {
                    _suppressPacingEvents = true;
                    if (TargetHoursBox != null)
                        TargetHoursBox.Text = Math.Round(restored.TotalTargetSeconds / 3600.0, 1).ToString("0.0", CultureInfo.InvariantCulture);
                    _suppressPacingEvents = false;
                }
                else
                {
                    RecalculateTargetHoursFromProfile();
                    GeneratePreviewSchedule();
                }

                ApplyFilter();
                UpdateStats();
                UpdateSchedulerUi();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Get("Err_LoadAchievements")}: {ex.Message}\n\n{Get("Err_SteamOpen")}", Get("Word_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                var navigationService = App.ServiceProvider.GetRequiredService<Wpf.Ui.INavigationService>();
                navigationService.GoBack();
            }
            finally
            {
                LoadingRing.Visibility = Visibility.Collapsed;
                AchievementsList.Visibility = Visibility.Visible;
            }
        }

        private void UpdateMetricsSummaryText()
        {
            if (OrganicMetricsText == null) return;

            var parts = new List<string>();

            if (_shAppInfo != null && (_shAppInfo.FastestHours > 0 || _shAppInfo.MedianHours > 0))
            {
                parts.Add($"SteamHunters: Rápido {_shAppInfo.FastestHours:0.0}h • Mediana {_shAppInfo.MedianHours:0.0}h");
            }

            if (_hltbInfo != null && (_hltbInfo.MainHours > 0 || _hltbInfo.CompletionistHours > 0))
            {
                parts.Add($"HLTB: Campanha {_hltbInfo.MainHours:0.0}h • 100% {_hltbInfo.CompletionistHours:0.0}h");
            }

            double played = _loadedGame?.PlaytimeHours ?? 0;
            parts.Add($"Suas Horas: {played:0.0}h");

            int unobtainable = _allAchievements.Count(a => a.IsUnobtainable);
            if (unobtainable > 0)
            {
                parts.Add($"⚠️ {unobtainable} inobtível(is) filtrada(s)");
            }

            OrganicMetricsText.Text = string.Join("   |   ", parts);
        }

        private AchievementPacingProfile GetSelectedPacingProfile()
        {
            string? tag = (PacingCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return tag switch
            {
                "Fastest" => AchievementPacingProfile.Fastest,
                "Completionist" => AchievementPacingProfile.Completionist,
                "Custom" => AchievementPacingProfile.Custom,
                _ => AchievementPacingProfile.Median
            };
        }

        private double GetTargetHoursInput()
        {
            if (TargetHoursBox != null &&
                double.TryParse(TargetHoursBox.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double h) &&
                h > 0)
            {
                return Math.Clamp(h, 0.1, 1000.0);
            }
            return 6.0;
        }

        private void RecalculateTargetHoursFromProfile()
        {
            if (TargetHoursBox == null || _allAchievements.Count == 0) return;

            var profile = GetSelectedPacingProfile();
            if (profile == AchievementPacingProfile.Custom) return;

            bool discount = DiscountHoursCheck?.IsChecked == true;
            double played = _loadedGame?.PlaytimeHours ?? 0;

            double estimated = _timelineEngine.EstimateTargetHours(
                profile,
                GetTargetHoursInput(),
                _shAppInfo,
                _hltbInfo,
                _allAchievements,
                played,
                discount);

            _suppressPacingEvents = true;
            TargetHoursBox.Text = estimated.ToString("0.0", CultureInfo.InvariantCulture);
            _suppressPacingEvents = false;
        }

        private void GeneratePreviewSchedule()
        {
            if (_allAchievements.Count == 0) return;
            double hours = GetTargetHoursInput();
            _timelineEngine.GenerateSchedule(_allAchievements, hours);
        }

        private void PacingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPacingEvents || _allAchievements.Count == 0) return;
            RecalculateTargetHoursFromProfile();
            GeneratePreviewSchedule();
            ApplyFilter();
        }

        private void DiscountHoursCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressPacingEvents || _allAchievements.Count == 0) return;
            RecalculateTargetHoursFromProfile();
            GeneratePreviewSchedule();
            ApplyFilter();
        }

        private void TargetHoursBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyCustomTargetHours();
                e.Handled = true;
            }
        }

        private void TargetHoursBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyCustomTargetHours();
        }

        private void ApplyCustomTargetHours()
        {
            if (_suppressPacingEvents || _allAchievements.Count == 0) return;
            _suppressPacingEvents = true;
            if (PacingCombo != null && PacingCombo.Items.Count >= 4)
                PacingCombo.SelectedIndex = 3; // Custom
            _suppressPacingEvents = false;

            GeneratePreviewSchedule();
            ApplyFilter();
            UpdateSchedulerUi();
        }

        private void SimulateSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (_allAchievements.Count == 0) return;

            GeneratePreviewSchedule();
            string profileName = GetSelectedPacingProfile().ToString();
            _schedulerService.CommitSchedule(_appId, _loadedGame?.Name ?? $"App {_appId}", profileName, _allAchievements);

            // Switch sort to Timeline so the user sees the #1, #2, #3 sequence clearly
            if (SortByCombo != null)
                SortByCombo.SelectedIndex = 0;

            ApplyFilter();
            UpdateSchedulerUi();
        }

        private void StartPauseOrganic_Click(object sender, RoutedEventArgs e)
        {
            if (_appId <= 0 || _allAchievements.Count == 0) return;

            // If currently running for this game -> Pause
            if (_schedulerService.IsRunning && _schedulerService.ActiveAppId == _appId)
            {
                _schedulerService.Pause();
                UpdateSchedulerUi();
                return;
            }

            // Ensure schedule is generated if none exists yet
            if (!_allAchievements.Any(a => !a.IsAchieved && a.ScheduledOffset.HasValue))
            {
                GeneratePreviewSchedule();
            }

            string profileName = GetSelectedPacingProfile().ToString();
            bool started = _schedulerService.StartOrResume(
                _appId,
                _loadedGame?.Name ?? $"App {_appId}",
                profileName,
                _allAchievements);

            if (!started)
            {
                MessageBox.Show(
                    "Não foi possível iniciar o Farm Híbrido.\nVerifique se a Steam está aberta e se ainda há conquistas obtíveis bloqueadas.",
                    Get("Word_Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            UpdateSchedulerUi();
        }

        private async void UnlockNext_Click(object sender, RoutedEventArgs e)
        {
            if (UnlockNextBtn != null) UnlockNextBtn.IsEnabled = false;
            try
            {
                bool ok = await _schedulerService.UnlockNextImmediatelyAsync();
                if (!ok)
                {
                    MessageBox.Show(
                        "Falha ao desbloquear a próxima conquista. Verifique se a Steam está aberta.",
                        Get("Word_Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                UpdateSchedulerUi();
            }
        }

        private void ResetSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (_appId <= 0) return;

            _schedulerService.ResetSchedule(_appId);
            RecalculateTargetHoursFromProfile();
            GeneratePreviewSchedule();
            ApplyFilter();
            UpdateSchedulerUi();
        }

        private void UpdateSchedulerUi()
        {
            if (OrganicElapsedText == null || OrganicNextText == null || StartPauseOrganicBtn == null)
                return;

            bool isThisGameActive = _schedulerService.ActiveAppId == _appId && _schedulerService.TotalInScheduleCount > 0;
            bool isRunning = isThisGameActive && _schedulerService.IsRunning;

            if (isThisGameActive)
            {
                var elapsed = _schedulerService.ElapsedTime;
                var total = _schedulerService.TotalTargetDuration;

                string elapsedStr = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
                string totalStr = $"{(int)total.TotalHours:00}:{total.Minutes:00}:{total.Seconds:00}";
                OrganicElapsedText.Text = $"{elapsedStr} / {totalStr}";

                var next = _schedulerService.NextPendingEntry;
                if (next != null)
                {
                    var remaining = next.TargetOffset - elapsed;
                    if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

                    string remStr = remaining.TotalHours >= 1
                        ? $"{(int)remaining.TotalHours:00}h {remaining.Minutes:00}m {remaining.Seconds:00}s"
                        : $"{remaining.Minutes:00}m {remaining.Seconds:00}s";

                    string statusPrefix = isRunning ? "🟢 Farmando" : "⏸️ Pausado";
                    OrganicNextText.Text = $"{statusPrefix} ({_schedulerService.UnlockedInScheduleCount}/{_schedulerService.TotalInScheduleCount}) • Próxima: #{next.Order} {next.Name} em {remStr}";
                }
                else
                {
                    OrganicNextText.Text = "✔ Todas as conquistas do cronograma foram desbloqueadas!";
                }

                if (UnlockNextBtn != null)
                    UnlockNextBtn.IsEnabled = next != null;

                if (isRunning)
                {
                    StartPauseOrganicBtn.Content = Get("AchEd_PauseOrganicBtn");
                    StartPauseOrganicBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Pause24 };
                    StartPauseOrganicBtn.Appearance = ControlAppearance.Secondary;
                }
                else
                {
                    StartPauseOrganicBtn.Content = _schedulerService.ElapsedTime.TotalSeconds > 0
                        ? Get("AchEd_ResumeOrganicBtn")
                        : Get("AchEd_StartOrganicBtn");
                    StartPauseOrganicBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Play24 };
                    StartPauseOrganicBtn.Appearance = ControlAppearance.Primary;
                }
            }
            else
            {
                int scheduledCount = _allAchievements.Count(a => !a.IsAchieved && !a.IsUnobtainable && a.ScheduledOffset.HasValue);
                var maxOffset = _allAchievements
                    .Where(a => !a.IsAchieved && a.ScheduledOffset.HasValue)
                    .Select(a => a.ScheduledOffset!.Value)
                    .DefaultIfEmpty(TimeSpan.Zero)
                    .Max();

                OrganicElapsedText.Text = $"00:00:00 / {(int)maxOffset.TotalHours:00}:{maxOffset.Minutes:00}:{maxOffset.Seconds:00}";
                OrganicNextText.Text = scheduledCount > 0
                    ? $"Pronto: {scheduledCount} conquistas planejadas em sequência orgânica"
                    : "Nenhuma conquista pendente para agendar";

                if (UnlockNextBtn != null)
                    UnlockNextBtn.IsEnabled = false;

                StartPauseOrganicBtn.Content = Get("AchEd_StartOrganicBtn");
                StartPauseOrganicBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Play24 };
                StartPauseOrganicBtn.Appearance = ControlAppearance.Primary;
            }
        }

        private void UpdateStats()
        {
            if (StatsText == null) return;
            int achieved = _allAchievements.Count(a => a.IsAchieved);
            StatsText.Text = $"{achieved} / {_allAchievements.Count}";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void SortDirection_Click(object sender, RoutedEventArgs e)
        {
            _isAscending = !_isAscending;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (AchievementsList == null || SearchBox == null || SortByCombo == null) return;

            string filter = SearchBox.Text.ToLower();
            var query = _allAchievements.Where(a =>
                a.Name.ToLower().Contains(filter) ||
                a.Description.ToLower().Contains(filter) ||
                a.PrerequisiteHint.ToLower().Contains(filter));

            string? sortCriteria = (SortByCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

            query = sortCriteria switch
            {
                "Timeline" => _isAscending
                    ? query
                        .OrderBy(a => a.IsUnobtainable ? 2 : (a.SuggestedOrder > 0 && !a.IsAchieved ? 0 : 1))
                        .ThenBy(a => a.SuggestedOrder > 0 ? a.SuggestedOrder : int.MaxValue)
                        .ThenBy(a => a.UnlockTime ?? DateTime.MaxValue)
                    : query
                        .OrderByDescending(a => a.SuggestedOrder)
                        .ThenByDescending(a => a.EffectivePercentage),

                "Rarity" => _isAscending
                    ? query.OrderByDescending(a => a.EffectivePercentage).ThenBy(a => a.SchemaOrder)
                    : query.OrderBy(a => a.EffectivePercentage).ThenByDescending(a => a.SchemaOrder),

                "Date" => _isAscending
                    ? query.OrderBy(a => a.UnlockTime ?? DateTime.MaxValue)
                    : query.OrderByDescending(a => a.UnlockTime ?? DateTime.MinValue),

                "Status" => _isAscending
                    ? query.OrderBy(a => a.IsAchieved)
                    : query.OrderByDescending(a => a.IsAchieved),

                _ => _isAscending
                    ? query.OrderBy(a => a.Name)
                    : query.OrderByDescending(a => a.Name)
            };

            _filteredAchievements = query.ToList();
            _currentAchPage = 1;
            UpdatePagedView();
        }

        private void UpdatePagedView()
        {
            if (AchievementsList == null || AchPageInfoText == null) return;

            int totalItems = _filteredAchievements.Count;
            int totalPages = (int)Math.Ceiling((double)totalItems / _achievementsPerPage);
            if (totalPages < 1) totalPages = 1;

            if (_currentAchPage < 1) _currentAchPage = 1;
            if (_currentAchPage > totalPages) _currentAchPage = totalPages;

            var paged = _filteredAchievements.Skip((_currentAchPage - 1) * _achievementsPerPage).Take(_achievementsPerPage).ToList();
            AchievementsList.ItemsSource = paged;

            AchPageInfoText.Text = $"Página {_currentAchPage} de {totalPages} ({totalItems} conquistas)";
            if (PrevAchPageBtn != null) PrevAchPageBtn.IsEnabled = _currentAchPage > 1;
            if (NextAchPageBtn != null) NextAchPageBtn.IsEnabled = _currentAchPage < totalPages;
        }

        private void PrevAchPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentAchPage > 1)
            {
                _currentAchPage--;
                UpdatePagedView();
            }
        }

        private void NextAchPage_Click(object sender, RoutedEventArgs e)
        {
            int totalPages = (int)Math.Ceiling((double)_filteredAchievements.Count / _achievementsPerPage);
            if (_currentAchPage < totalPages)
            {
                _currentAchPage++;
                UpdatePagedView();
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            var navigationService = App.ServiceProvider.GetRequiredService<Wpf.Ui.INavigationService>();
            navigationService.GoBack();
        }

        // Batch actions affect all achievements in the current search query
        private IEnumerable<AchievementModel> TargetBatchAchievements =>
            _filteredAchievements.Count > 0 ? _filteredAchievements : _allAchievements;

        private void Invert_Click(object sender, RoutedEventArgs e)
        {
            foreach (var ach in TargetBatchAchievements.Where(a => !a.IsUnobtainable))
                ach.IsAchieved = !ach.IsAchieved;
            UpdatePagedView();
            UpdateStats();
        }

        private void UnlockAll_Click(object sender, RoutedEventArgs e)
        {
            var target = TargetBatchAchievements.ToList();
            bool hasUnobtainable = target.Any(a => a.IsUnobtainable && !a.IsAchieved);
            bool includeUnobtainable = false;

            if (hasUnobtainable)
            {
                var res = MessageBox.Show(
                    "Existem conquistas marcadas como INOBTÍVEIS / QUEBRADAS no SteamHunters.\n\nDeseja manter as conquistas inobtíveis BLOQUEADAS para proteger seu perfil contra flags?\n\n• Sim (Recomendado): Desbloqueia apenas as conquistas legítimas/obtíveis.\n• Não: Desbloqueia absolutamente todas.",
                    "Proteção contra Conquistas Inobtíveis",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                includeUnobtainable = (res == MessageBoxResult.No);
            }

            foreach (var ach in target)
            {
                if (ach.IsUnobtainable && !includeUnobtainable)
                    continue;
                ach.IsAchieved = true;
            }
            UpdatePagedView();
            UpdateStats();
        }

        private void LockAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var ach in TargetBatchAchievements)
                ach.IsAchieved = false;
            UpdatePagedView();
            UpdateStats();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_appId <= 0 || _allAchievements.Count == 0) return;

            LoadingRing.Visibility = Visibility.Visible;
            if (sender is UIElement btn) btn.IsEnabled = false;

            bool success;
            try
            {
                success = await Task.Run(() => _achievementService.SaveAchievements(_appId, _allAchievements));
            }
            finally
            {
                LoadingRing.Visibility = Visibility.Collapsed;
                if (sender is UIElement b) b.IsEnabled = true;
            }

            if (success)
            {
                GeneratePreviewSchedule();
                ApplyFilter();
                UpdateSchedulerUi();
                MessageBox.Show(Get("Err_SaveSuccess"), Get("Word_Success"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(Get("Err_SaveFail"), Get("Word_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CommunityGuides_Click(object sender, RoutedEventArgs e)
        {
            if (_appId <= 0) return;

            if (sender is UIElement btn) btn.IsEnabled = false;
            try
            {
                var guides = await _guidesService.GetTopGuidesAsync(_appId);
                if (guides.Count == 0)
                {
                    // Fallback to opening the Steam community guides hub directly in the user's browser
                    var targetUrl = $"https://steamcommunity.com/app/{_appId}/guides/?browsefilter=toprated";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = targetUrl,
                        UseShellExecute = true
                    });
                    return;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Top Guias da Comunidade Steam para {_loadedGame?.Name ?? $"App {_appId}"}:\n");
                for (int i = 0; i < guides.Count; i++)
                {
                    sb.AppendLine($"{i + 1}. {guides[i].Title}");
                }
                sb.AppendLine("\nDeseja abrir o melhor guia no seu navegador?");

                var res = MessageBox.Show(sb.ToString(), "Guias da Comunidade Steam", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (res == MessageBoxResult.Yes && !string.IsNullOrEmpty(guides[0].Url))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = guides[0].Url,
                        UseShellExecute = true
                    });
                }
            }
            finally
            {
                if (sender is UIElement b) b.IsEnabled = true;
            }
        }
    }
}
