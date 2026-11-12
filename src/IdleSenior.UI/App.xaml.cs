using IdleSenior.Core.Services;
using IdleSenior.UI.Views.Pages;
using IdleSenior.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace IdleSenior.UI;

public partial class App : Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Global exception safety net ───────────────────────────────────────
        // Prevents the app from closing silently on unhandled exceptions.
        // Any exception that escapes an async void handler (e.g. a page Loaded event)
        // ends up here instead of terminating the process.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();

        // Apply saved theme + language before the window is shown
        var settings = ServiceProvider.GetRequiredService<AppSettingsService>();
        IdleSenior.UI.Services.ThemeService.ApplyTheme(settings.Current.AppTheme);
        IdleSenior.UI.Services.LocalizationService.SetLanguage(settings.Current.Language);

        mainWindow.Closed += (_, _) => StopAllActiveWorkers();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopAllActiveWorkers();
        base.OnExit(e);
    }

    private static void StopAllActiveWorkers()
    {
        try
        {
            ServiceProvider?.GetService<IdleSenior.UI.Services.OrganicAchievementSchedulerService>()?.Pause();
            ServiceProvider?.GetService<SteamIdleService>()?.StopAll();
        }
        catch { }
    }

    // ── Global exception handlers ─────────────────────────────────────────────

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Mark as handled so WPF doesn't terminate the process.
        e.Handled = true;
        System.Diagnostics.Debug.WriteLine($"[App] DispatcherUnhandledException: {e.Exception}");

        try
        {
            var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_error.log");
            System.IO.File.WriteAllText(logPath, $"Timestamp: {DateTime.Now}\n{e.Exception}\n\nInner:\n{e.Exception.InnerException}");
        }
        catch { }

        // Show a user-friendly dialog instead of silently crashing.
        var title = IdleSenior.UI.Services.LocalizationService.Get("Err_Unexpected");
        var cont  = IdleSenior.UI.Services.LocalizationService.Get("Err_AppContinues");
        var details = e.Exception.InnerException != null
            ? $"{e.Exception.Message}\n\nDetalhes:\n{e.Exception.InnerException.Message}"
            : e.Exception.Message;

        MessageBox.Show(
            $"{title}:\n\n{details}\n\n{cont}",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Prevent the process from being taken down by unobserved task exceptions.
        e.SetObserved();
        System.Diagnostics.Debug.WriteLine($"[App] UnobservedTaskException: {e.Exception}");
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Last-resort logging for fatal background-thread exceptions.
        System.Diagnostics.Debug.WriteLine($"[App] UnhandledException (fatal={e.IsTerminating}): {e.ExceptionObject}");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ── Singletons with stable state ──────────────────────────────────────
        services.AddSingleton(AppSettingsService.Instance);
        services.AddSingleton<SteamIdleService>();
        services.AddSingleton<IdleSenior.UI.Services.SelectionService>();

        // SteamLibraryService — singleton so its authenticated HTTP calls and
        // badge-page scrapes are cached and reused across page navigations.
        services.AddSingleton<SteamLibraryService>(sp =>
        {
            var client = MakeSteamClient();
            return new SteamLibraryService(
                client,
                sp.GetRequiredService<AppSettingsService>(),
                sp.GetRequiredService<SteamLocalDiscoveryService>());
        });

        // SteamWebService — singleton, owns the game-list cache.
        services.AddSingleton<SteamWebService>(sp =>
        {
            var client = MakeSteamClient();
            return new SteamWebService(client,
                sp.GetRequiredService<SteamLibraryService>(),
                sp.GetRequiredService<AppSettingsService>());
        });

        // HowLongToBeatService — singleton so its auth handshake is reused.
        services.AddSingleton<HowLongToBeatService>(sp =>
        {
            var client = MakeHltbClient();
            return new HowLongToBeatService(client);
        });

        // SecurityAdvisoryService — aggregates Steam VAC + AreWeAntiCheatYet
        services.AddSingleton<SecurityAdvisoryService>(sp =>
        {
            var client = MakeSteamClient();
            return new SecurityAdvisoryService(client);
        });

        // SteamGuidesService — queries top community guides under demand
        services.AddSingleton<SteamGuidesService>(sp =>
        {
            var client = MakeSteamClient();
            return new SteamGuidesService(client, sp.GetRequiredService<AppSettingsService>());
        });

        // CardMarketDataService — safe single-game market pricing with disk cache
        services.AddSingleton<CardMarketDataService>(sp =>
        {
            var client = MakeSteamClient();
            return new CardMarketDataService(client);
        });

        // GameInfoService — singleton with in-memory cache combining Store, SteamHunters, HLTB and Security
        services.AddSingleton<GameInfoService>(sp =>
        {
            var client = MakeSteamClient();
            return new GameInfoService(
                client,
                sp.GetRequiredService<HowLongToBeatService>(),
                sp.GetRequiredService<SteamHuntersService>(),
                sp.GetRequiredService<SecurityAdvisoryService>(),
                sp.GetRequiredService<AppSettingsService>());
        });

        // SteamHuntersService — singleton with in-memory cache of rarity and completion stats.
        services.AddSingleton<SteamHuntersService>(sp =>
        {
            var client = MakeSteamClient();
            return new SteamHuntersService(client);
        });

        // SteamLocalDiscoveryService — reads local loginusers.vdf and resolves vanity URLs
        services.AddSingleton<SteamLocalDiscoveryService>(sp =>
        {
            var client = MakeSteamClient();
            return new SteamLocalDiscoveryService(client);
        });

        // AccountAnalyticsService — profile summary, level, library diagnosis, achievements and pricing analytics
        services.AddSingleton<AccountAnalyticsService>(sp =>
        {
            var client = MakeSteamClient();
            return new AccountAnalyticsService(
                client,
                sp.GetRequiredService<AppSettingsService>(),
                sp.GetRequiredService<SteamLibraryService>(),
                sp.GetRequiredService<SteamWebService>(),
                sp.GetRequiredService<SteamLocalDiscoveryService>(),
                sp.GetRequiredService<SteamHuntersService>());
        });

        // AchievementTimelineEngine — builds organic, prerequisite-aware achievement schedules.
        services.AddSingleton<AchievementTimelineEngine>();

        // SteamAchievementService — reads via Steam Web API + SteamHunters and writes via AchievementWorker.
        services.AddSingleton<IdleSenior.UI.Services.SteamAchievementService>(sp =>
        {
            var client = MakeSteamClient();
            return new IdleSenior.UI.Services.SteamAchievementService(
                client,
                sp.GetRequiredService<AppSettingsService>(),
                sp.GetRequiredService<SteamHuntersService>());
        });

        // OrganicAchievementSchedulerService — singleton background coordinator for simultaneous hours + achievement farming.
        services.AddSingleton<IdleSenior.UI.Services.OrganicAchievementSchedulerService>();

        // ── Navigation infrastructure ─────────────────────────────────────────
        services.AddSingleton<Wpf.Ui.Abstractions.INavigationViewPageProvider, IdleSenior.UI.Services.PageService>();
        services.AddSingleton<Wpf.Ui.INavigationService, Wpf.Ui.NavigationService>();

        // ── ViewModels ────────────────────────────────────────────────────────
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<AchievementsViewModel>();
        // SettingsViewModel needs SteamWebService to invalidate the cache on save
        services.AddTransient<SettingsViewModel>();

        // ── Pages (Singletons so active farms, timers, and scroll states persist across tab switches) ──
        services.AddSingleton<DashboardPage>();
        services.AddSingleton<CardFarmPage>();
        services.AddSingleton<HoursFarmQueuePage>();
        services.AddSingleton<LibraryPage>();
        services.AddSingleton<AchievementsPage>();
        services.AddSingleton<AchievementEditorPage>();
        services.AddSingleton<SettingsPage>();

        // ── Main window ───────────────────────────────────────────────────────
        services.AddSingleton<MainWindow>();
    }

    // ── HttpClient factories ──────────────────────────────────────────────────

    private static HttpClient MakeSteamClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 16
        })
        {
            Timeout = TimeSpan.FromSeconds(12),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" }
            }
        };

    private static HttpClient MakeHltbClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
                { "Referer", "https://howlongtobeat.com/" },
                { "Origin",  "https://howlongtobeat.com" }
            }
        };
}
