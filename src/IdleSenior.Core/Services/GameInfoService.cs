using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using IdleSenior.Core.Models;

namespace IdleSenior.Core.Services
{
    /// <summary>
    /// Fetches game details with multi-source fallback:
    /// 1. Primary: Steam Store API (appdetails) + Reviews API + SteamHunters (100% time) + HowLongToBeat (Main Story time)
    /// 2. Secondary: SteamHunters API (/api/apps/{appId}) for delisted games, ratings, and completion times
    /// 3. Security: SecurityAdvisoryService (Steam VAC + AreWeAntiCheatYet aggregated)
    /// Results are cached in memory for instant subsequent access.
    /// </summary>
    public class GameInfoService
    {
        private readonly HttpClient _httpClient;
        private readonly HowLongToBeatService _hltbService;
        private readonly SteamHuntersService _steamHuntersService;
        private readonly SecurityAdvisoryService _securityService;
        private readonly AppSettingsService _settingsService;

        // Thread-safe cache: "appId_cc" → details
        private readonly ConcurrentDictionary<string, GameDetails> _cache = new();
        private static readonly ConcurrentDictionary<int, string> _diskDescriptionCache = new();
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdleSeniorSupremacy",
            "cache");
        private static readonly string DescriptionsCachePath = Path.Combine(CacheDir, "app_descriptions.json");

        public GameInfoService(
            HttpClient httpClient,
            HowLongToBeatService hltbService,
            SteamHuntersService steamHuntersService,
            SecurityAdvisoryService securityService,
            AppSettingsService settingsService)
        {
            _httpClient = httpClient;
            _hltbService = hltbService;
            _steamHuntersService = steamHuntersService;
            _securityService = securityService;
            _settingsService = settingsService;
            LoadDiskDescriptions();
        }

        private static void LoadDiskDescriptions()
        {
            try
            {
                if (File.Exists(DescriptionsCachePath))
                {
                    string json = File.ReadAllText(DescriptionsCachePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<int, string>>(json);
                    if (dict != null)
                    {
                        foreach (var kv in dict)
                            _diskDescriptionCache[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        private static void SaveDiskDescriptions()
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var dict = new Dictionary<int, string>(_diskDescriptionCache);
                File.WriteAllText(DescriptionsCachePath, JsonSerializer.Serialize(dict));
            }
            catch { }
        }

        public async Task<GameDetails?> GetGameDetailsAsync(int appId)
        {
            if (appId <= 0) return null;

            var (cc, _) = AccountAnalyticsService.GetCurrencyRegionAndSymbol(_settingsService.Current.Currency);
            string langParam = _settingsService.Current.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "english"
                : "brazilian";
            string cacheKey = $"{appId}_{cc}_{langParam}";

            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            var details = new GameDetails
            {
                AppId = appId,
                BannerUrl = $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
            };

            // Check if we have cached description on disk
            if (_diskDescriptionCache.TryGetValue(appId, out var cachedDesc) && !string.IsNullOrWhiteSpace(cachedDesc))
            {
                details.Description = cachedDesc;
            }

            var storeCategoryIds = new List<int>();

            // ── Option 1 (Primary): Steam Store API ───────────────────────────────
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://store.steampowered.com/api/appdetails?appids={appId}&cc={cc}&l={langParam}");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                request.Headers.Add("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");

                var respMsg = await _httpClient.SendAsync(request);
                if (respMsg.IsSuccessStatusCode)
                {
                    var response = await respMsg.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(response);

                    if (doc.RootElement.TryGetProperty(appId.ToString(), out var appData) &&
                        appData.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
                        appData.TryGetProperty("data", out var data))
                    {
                        details.Name        = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        details.BannerUrl   = data.TryGetProperty("header_image", out var hi) && !string.IsNullOrWhiteSpace(hi.GetString())
                            ? hi.GetString()!
                            : details.BannerUrl;

                        string rawDesc = data.TryGetProperty("short_description", out var sd) ? sd.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(rawDesc))
                        {
                            details.Description = System.Net.WebUtility.HtmlDecode(rawDesc);
                            _diskDescriptionCache[appId] = details.Description;
                            _ = Task.Run(SaveDiskDescriptions);
                        }

                        if (data.TryGetProperty("publishers", out var pubs) && pubs.ValueKind == JsonValueKind.Array && pubs.GetArrayLength() > 0)
                            details.Publisher = pubs[0].GetString() ?? "";

                        if (data.TryGetProperty("developers", out var devs) && devs.ValueKind == JsonValueKind.Array && devs.GetArrayLength() > 0)
                            details.Developer = devs[0].GetString() ?? "";

                        if (data.TryGetProperty("release_date", out var rd))
                            details.ReleaseDate = rd.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";

                        if (data.TryGetProperty("metacritic", out var mc) && mc.TryGetProperty("score", out var scoreProp))
                            details.MetacriticScore = scoreProp.GetInt32();

                        if (data.TryGetProperty("is_free", out var isFreeProp) && isFreeProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            details.IsFree = isFreeProp.GetBoolean();

                        if (data.TryGetProperty("price_overview", out var priceOverview))
                        {
                            details.PriceFormatted = priceOverview.TryGetProperty("final_formatted", out var ff) ? ff.GetString() ?? "" : "";
                            details.InitialPriceFormatted = priceOverview.TryGetProperty("initial_formatted", out var initF) ? initF.GetString() ?? "" : "";
                            details.DiscountPercent = priceOverview.TryGetProperty("discount_percent", out var disc) ? disc.GetInt32() : 0;
                        }

                        if (data.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cat in cats.EnumerateArray())
                            {
                                if (cat.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                                    storeCategoryIds.Add(idProp.GetInt32());
                            }
                        }
                    }
                }
            }
            catch { }

            // ── Secondary Metadata & Concurrent Metrics ───────────────────────────
            var shTask     = _steamHuntersService.GetAppInfoAsync(appId);
            var playerTask = FetchPlayerCountAsync(appId);
            var reviewTask = FetchRatingAsync(appId);
            var secTask    = _securityService.GetAdvisoryAsync(appId, storeCategoryIds);

            await Task.WhenAll(shTask, playerTask, reviewTask, secTask);

            var shInfo = await shTask;
            details.Security = await secTask;

            if (string.IsNullOrWhiteSpace(details.Name) && shInfo != null && !string.IsNullOrWhiteSpace(shInfo.Name))
            {
                details.Name = shInfo.Name;
            }

            if (string.IsNullOrWhiteSpace(details.Description))
            {
                details.Description = "Sinopse não disponível no momento.";
            }

            if (string.IsNullOrWhiteSpace(details.Name))
                return null;

            details.PlayerCount = await playerTask;
            var ratingInfo      = await reviewTask;
            details.Rating      = ratingInfo.Percent > 0 ? ratingInfo.Percent : (details.MetacriticScore > 0 ? details.MetacriticScore : 0);
            details.RatingDesc  = ratingInfo.Desc;

            // ── Unified Playtime Estimates (SteamHunters 100% + HLTB Story) ───────
            var hltbResult = await FetchHltbAsync(details.Name);

            // 1. Completionist (100%): SteamHunters is PRIMARY for PC
            if (shInfo != null && shInfo.MedianHours > 0)
            {
                details.CompletionistHours = $"{shInfo.MedianHours:0.0}h";
                details.CompletionistSource = "SteamHunters (Mediana PC)";
            }
            else if (hltbResult != null && hltbResult.CompletionistHours > 0)
            {
                details.CompletionistHours = $"{hltbResult.CompletionistHours:0.0}h";
                details.CompletionistSource = "HowLongToBeat";
            }
            else
            {
                details.CompletionistHours = "N/A";
            }

            // 2. Main Story: HowLongToBeat is PRIMARY
            if (hltbResult != null && hltbResult.MainHours > 0)
            {
                details.MainStoryHours = $"{hltbResult.MainHours:0.0}h";
                details.MainStorySource = "HowLongToBeat";
            }
            else if (shInfo != null && shInfo.FastestHours > 0)
            {
                // Fastest completion time on PC is a solid upper-bound for a direct story run
                details.MainStoryHours = $"{shInfo.FastestHours:0.0}h";
                details.MainStorySource = "SteamHunters (Estimado)";
            }
            else
            {
                details.MainStoryHours = "N/A";
            }

            _cache[cacheKey] = details;
            return details;
        }

        // ── Sub-fetches ───────────────────────────────────────────────────────────

        private async Task<int> FetchPlayerCountAsync(int appId)
        {
            try
            {
                var url = $"https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={appId}";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("player_count", out var cnt))
                    return cnt.GetInt32();
            }
            catch { }
            return 0;
        }

        private async Task<(int Percent, string Desc)> FetchRatingAsync(int appId)
        {
            try
            {
                var url = $"https://api.steampowered.com/IUserReviewsService/GetAppReviews/v1/?appid={appId}";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("query_summary", out var summary))
                {
                    int total    = summary.TryGetProperty("total_reviews", out var tProp) ? tProp.GetInt32() : 0;
                    int positive = summary.TryGetProperty("total_positive", out var pProp) ? pProp.GetInt32() : 0;
                    string desc  = summary.TryGetProperty("review_score_desc", out var dProp) ? dProp.GetString() ?? "" : "";

                    if (total > 0)
                    {
                        int pct = (int)Math.Round((double)positive / total * 100);
                        return (pct, desc);
                    }
                }
            }
            catch { }
            return (0, string.Empty);
        }

        private async Task<HltbResult?> FetchHltbAsync(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                return await _hltbService.SearchAsync(name);
            }
            catch
            {
                return null;
            }
        }
    }
}
