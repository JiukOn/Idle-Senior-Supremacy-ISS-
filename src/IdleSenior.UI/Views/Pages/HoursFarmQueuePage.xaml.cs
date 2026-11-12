using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IdleSenior.Core.Services;
using Wpf.Ui.Controls;

namespace IdleSenior.UI.Views.Pages
{
    public class BulkGameCandidate : INotifyPropertyChanged
    {
        private bool _isSelected;
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class HoursQueueItemViewModel : INotifyPropertyChanged
    {
        private string _name = "";
        private string _statusText = "";
        private Brush _statusBrush = Brushes.Gray;
        private string _elapsedText = "00:00:00";
        private string _targetText = "1.0h";
        private bool _isRunning;
        private bool _canMoveUp;
        private bool _canMoveDown;

        public int AppId { get; set; }
        public string AppIdText => $"AppID {AppId}";

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public Brush StatusBrush
        {
            get => _statusBrush;
            set { if (_statusBrush != value) { _statusBrush = value; OnPropertyChanged(); } }
        }

        public string ElapsedText
        {
            get => _elapsedText;
            set { if (_elapsedText != value) { _elapsedText = value; OnPropertyChanged(); } }
        }

        public string TargetText
        {
            get => _targetText;
            set { if (_targetText != value) { _targetText = value; OnPropertyChanged(); } }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PrimaryActionText));
                    OnPropertyChanged(nameof(PrimaryActionAppearance));
                    OnPropertyChanged(nameof(PrimaryActionIcon));
                }
            }
        }

        public bool CanMoveUp
        {
            get => _canMoveUp;
            set { if (_canMoveUp != value) { _canMoveUp = value; OnPropertyChanged(); } }
        }

        public bool CanMoveDown
        {
            get => _canMoveDown;
            set { if (_canMoveDown != value) { _canMoveDown = value; OnPropertyChanged(); } }
        }

        public string PrimaryActionText => IsRunning ? "Parar" : "Iniciar";
        public ControlAppearance PrimaryActionAppearance => IsRunning ? ControlAppearance.Danger : ControlAppearance.Primary;
        public SymbolIcon PrimaryActionIcon => new(IsRunning ? SymbolRegular.Stop24 : SymbolRegular.Play24);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public partial class HoursFarmQueuePage : Page
    {
        private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(80, 250, 123));
        private static readonly Brush QueuedBrush = new SolidColorBrush(Color.FromRgb(255, 184, 108));
        private static readonly Brush LimitWarnBrush = new SolidColorBrush(Color.FromRgb(255, 85, 85));

        private readonly SteamIdleService _idleService;
        private readonly AppSettingsService _settingsService;
        private readonly SteamWebService _steamWebService;
        private readonly ObservableCollection<HoursQueueItemViewModel> _items = new();
        private readonly Dictionary<int, string> _nameCache = new();
        private readonly List<BulkGameCandidate> _allBulkCandidates = new();
        private readonly DispatcherTimer _refreshTimer;

        public HoursFarmQueuePage(
            SteamIdleService idleService,
            AppSettingsService settingsService,
            SteamWebService steamWebService)
        {
            InitializeComponent();
            _idleService = idleService;
            _settingsService = settingsService;
            _steamWebService = steamWebService;

            QueueItemsControl.ItemsSource = _items;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refreshTimer.Tick += (_, _) => SyncQueueItems();

            Loaded += HoursFarmQueuePage_Loaded;
            Unloaded += HoursFarmQueuePage_Unloaded;
        }

        private async void HoursFarmQueuePage_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateModeHeader();
            SyncQueueItems();
            _refreshTimer.Start();

            try
            {
                var games = await _steamWebService.GetOwnedGamesAsync(forceRefresh: false);
                _allBulkCandidates.Clear();
                foreach (var g in games.OrderBy(x => x.Name))
                {
                    _nameCache[g.AppId] = g.Name;
                    _allBulkCandidates.Add(new BulkGameCandidate { AppId = g.AppId, Name = g.Name });
                }
                RefreshBulkFilter();
                SyncQueueItems();
            }
            catch { }
        }

        private void HoursFarmQueuePage_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
        }

        private void UpdateModeHeader()
        {
            bool isSequential = string.Equals(_settingsService.Current.HoursFarmMode, "Sequential", StringComparison.OrdinalIgnoreCase);
            ModeSubtitleText.Text = isSequential
                ? "Modo atual: Sequencial (1 por vez) — use as setas para definir a prioridade da fila."
                : "Modo atual: Simultâneo (até 32 jogos ativos juntos na Steam, somando Cartas + Horas).";
        }

        private void SyncQueueItems()
        {
            UpdateModeHeader();

            int totalSteam = _idleService.ActiveWorkerCount;
            SteamSlotsText.Text = $"Steam: {totalSteam} / {SteamIdleService.MaxSteamSimultaneousGames} ativos";
            SteamSlotsText.Foreground = totalSteam >= SteamIdleService.MaxSteamSimultaneousGames
                ? LimitWarnBrush
                : ActiveBrush;

            var queuedIds = _settingsService.Current.HoursFarmApps.ToList();
            var activeHoursIds = _idleService.GetActiveAppIds(IdlePurpose.Hours);

            var orderedIds = new List<int>(queuedIds);
            foreach (int activeId in activeHoursIds)
            {
                if (!orderedIds.Contains(activeId))
                    orderedIds.Add(activeId);
            }

            bool sameOrder = _items.Count == orderedIds.Count &&
                             _items.Select(i => i.AppId).SequenceEqual(orderedIds);

            if (!sameOrder)
            {
                _items.Clear();
                for (int i = 0; i < orderedIds.Count; i++)
                {
                    int appId = orderedIds[i];
                    _items.Add(new HoursQueueItemViewModel
                    {
                        AppId = appId,
                        Name = _nameCache.TryGetValue(appId, out var n) ? n : $"App {appId}"
                    });
                }
            }

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                int appId = item.AppId;
                bool isRunning = _idleService.IsIdling(appId, IdlePurpose.Hours);
                var start = _idleService.GetStartTime(appId);
                var elapsed = (isRunning && start.HasValue) ? (DateTime.UtcNow - start.Value) : TimeSpan.Zero;
                double target = _settingsService.Current.HoursFarmTargets.TryGetValue(appId, out var t) ? t : 1.0;

                if (_nameCache.TryGetValue(appId, out var cachedName))
                    item.Name = cachedName;

                item.IsRunning = isRunning;
                item.StatusText = isRunning ? "⚡ Farmando agora" : "⏳ Na fila";
                item.StatusBrush = isRunning ? ActiveBrush : QueuedBrush;
                item.ElapsedText = elapsed.ToString(@"hh\:mm\:ss");
                item.TargetText = target > 0 ? $"{target:0.0}h" : "Sem limite";

                int queueIndex = queuedIds.IndexOf(appId);
                item.CanMoveUp = queueIndex > 0;
                item.CanMoveDown = queueIndex >= 0 && queueIndex < queuedIds.Count - 1;
            }

            bool hasAny = _items.Count > 0;
            EmptyStateCard.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
            StartAllHoursBtn.IsEnabled = queuedIds.Any(id => !_idleService.IsIdling(id, IdlePurpose.Hours));
            StopAllHoursBtn.IsEnabled = activeHoursIds.Count > 0;
        }

        private void ToggleBulkAdd_Click(object sender, RoutedEventArgs e)
        {
            BulkAddCard.Visibility = BulkAddCard.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (BulkAddCard.Visibility == Visibility.Visible)
                RefreshBulkFilter();
        }

        private void BulkSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshBulkFilter();
        }

        private void RefreshBulkFilter()
        {
            string query = BulkSearchBox?.Text?.Trim() ?? "";
            var queuedSet = _settingsService.Current.HoursFarmApps.ToHashSet();
            var filtered = _allBulkCandidates
                .Where(c => !queuedSet.Contains(c.AppId) &&
                            (string.IsNullOrEmpty(query) || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Take(100)
                .ToList();

            BulkGamesList.ItemsSource = filtered;
        }

        private void SelectVisibleBulk_Click(object sender, RoutedEventArgs e)
        {
            if (BulkGamesList.ItemsSource is IEnumerable<BulkGameCandidate> visible)
            {
                foreach (var item in visible)
                    item.IsSelected = true;
            }
        }

        private void ConfirmBulkAdd_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allBulkCandidates.Where(c => c.IsSelected).ToList();
            if (selected.Count == 0) return;

            double targetHours = 10.0;
            if (BulkTargetInput != null &&
                double.TryParse(BulkTargetInput.Text.Trim().Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed) &&
                parsed > 0)
            {
                targetHours = parsed;
            }

            foreach (var c in selected)
            {
                if (!_settingsService.Current.HoursFarmApps.Contains(c.AppId))
                    _settingsService.Current.HoursFarmApps.Add(c.AppId);
                _settingsService.Current.HoursFarmTargets[c.AppId] = targetHours;
                c.IsSelected = false;
            }
            _settingsService.Save();

            BulkAddCard.Visibility = Visibility.Collapsed;
            RefreshBulkFilter();

            if (_settingsService.Current.HoursFarmAutoStart)
                StartAllHours_Click(this, new RoutedEventArgs());
            else
                SyncQueueItems();
        }

        private void StartAllHours_Click(object sender, RoutedEventArgs e)
        {
            var list = _settingsService.Current.HoursFarmApps.ToList();
            if (list.Count == 0) return;

            bool isSequential = string.Equals(_settingsService.Current.HoursFarmMode, "Sequential", StringComparison.OrdinalIgnoreCase);
            if (isSequential)
            {
                if (_idleService.GetActiveAppIds(IdlePurpose.Hours).Count == 0)
                {
                    if (!_idleService.StartIdle(list[0], IdlePurpose.Hours, out string? err))
                    {
                        System.Windows.MessageBox.Show(err ?? "Erro ao iniciar idle.", "Farm de Horas",
                            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }
                SyncQueueItems();
                return;
            }

            string? lastErr = null;
            foreach (int appId in list)
            {
                if (_idleService.IsIdling(appId, IdlePurpose.Hours)) continue;
                if (!_idleService.StartIdle(appId, IdlePurpose.Hours, out string? err))
                {
                    lastErr = err;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(lastErr))
            {
                System.Windows.MessageBox.Show(lastErr, "Farm de Horas",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }

            SyncQueueItems();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: int appId })
                MoveInQueue(appId, -1);
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: int appId })
                MoveInQueue(appId, +1);
        }

        private void MoveInQueue(int appId, int delta)
        {
            var list = _settingsService.Current.HoursFarmApps;
            int idx = list.IndexOf(appId);
            if (idx < 0) return;

            int newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= list.Count) return;

            list.RemoveAt(idx);
            list.Insert(newIdx, appId);
            _settingsService.Save();
            SyncQueueItems();
        }

        private void PrimaryAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: int appId }) return;

            if (_idleService.IsIdling(appId, IdlePurpose.Hours))
            {
                _idleService.StopIdle(appId, IdlePurpose.Hours);
            }
            else
            {
                bool isSequential = string.Equals(_settingsService.Current.HoursFarmMode, "Sequential", StringComparison.OrdinalIgnoreCase);
                if (isSequential)
                {
                    _idleService.StopByPurpose(IdlePurpose.Hours);
                    var list = _settingsService.Current.HoursFarmApps;
                    if (list.Remove(appId))
                    {
                        list.Insert(0, appId);
                        _settingsService.Save();
                    }
                }

                if (!_idleService.StartIdle(appId, IdlePurpose.Hours, out string? err))
                {
                    System.Windows.MessageBox.Show(
                        err ?? "Erro ao iniciar idle. Verifique se a Steam está aberta.",
                        "Farm de Horas",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            }

            SyncQueueItems();
        }

        private void RemoveFromQueue_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: int appId }) return;

            if (_idleService.IsIdling(appId, IdlePurpose.Hours))
                _idleService.StopIdle(appId, IdlePurpose.Hours);

            _settingsService.Current.HoursFarmApps.Remove(appId);
            _settingsService.Save();
            RefreshBulkFilter();
            SyncQueueItems();
        }

        private void StopAllHours_Click(object sender, RoutedEventArgs e)
        {
            _idleService.StopByPurpose(IdlePurpose.Hours);
            SyncQueueItems();
        }

        private void GameTitle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { Tag: int appId } && Window.GetWindow(this) is MainWindow mainWindow)
            {
                _ = mainWindow.ShowGameDetails(appId);
            }
        }
    }
}
