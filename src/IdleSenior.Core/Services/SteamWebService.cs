using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using IdleSenior.Core.Models;

namespace IdleSenior.Core.Services
{
    public class SteamWebService
    {
        private readonly HttpClient _httpClient;
        private readonly SteamLibraryService _libraryService;
        private readonly AppSettingsService _settingsService;

        // Cache so repeated page navigations don't re-fetch the whole library.
        // Call InvalidateCache() to force a refresh (e.g. after settings change).
        private List<GameModel>? _cachedGames;
        private List<GameModel>? _cachedRecentGames;

        public SteamWebService(HttpClient httpClient, SteamLibraryService libraryService, AppSettingsService settingsService)
        {
            _httpClient = httpClient;
            _libraryService = libraryService;
            _settingsService = settingsService;
        }

        /// <summary>
        /// Returns the full owned-games list. Results are cached after the first call.
        /// Achievement stats are NOT included here — call EnrichWithAchievementStatsAsync
        /// separately when you actually need completion percentages.
        /// </summary>
        public async Task<List<GameModel>> GetOwnedGamesAsync(bool forceRefresh = false)
        {
            if (_cachedGames != null && !forceRefresh)
                return _cachedGames;

            var steamGames = await _libraryService.GetOwnedGamesAsync();
            _cachedGames = steamGames.Cast<GameModel>().ToList();

            return _cachedGames;
        }

        /// <summary>
        /// Enriches cached games with achievement earned/total counts.
        /// Called only by pages that actually display completion percentages
        /// (AchievementsPage) to avoid slow badge-page scraping on every load.
        /// </summary>
        public async Task EnrichWithAchievementStatsAsync()
        {
            if (_cachedGames == null)
                await GetOwnedGamesAsync();

            var stats = await _libraryService.GetAchievementStatsAsync();
            foreach (var game in _cachedGames!)
            {
                if (stats.TryGetValue(game.AppId, out var s))
                {
                    game.EarnedAchievements = s.Earned;
                    game.TotalAchievements  = s.Total;
                }
            }
        }

        /// <summary>Clears the cache so the next GetOwnedGamesAsync re-fetches from Steam.</summary>
        public void InvalidateCache()
        {
            _cachedGames = null;
            _cachedRecentGames = null;
        }

        /// <summary>
        /// Loads recently played games using a 2-option fallback:
        /// 1. Primary: Steam Web API (IPlayerService/GetRecentlyPlayedGames/v1)
        /// 2. Secondary: Top 8 owned games sorted by LastPlayedUnix / PlaytimeHours
        /// </summary>
        public async Task<List<GameModel>> GetRecentGamesAsync(bool forceRefresh = false)
        {
            if (_cachedRecentGames != null && !forceRefresh)
                return _cachedRecentGames;

            var games = new List<GameModel>();
            var settings = _settingsService.Current;

            // Option 1: Official Recently Played API
            if (!string.IsNullOrEmpty(settings.SteamApiKey) && !string.IsNullOrEmpty(settings.SteamId64))
            {
                try
                {
                    var url = $"https://api.steampowered.com/IPlayerService/GetRecentlyPlayedGames/v1/?key={settings.SteamApiKey}&steamid={settings.SteamId64}&format=json";
                    var response = await _httpClient.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(response);

                    if (doc.RootElement.TryGetProperty("response", out var resp) &&
                        resp.TryGetProperty("games", out var gamesArray))
                    {
                        foreach (var gameNode in gamesArray.EnumerateArray())
                        {
                            games.Add(new GameModel
                            {
                                AppId = gameNode.GetProperty("appid").GetInt32(),
                                Name = gameNode.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                                IconHash = gameNode.TryGetProperty("img_icon_url", out var ic) ? ic.GetString() : null,
                                PlaytimeHours = gameNode.TryGetProperty("playtime_forever", out var ptf)
                                    ? Math.Round(ptf.GetDouble() / 60.0, 1)
                                    : (gameNode.TryGetProperty("playtime_2weeks", out var pt) ? Math.Round(pt.GetDouble() / 60.0, 1) : 0),
                                LastPlayed = "Recente"
                            });
                        }
                    }
                }
                catch { }
            }

            // Option 2 (Fallback): Top 8 owned games by LastPlayedUnix / PlaytimeHours
            if (games.Count == 0)
            {
                try
                {
                    var owned = await GetOwnedGamesAsync(forceRefresh);
                    games = owned
                        .OrderByDescending(g => g.LastPlayedUnix)
                        .ThenByDescending(g => g.PlaytimeHours)
                        .Take(8)
                        .ToList();
                }
                catch { }
            }

            if (games.Count > 0)
                _cachedRecentGames = games;

            return games;
        }
    }
}
