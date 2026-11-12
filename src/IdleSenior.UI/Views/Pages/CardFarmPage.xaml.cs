using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IdleSenior.Core.Models;
using IdleSenior.Core.Services;
using static IdleSenior.UI.Services.LocalizationService;

namespace IdleSenior.UI.Views.Pages
{
    public partial class CardFarmPage : Page
    {
        private const int IdleMinutesPerCycle = 15;
        private const int CooldownSeconds = 60;
        private const int MaxConsecutiveCheckErrors = 3;
        private const int MaxSimultaneousBatchGames = 25;

        private readonly SteamLibraryService _libraryService;
        private readonly SteamIdleService _idleService;
        private readonly AppSettingsService _settingsService;
        private readonly CardMarketDataService _marketService;
        private List<SteamGame> _farmingQueue = new();
        private readonly HashSet<int> _cardFarmActiveAppIds = new();
        private SteamGame? _currentGame;
        private CancellationTokenSource? _farmCts;
        private readonly DispatcherTimer _elapsedTimer;
        private DateTime _cycleStart;

        public CardFarmPage(
            SteamLibraryService libraryService,
            SteamIdleService idleService,
            AppSettingsService settingsService,
            CardMarketDataService marketService)
        {
            InitializeComponent();
            _libraryService = libraryService;
            _idleService = idleService;
            _settingsService = settingsService;
            _marketService = marketService;

            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) =>
                TimeElapsedText.Text = (DateTime.Now - _cycleStart).ToString(@"hh\:mm\:ss");

            Loaded += CardFarmPage_Loaded;
            Unloaded += CardFarmPage_Unloaded;
        }

        private void CardFarmPage_Loaded(object sender, RoutedEventArgs e)
        {
            _idleService.WorkerStopped -= OnWorkerStopped;
            _idleService.WorkerStopped += OnWorkerStopped;
            if (_farmCts == null)
                RefreshQueue();
        }

        private void CardFarmPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _idleService.WorkerStopped -= OnWorkerStopped;
        }

        private void OnWorkerStopped(int appId, bool wasUnexpected, string? errorMessage)
        {
            bool isCardFarmWorker;
            lock (_cardFarmActiveAppIds)
            {
                isCardFarmWorker = _cardFarmActiveAppIds.Contains(appId);
            }
            if (!wasUnexpected || !isCardFarmWorker) return;

            Dispatcher.BeginInvoke(() =>
            {
                _farmCts?.Cancel();
                string details = string.IsNullOrWhiteSpace(errorMessage)
                    ? "O processo Worker encerrou inesperadamente. Verifique se a Steam está aberta."
                    : errorMessage;
                MessageBox.Show(
                    $"Falha ao manter o jogo ativo (AppID {appId}):\n\n{details}",
                    Get("Word_Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
        }

        private void CardSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySortAndFilter();
        }

        private void BlockGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is int appId)
            {
                var blacklist = _settingsService.Current.CardFarmBlacklist;
                if (blacklist.Contains(appId))
                {
                    blacklist.Remove(appId);
                }
                else
                {
                    blacklist.Add(appId);
                }
                _settingsService.Save();

                foreach (var g in _farmingQueue)
                {
                    g.IsBlocked = blacklist.Contains(g.AppId);
                }

                ApplySortAndFilter();
            }
        }

        private void ApplySortAndFilter()
        {
            if (_farmingQueue == null || QueueList == null) return;

            string sortBy = (CardSortCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Cards";

            IEnumerable<SteamGame> query = sortBy switch
            {
                "Price" => _farmingQueue
                    .OrderBy(g => g.IsBlocked)
                    .ThenByDescending(g => g.AverageCardPrice)
                    .ThenByDescending(g => g.RemainingCards),
                "Name" => _farmingQueue
                    .OrderBy(g => g.IsBlocked)
                    .ThenBy(g => g.Name),
                "Year" => _farmingQueue
                    .OrderBy(g => g.IsBlocked)
                    .ThenByDescending(g => g.ReleaseYear)
                    .ThenByDescending(g => g.RemainingCards),
                _ => _farmingQueue
                    .OrderBy(g => g.IsBlocked)
                    .ThenByDescending(g => g.RemainingCards)
            };

            _farmingQueue = query.ToList();
            QueueList.ItemsSource = null;
            QueueList.ItemsSource = _farmingQueue;
        }

        private async void RefreshQueue()
        {
            if (_farmCts != null) return; // Don't overwrite queue mid-farm

            LoadingRing.Visibility = Visibility.Visible;
            RefreshBtn.IsEnabled = false;

            try
            {
                _farmingQueue = await _libraryService.GetGamesWithCardDropsAsync();
                var blacklist = _settingsService.Current.CardFarmBlacklist;
                foreach (var g in _farmingQueue)
                {
                    g.IsBlocked = blacklist.Contains(g.AppId);
                }

                ApplySortAndFilter();

                // Fetch market prices in background to enrich Price sort
                _ = Task.Run(async () =>
                {
                    foreach (var g in _farmingQueue)
                    {
                        try
                        {
                            var price = await _marketService.GetPriceForGameAsync(g.AppId);
                            if (price != null && !string.IsNullOrEmpty(price.LowestPriceText))
                            {
                                g.CardPriceText = $"Mercado: {price.LowestPriceText}";
                                if (double.TryParse(price.LowestPriceText.Replace("R$", "").Replace("$", "").Replace("€", "").Trim().Replace(',', '.'),
                                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double pr))
                                {
                                    g.AverageCardPrice = pr;
                                }
                            }
                        }
                        catch { }
                    }
                    _ = Dispatcher.InvokeAsync(ApplySortAndFilter);
                });

                if (EmptyNoticePanel != null)
                {
                    if (_farmingQueue.Count == 0)
                    {
                        var s = _settingsService.Current;
                        bool hasAuth = !string.IsNullOrWhiteSpace(s.SessionId) && !string.IsNullOrWhiteSpace(s.SteamLoginSecure);
                        if (!hasAuth)
                        {
                            EmptyNoticeTitle.Text = "Autenticação da Steam necessária";
                            EmptyNoticeDesc.Text = "Para verificar e farmar cartas da sua conta, a Steam exige cookies de login (sessionid e steamLoginSecure). Preencha-os nas Configurações.";
                        }
                        else
                        {
                            EmptyNoticeTitle.Text = "Nenhuma carta pendente";
                            EmptyNoticeDesc.Text = "Todos os drops de cartas dos seus jogos elegíveis já foram obtidos ou não há jogos com insígnias pendentes.";
                        }
                        EmptyNoticePanel.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        EmptyNoticePanel.Visibility = Visibility.Collapsed;
                    }
                }
            }
            finally
            {
                LoadingRing.Visibility = Visibility.Collapsed;
                RefreshBtn.IsEnabled = true;
            }
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            double minHours = _settingsService.Current.CardFarmMinHours;
            var anyWithCards = _farmingQueue.Where(g => !g.IsBlocked && g.RemainingCards > 0).ToList();
            if (anyWithCards.Count == 0)
            {
                MessageBox.Show(Get("CardFarm_NoGame"));
                return;
            }

            var eligible = anyWithCards
                .Where(g => minHours <= 0 || g.HoursPlayed >= minHours)
                .ToList();

            if (eligible.Count == 0)
            {
                MessageBox.Show(
                    $"Nenhum jogo na fila atingiu o mínimo configurado de {minHours:0.0}h jogadas.\nAjuste 'Horas Mínimas para Farmar Cartas' nas Configurações se desejar farmar antes.",
                    Get("CardFarm_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _farmCts = new CancellationTokenSource();
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            RefreshBtn.IsEnabled = false;
            FarmProgress.Visibility = Visibility.Visible;

            try
            {
                await RunFarmLoopAsync(_farmCts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                StopFarm();
            }
        }

        /// <summary>
        /// Implements simultaneous or sequential card farming, respecting minimum hours and blocklists.
        /// </summary>
        private async Task RunFarmLoopAsync(CancellationToken ct)
        {
            double minHours = _settingsService.Current.CardFarmMinHours;
            string farmMode = _settingsService.Current.CardFarmMode; // "Simultaneous" | "Sequential"

            // Phase 1: 2-Hour Rule Simultaneous Batch (only if mode is Simultaneous)
            if (farmMode != "Sequential")
            {
                var underTwoHours = _farmingQueue
                    .Where(g => !g.IsBlocked && g.HoursPlayed < 2.0 && g.RemainingCards > 0 && (minHours <= 0 || g.HoursPlayed >= minHours))
                    .Take(MaxSimultaneousBatchGames)
                    .ToList();

                while (underTwoHours.Count > 1 && !ct.IsCancellationRequested)
                {
                    _currentGame = underTwoHours[0];
                    if (CardValueText != null) CardValueText.Text = "";
                    CurrentGameName.Text = $"Lote < 2h ({underTwoHours.Count} jogos simultâneos)";
                    CardsRemainingText.Text = $"{underTwoHours.Sum(g => g.RemainingCards)} {Get("CardFarm_CardsLeft")}";
                    StatusTitle.Text = $"STATUS: FARM SIMULTÂNEO (< 2h) — {IdleMinutesPerCycle} min";

                    int started = 0;
                    string? startErr = null;
                    foreach (var g in underTwoHours)
                    {
                        if (_idleService.StartIdle(g.AppId, IdlePurpose.Cards, out var err))
                        {
                            lock (_cardFarmActiveAppIds) { _cardFarmActiveAppIds.Add(g.AppId); }
                            g.IsIdling = true;
                            started++;
                        }
                        else
                        {
                            startErr = err;
                        }
                    }

                    if (started == 0)
                    {
                        MessageBox.Show(startErr ?? "Não foi possível iniciar os processos de farm.",
                            Get("Word_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        _farmCts?.Cancel();
                        return;
                    }

                    _cycleStart = DateTime.Now;
                    _elapsedTimer.Start();

                    await DelayAsync(TimeSpan.FromMinutes(IdleMinutesPerCycle), ct);

                    _idleService.StopByPurpose(IdlePurpose.Cards);
                    lock (_cardFarmActiveAppIds) { _cardFarmActiveAppIds.Clear(); }
                    _elapsedTimer.Stop();

                    double elapsedMinutes = Math.Max(1.0, (DateTime.Now - _cycleStart).TotalMinutes);
                    foreach (var g in underTwoHours)
                    {
                        g.IsIdling = false;
                        g.HoursPlayed = Math.Round(g.HoursPlayed + (elapsedMinutes / 60.0), 2);
                    }

                    uint totalBatchMinutes = (uint)Math.Round(elapsedMinutes * started);
                    uint droppedInBatch = await RefreshAllDropsAndCountAsync();
                    _settingsService.AddIdledStats(totalBatchMinutes, droppedInBatch);

                    if (ct.IsCancellationRequested) return;

                    StatusTitle.Text = $"STATUS: COOLDOWN ({CooldownSeconds}s)";
                    await DelayAsync(TimeSpan.FromSeconds(CooldownSeconds), ct);

                    underTwoHours = _farmingQueue
                        .Where(g => !g.IsBlocked && g.HoursPlayed < 2.0 && g.RemainingCards > 0 && (minHours <= 0 || g.HoursPlayed >= minHours))
                        .Take(MaxSimultaneousBatchGames)
                        .ToList();
                }
            }

            // Phase 2: Single-game 15-minute farming cycles
            var queue = _farmingQueue
                .Where(g => !g.IsBlocked && g.RemainingCards > 0 && (minHours <= 0 || g.HoursPlayed >= minHours))
                .ToList();

            foreach (var game in queue)
            {
                if (ct.IsCancellationRequested) break;
                if (game.RemainingCards <= 0) continue;

                _currentGame = game;
                UpdateCurrentGameUI(game, phase: "FARMANDO");

                int consecutiveErrors = 0;
                bool hasCards = true;

                while (hasCards && !ct.IsCancellationRequested)
                {
                    // --- IDLE PHASE ---
                    if (!_idleService.StartIdle(game.AppId, IdlePurpose.Cards, out var startErr))
                    {
                        MessageBox.Show(
                            startErr ?? "Não foi possível iniciar o processo de farm.",
                            Get("Word_Error"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        _farmCts?.Cancel();
                        break;
                    }

                    lock (_cardFarmActiveAppIds) { _cardFarmActiveAppIds.Add(game.AppId); }
                    game.IsIdling = true;
                    _cycleStart = DateTime.Now;
                    _elapsedTimer.Start();

                    StatusTitle.Text = $"STATUS: FARMANDO ({IdleMinutesPerCycle} min/ciclo)";
                    await DelayAsync(TimeSpan.FromMinutes(IdleMinutesPerCycle), ct);

                    _idleService.StopIdle(game.AppId, IdlePurpose.Cards);
                    lock (_cardFarmActiveAppIds) { _cardFarmActiveAppIds.Remove(game.AppId); }
                    game.IsIdling = false;
                    _elapsedTimer.Stop();

                    uint minutesIdled = (uint)Math.Max(1, Math.Round((DateTime.Now - _cycleStart).TotalMinutes));

                    if (ct.IsCancellationRequested)
                    {
                        _settingsService.AddIdledStats(minutesIdled, 0);
                        break;
                    }

                    // --- RE-CHECK CARDS (Fast single-game check) ---
                    StatusTitle.Text = "STATUS: VERIFICANDO CARDS...";
                    int previousCards = game.RemainingCards;
                    int? updatedCards = await _libraryService.GetRemainingCardDropsForGameAsync(game.AppId);

                    if (updatedCards == null)
                    {
                        consecutiveErrors++;
                        _settingsService.AddIdledStats(minutesIdled, 0);

                        if (consecutiveErrors >= MaxConsecutiveCheckErrors)
                        {
                            // Avoid infinite loop if session expired or network is down
                            MessageBox.Show(
                                "Não foi possível verificar as cartas restantes após múltiplas tentativas. Verifique os cookies de sessão nas Configurações.",
                                Get("Word_Error"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                            _farmCts?.Cancel();
                            break;
                        }
                    }
                    else
                    {
                        consecutiveErrors = 0;
                        uint cardsDropped = updatedCards.Value < previousCards
                            ? (uint)(previousCards - updatedCards.Value)
                            : 0;

                        game.RemainingCards = updatedCards.Value;
                        _settingsService.AddIdledStats(minutesIdled, cardsDropped);
                        hasCards = game.RemainingCards > 0;
                    }

                    if (!hasCards)
                    {
                        _farmingQueue.Remove(game);
                        QueueList.ItemsSource = null;
                        QueueList.ItemsSource = _farmingQueue;
                        break;
                    }

                    UpdateCurrentGameUI(game, phase: "COOLDOWN");

                    // --- COOLDOWN PHASE ---
                    StatusTitle.Text = $"STATUS: COOLDOWN ({CooldownSeconds}s)";
                    await DelayAsync(TimeSpan.FromSeconds(CooldownSeconds), ct);
                }
            }
        }

        private async Task<uint> RefreshAllDropsAndCountAsync()
        {
            uint totalDropped = 0;
            try
            {
                var updatedList = await _libraryService.GetGamesWithCardDropsAsync(forceRefresh: true);
                if (updatedList.Count == 0 && _farmingQueue.Count > 0)
                {
                    // Guard against transient network/rate-limit/session failure wiping the queue
                    bool sessionOk = await _libraryService.ValidateSessionAsync();
                    if (!sessionOk)
                        return 0;
                }

                var updatedMap = updatedList.ToDictionary(g => g.AppId, g => g.RemainingCards);

                foreach (var existing in _farmingQueue.ToList())
                {
                    if (updatedMap.TryGetValue(existing.AppId, out int newRemaining))
                    {
                        if (newRemaining < existing.RemainingCards)
                            totalDropped += (uint)(existing.RemainingCards - newRemaining);
                        existing.RemainingCards = newRemaining;
                    }
                    else
                    {
                        totalDropped += (uint)Math.Max(0, existing.RemainingCards);
                        existing.RemainingCards = 0;
                        _farmingQueue.Remove(existing);
                    }
                }

                QueueList.ItemsSource = null;
                QueueList.ItemsSource = _farmingQueue;
            }
            catch { }

            return totalDropped;
        }

        private void UpdateCurrentGameUI(SteamGame game, string phase)
        {
            CurrentGameName.Text = game.Name;
            CardsRemainingText.Text = $"{game.RemainingCards} {Get("CardFarm_CardsLeft")}";
            StatusTitle.Text = $"STATUS: {phase}";
            _ = IdleSenior.UI.Helpers.SmartImageLoader.LoadIntoAsync(CurrentGameBanner, game.BannerFallbackUrls, 320);

            // Fetch live market card price safely with disk cache (zero spam)
            _ = Task.Run(async () =>
            {
                var price = await _marketService.GetPriceForGameAsync(game.AppId);
                if (price != null && !string.IsNullOrEmpty(price.LowestPriceText))
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (_currentGame?.AppId == game.AppId && CardValueText != null)
                            CardValueText.Text = $"Menor Preço Mercado: {price.LowestPriceText}";
                    });
                }
            });
        }

        private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            try { await Task.Delay(delay, ct); }
            catch (TaskCanceledException) { throw new OperationCanceledException(); }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _farmCts?.Cancel();
        }

        private void StopFarm()
        {
            _elapsedTimer.Stop();
            _idleService.StopByPurpose(IdlePurpose.Cards);
            lock (_cardFarmActiveAppIds) { _cardFarmActiveAppIds.Clear(); }

            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            RefreshBtn.IsEnabled = true;
            FarmProgress.Visibility = Visibility.Collapsed;
            StatusTitle.Text = Get("CardFarm_StatusStopped");
            CurrentGameName.Text = Get("CardFarm_NoGame");
            TimeElapsedText.Text = "--:--:--";
            CardsRemainingText.Text = "—";
            if (CardValueText != null) CardValueText.Text = "";
            if (CurrentGameBanner != null) CurrentGameBanner.Source = null;
            _currentGame = null;
            _farmCts?.Dispose();
            _farmCts = null;
        }

        private void QueueRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is SteamGame game)
            {
                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    _ = mainWindow.ShowGameDetails(game.AppId);
                }
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshQueue();

        private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }
    }
}
