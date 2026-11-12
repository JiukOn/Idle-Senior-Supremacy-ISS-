using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IdleSenior.Core.Models;

namespace IdleSenior.Core.Services
{
    public class AccountPriceEntry
    {
        public int AppId { get; set; }
        public int InitialCents { get; set; }
        public int FinalCents { get; set; }
        public int LowestSeenCents { get; set; }
        public int DiscountPercent { get; set; }
        public string Currency { get; set; } = "BRL";
        public DateTime LastChecked { get; set; } = DateTime.UtcNow;
    }

    public class AccountAnalyticsService
    {
        private readonly HttpClient _httpClient;
        private readonly AppSettingsService _settingsService;
        private readonly SteamLibraryService _libraryService;
        private readonly SteamWebService _webService;
        private readonly SteamLocalDiscoveryService _discoveryService;
        private readonly SteamHuntersService _steamHuntersService;

        private readonly string _cacheDir;
        private readonly string _analyticsCacheFile;
        private readonly string _pricesCacheFile;
        private readonly string _achievementsCacheFile;

        public AccountAnalyticsService(
            HttpClient httpClient,
            AppSettingsService settingsService,
            SteamLibraryService libraryService,
            SteamWebService webService,
            SteamLocalDiscoveryService discoveryService,
            SteamHuntersService steamHuntersService)
        {
            _httpClient = httpClient;
            _settingsService = settingsService;
            _libraryService = libraryService;
            _webService = webService;
            _discoveryService = discoveryService;
            _steamHuntersService = steamHuntersService;

            _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleSenior", "cache");
            Directory.CreateDirectory(_cacheDir);

            _analyticsCacheFile = Path.Combine(_cacheDir, "dashboard_analytics.json");
            _pricesCacheFile = Path.Combine(_cacheDir, "app_prices.json");
            _achievementsCacheFile = Path.Combine(_cacheDir, "achievements_summary.json");
        }

        public async Task<AccountAnalyticsModel> GetOrLoadCachedAnalyticsAsync()
        {
            if (File.Exists(_analyticsCacheFile))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(_analyticsCacheFile);
                    var cached = JsonSerializer.Deserialize<AccountAnalyticsModel>(json);
                    if (cached != null) return cached;
                }
                catch { }
            }

            return new AccountAnalyticsModel();
        }

        public void SaveAnalyticsCache(AccountAnalyticsModel model)
        {
            try
            {
                model.LastUpdated = DateTime.UtcNow;
                string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_analyticsCacheFile, json);
            }
            catch { }
        }

        public void CalculateLibraryDiagnosis(List<GameModel> games, AccountAnalyticsModel model)
        {
            if (games == null || games.Count == 0) return;

            model.TotalOwnedGames = games.Count;
            model.TotalPlaytimeHours = Math.Round(games.Sum(g => g.PlaytimeHours), 1);
            model.NeverPlayedCount = games.Count(g => g.PlaytimeHours <= 0);
            model.PlayedUnder5hCount = games.Count(g => g.PlaytimeHours > 0 && g.PlaytimeHours < 5.0);
            model.PlayedOver5hCount = games.Count(g => g.PlaytimeHours >= 5.0);
        }

        public async Task FetchProfileSummaryAsync(AccountAnalyticsModel model)
        {
            var settings = _settingsService.Current;
            string steamId = settings.SteamId64;

            if (string.IsNullOrWhiteSpace(steamId))
            {
                // Fallback: check locally discovered accounts
                var accounts = _discoveryService.DiscoverLocalAccounts();
                if (accounts.Count > 0)
                {
                    steamId = accounts[0].SteamId64;
                    if (string.IsNullOrWhiteSpace(model.PersonaName) || model.PersonaName == "Jogador Steam")
                        model.PersonaName = accounts[0].PersonaName;
                }
            }

            if (string.IsNullOrWhiteSpace(steamId)) return;

            try
            {
                // 1. Fetch XML profile (PersonaName, Avatar, CustomUrl)
                string xmlUrl = $"https://steamcommunity.com/profiles/{steamId}/?xml=1";
                string xml = await _httpClient.GetStringAsync(xmlUrl);

                var nameMatch = Regex.Match(xml, @"<steamID><!\[CDATA\[(.*?)\]\]></steamID>");
                if (nameMatch.Success && !string.IsNullOrWhiteSpace(nameMatch.Groups[1].Value))
                    model.PersonaName = nameMatch.Groups[1].Value;

                var avatarMatch = Regex.Match(xml, @"<avatarFull><!\[CDATA\[(.*?)\]\]></avatarFull>");
                if (avatarMatch.Success && !string.IsNullOrWhiteSpace(avatarMatch.Groups[1].Value))
                    model.AvatarUrl = avatarMatch.Groups[1].Value;

                var customUrlMatch = Regex.Match(xml, @"<customURL><!\[CDATA\[(.*?)\]\]></customURL>");
                if (customUrlMatch.Success && !string.IsNullOrWhiteSpace(customUrlMatch.Groups[1].Value))
                    model.ProfileUrl = $"https://steamcommunity.com/id/{customUrlMatch.Groups[1].Value}";
                else
                    model.ProfileUrl = $"https://steamcommunity.com/profiles/{steamId}";

                var vacMatch = Regex.Match(xml, @"<vacBanned>(\d+)</vacBanned>");
                if (vacMatch.Success)
                    model.VacBanned = vacMatch.Groups[1].Value == "1";

                var tradeMatch = Regex.Match(xml, @"<tradeBanState>([^<]+)</tradeBanState>");
                if (tradeMatch.Success)
                    model.TradeBanState = tradeMatch.Groups[1].Value;

                var memberMatch = Regex.Match(xml, @"<memberSince>([^<]+)</memberSince>");
                if (memberMatch.Success)
                    model.MemberSince = memberMatch.Groups[1].Value;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AccountAnalytics] XML fetch failed: {ex.Message}");
            }

            // 2. Fetch Steam Level, XP & Badges + Bans via Steam Web API (2 lightweight JSON calls)
            try
            {
                if (!string.IsNullOrWhiteSpace(settings.SteamApiKey))
                {
                    try
                    {
                        string badgesApiUrl = $"https://api.steampowered.com/IPlayerService/GetBadges/v1/?key={settings.SteamApiKey}&steamid={steamId}";
                        string badgesJson = await _httpClient.GetStringAsync(badgesApiUrl);
                        using var doc = JsonDocument.Parse(badgesJson);
                        if (doc.RootElement.TryGetProperty("response", out var resp))
                        {
                            if (resp.TryGetProperty("player_level", out var lvl))
                                model.SteamLevel = lvl.GetInt32();
                            if (resp.TryGetProperty("player_xp", out var pxp))
                                model.TotalXp = pxp.GetInt32();
                            if (resp.TryGetProperty("player_xp_needed_current_level", out var curLvlXp) &&
                                resp.TryGetProperty("player_xp_needed_to_level_up", out var toNext))
                            {
                                model.CurrentLevelXp = Math.Max(0, model.TotalXp - curLvlXp.GetInt32());
                                model.XpToNextLevel = Math.Max(0, toNext.GetInt32());
                            }
                            if (resp.TryGetProperty("badges", out var badgesArr) && badgesArr.ValueKind == JsonValueKind.Array)
                            {
                                int normal = 0, special = 0;
                                foreach (var b in badgesArr.EnumerateArray())
                                {
                                    if (b.TryGetProperty("appid", out var appIdProp) && appIdProp.ValueKind == JsonValueKind.Number && appIdProp.GetInt32() > 0)
                                        normal++;
                                    else
                                        special++;
                                }
                                model.NormalBadgesCount = normal;
                                model.SpecialBadgesCount = special;
                            }
                        }
                    }
                    catch { }

                    try
                    {
                        string bansApiUrl = $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={settings.SteamApiKey}&steamids={steamId}";
                        string bansJson = await _httpClient.GetStringAsync(bansApiUrl);
                        using var bansDoc = JsonDocument.Parse(bansJson);
                        if (bansDoc.RootElement.TryGetProperty("players", out var playersArr) &&
                            playersArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var p in playersArr.EnumerateArray())
                            {
                                if (p.TryGetProperty("VACBanned", out var vac))
                                    model.VacBanned = vac.GetBoolean();
                                if (p.TryGetProperty("NumberOfGameBans", out var gb))
                                    model.GameBansCount = gb.GetInt32();
                                if (p.TryGetProperty("CommunityBanned", out var cb))
                                    model.CommunityBanned = cb.GetBoolean();
                                if (p.TryGetProperty("EconomyBan", out var eb))
                                    model.TradeBanState = eb.GetString() ?? "None";
                                break;
                            }
                        }
                    }
                    catch { }
                }

                // Fallback: scrape profile HTML for level if API key unavailable or returned 0
                if (model.SteamLevel == 0)
                {
                    string profileHtmlUrl = $"https://steamcommunity.com/profiles/{steamId}/";
                    var req = new HttpRequestMessage(HttpMethod.Get, profileHtmlUrl);
                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                    var respMsg = await _httpClient.SendAsync(req);
                    if (respMsg.IsSuccessStatusCode)
                    {
                        string html = await respMsg.Content.ReadAsStringAsync();
                        var levelMatch = Regex.Match(html, @"friendPlayerLevelNum[^>]*>(\d+)<");
                        if (levelMatch.Success && int.TryParse(levelMatch.Groups[1].Value, out int parsedLevel))
                        {
                            model.SteamLevel = parsedLevel;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AccountAnalytics] Level fetch failed: {ex.Message}");
            }
        }

        public async Task FetchAchievementsSummaryAsync(AccountAnalyticsModel model, bool forceRefresh)
        {
            if (!forceRefresh && File.Exists(_achievementsCacheFile))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(_achievementsCacheFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("TotalAchievementsUnlocked", out var u)) model.TotalAchievementsUnlocked = u.GetInt32();
                    if (root.TryGetProperty("TotalAchievementsPossible", out var p)) model.TotalAchievementsPossible = p.GetInt32();
                    if (root.TryGetProperty("PerfectGamesCount", out var pf)) model.PerfectGamesCount = pf.GetInt32();
                    if (root.TryGetProperty("HunterPoints", out var hp)) model.HunterPoints = hp.GetInt32();
                    if (model.PerfectGamesCount > 0 || model.TotalAchievementsUnlocked > 0)
                        return;
                }
                catch { }
            }

            var settings = _settingsService.Current;
            string steamId = settings.SteamId64;
            string apiKey = settings.SteamApiKey;

            // 1. Parse Profile Showcases directly (Completionist Showcase & Avg Rate)
            if (!string.IsNullOrWhiteSpace(steamId))
            {
                try
                {
                    string profileUrl = !string.IsNullOrWhiteSpace(model.ProfileUrl)
                        ? model.ProfileUrl
                        : $"https://steamcommunity.com/profiles/{steamId}";

                    var req = new HttpRequestMessage(HttpMethod.Get, profileUrl);
                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                    req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                    var resp = await _httpClient.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        string html = await resp.Content.ReadAsStringAsync();

                        // 1a. Match Perfect Games (100% Platinas) from Completionist Showcase
                        var perfectMatch = Regex.Match(html, @"tab=perfect[^>]*>.*?<(?:div|span) class=""value"">\s*(\d+)\s*</(?:div|span)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        if (!perfectMatch.Success)
                        {
                            perfectMatch = Regex.Match(html, @"<(?:div|span) class=""value"">\s*(\d+)\s*</(?:div|span)>\s*<(?:div|span) class=""label"">\s*(?:Completed Games|Perfect Games|Jogos concluídos|Jogos perfeitos)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        }
                        if (perfectMatch.Success && int.TryParse(perfectMatch.Groups[1].Value, out int perfectCount))
                        {
                            model.PerfectGamesCount = perfectCount;
                        }

                        // 1b. Match Unlocked Achievements count from Showcase if present
                        var achMatch = Regex.Match(html, @"<(?:div|span) class=""value"">\s*([\d,\.]+)\s*</(?:div|span)>\s*<(?:div|span) class=""label"">\s*(?:Achievements|Conquistas)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        if (achMatch.Success)
                        {
                            string rawVal = achMatch.Groups[1].Value.Replace(",", "").Replace(".", "").Trim();
                            if (int.TryParse(rawVal, out int unlockedCount))
                            {
                                model.TotalAchievementsUnlocked = unlockedCount;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AccountAnalytics] Profile showcase parse failed: {ex.Message}");
                }
            }

            // 2. Official Steam Web API fetch for Unlocked Achievements across played games
            if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(steamId))
            {
                try
                {
                    string achPerAppFile = Path.Combine(_cacheDir, "user_achievements_cache.json");
                    var achMap = new ConcurrentDictionary<int, int>();

                    if (!forceRefresh && File.Exists(achPerAppFile))
                    {
                        try
                        {
                            string cachedMapJson = await File.ReadAllTextAsync(achPerAppFile);
                            var loadedMap = JsonSerializer.Deserialize<Dictionary<int, int>>(cachedMapJson);
                            if (loadedMap != null && loadedMap.Count > 0)
                            {
                                foreach (var kv in loadedMap) achMap[kv.Key] = kv.Value;
                            }
                        }
                        catch { }
                    }

                    if (forceRefresh || achMap.IsEmpty)
                    {
                        var ownedGames = await _webService.GetOwnedGamesAsync(forceRefresh: false);
                        var playedGames = ownedGames.Where(g => g.PlaytimeHours > 0).ToList();

                        if (playedGames.Count > 0)
                        {
                            var semaphore = new SemaphoreSlim(12);
                            var tasks = playedGames.Select(async game =>
                            {
                                await semaphore.WaitAsync();
                                try
                                {
                                    string apiEndpoint = $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v0001/?appid={game.AppId}&key={apiKey}&steamid={steamId}";
                                    var req = new HttpRequestMessage(HttpMethod.Get, apiEndpoint);
                                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                                    var resp = await _httpClient.SendAsync(req);
                                    if (resp.IsSuccessStatusCode)
                                    {
                                        string body = await resp.Content.ReadAsStringAsync();
                                        using var doc = JsonDocument.Parse(body);
                                        if (doc.RootElement.TryGetProperty("playerstats", out var pstats) &&
                                            pstats.TryGetProperty("achievements", out var achArr) &&
                                            achArr.ValueKind == JsonValueKind.Array)
                                        {
                                            int unlocked = 0;
                                            foreach (var ach in achArr.EnumerateArray())
                                            {
                                                if (ach.TryGetProperty("achieved", out var achVal) && achVal.GetInt32() == 1)
                                                    unlocked++;
                                            }
                                            achMap[game.AppId] = unlocked;
                                        }
                                    }
                                }
                                catch { }
                                finally
                                {
                                    semaphore.Release();
                                }
                            }).ToList();

                            await Task.WhenAll(tasks);

                            try
                            {
                                string mapJson = JsonSerializer.Serialize(achMap, new JsonSerializerOptions { WriteIndented = true });
                                await File.WriteAllTextAsync(achPerAppFile, mapJson);
                            }
                            catch { }
                        }
                    }

                    if (!achMap.IsEmpty)
                    {
                        model.TotalAchievementsUnlocked = achMap.Values.Sum();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AccountAnalytics] Web API achievements fetch failed: {ex.Message}");
                }
            }

            // Calculate Hunter points
            model.HunterPoints = (model.TotalAchievementsUnlocked * 12) + (model.PerfectGamesCount * 250);

            // Persist cache
            try
            {
                string saveJson = JsonSerializer.Serialize(new
                {
                    model.TotalAchievementsUnlocked,
                    model.TotalAchievementsPossible,
                    model.PerfectGamesCount,
                    model.HunterPoints,
                    CachedAt = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_achievementsCacheFile, saveJson);
            }
            catch { }
        }

        public static (string CountryCode, string Symbol) GetCurrencyRegionAndSymbol(string? currency) =>
            (currency ?? "BRL").ToUpperInvariant() switch
            {
                "USD" => ("us", "$"),
                "EUR" => ("de", "€"),
                "GBP" => ("gb", "£"),
                _ => ("br", "R$")
            };

        public async Task GetOrFetchAccountValueAsync(
            List<GameModel> games,
            AccountAnalyticsModel model,
            bool forceRefresh,
            IProgress<(int Current, int Total)>? progress = null)
        {
            if (games == null || games.Count == 0) return;

            var (cc, symbol) = GetCurrencyRegionAndSymbol(_settingsService.Current.Currency);
            model.CurrencySymbol = symbol;
            string currencyCacheFile = Path.Combine(_cacheDir, $"app_prices_{cc}.json");
            if (!File.Exists(currencyCacheFile) && cc == "br" && File.Exists(_pricesCacheFile))
                currencyCacheFile = _pricesCacheFile;

            var pricesCache = new ConcurrentDictionary<int, AccountPriceEntry>();

            if (File.Exists(currencyCacheFile))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(currencyCacheFile);
                    var list = JsonSerializer.Deserialize<List<AccountPriceEntry>>(json);
                    if (list != null)
                    {
                        foreach (var item in list)
                            pricesCache[item.AppId] = item;
                    }
                }
                catch { }
            }

            var missingGames = forceRefresh
                ? games
                : games.Where(g => !pricesCache.ContainsKey(g.AppId) || (DateTime.UtcNow - pricesCache[g.AppId].LastChecked).TotalDays > 30).ToList();

            if (missingGames.Count > 0)
            {
                int totalToFetch = missingGames.Count;
                int completed = 0;
                const int batchSize = 40;
                var batches = missingGames.Chunk(batchSize).ToList();
                var semaphore = new SemaphoreSlim(3);

                var tasks = batches.Select(async batch =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string appIdsParam = string.Join(",", batch.Select(g => g.AppId));
                        string url = $"https://store.steampowered.com/api/appdetails?appids={appIdsParam}&cc={cc}&filters=price_overview";
                        var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                        var resp = await _httpClient.SendAsync(req);
                        if (resp.IsSuccessStatusCode)
                        {
                            string body = await resp.Content.ReadAsStringAsync();
                            using var doc = JsonDocument.Parse(body);

                            foreach (var game in batch)
                            {
                                if (doc.RootElement.TryGetProperty(game.AppId.ToString(), out var appObj) &&
                                    appObj.TryGetProperty("success", out var okProp) &&
                                    okProp.GetBoolean() &&
                                    appObj.TryGetProperty("data", out var dataObj) &&
                                    dataObj.ValueKind == JsonValueKind.Object &&
                                    dataObj.TryGetProperty("price_overview", out var priceObj))
                                {
                                    int initial = priceObj.TryGetProperty("initial", out var ini) ? ini.GetInt32() : 0;
                                    int final = priceObj.TryGetProperty("final", out var fin) ? fin.GetInt32() : 0;
                                    int discount = priceObj.TryGetProperty("discount_percent", out var disc) ? disc.GetInt32() : 0;
                                    string cur = priceObj.TryGetProperty("currency", out var c) ? c.GetString() ?? "BRL" : "BRL";

                                    int prevLowest = pricesCache.TryGetValue(game.AppId, out var prev) && prev.LowestSeenCents > 0
                                        ? prev.LowestSeenCents
                                        : final;
                                    int lowestSeen = final > 0 ? Math.Min(prevLowest, final) : prevLowest;

                                    pricesCache[game.AppId] = new AccountPriceEntry
                                    {
                                        AppId = game.AppId,
                                        InitialCents = initial > 0 ? initial : final,
                                        FinalCents = final,
                                        LowestSeenCents = lowestSeen,
                                        DiscountPercent = discount,
                                        Currency = cur,
                                        LastChecked = DateTime.UtcNow
                                    };
                                }
                                else if (!pricesCache.ContainsKey(game.AppId))
                                {
                                    // Free-to-play or unpriced game — cache as 0 to avoid re-querying
                                    pricesCache[game.AppId] = new AccountPriceEntry
                                    {
                                        AppId = game.AppId,
                                        InitialCents = 0,
                                        FinalCents = 0,
                                        LowestSeenCents = 0,
                                        DiscountPercent = 0,
                                        Currency = _settingsService.Current.Currency,
                                        LastChecked = DateTime.UtcNow
                                    };
                                }
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        await Task.Delay(120);
                        semaphore.Release();
                        int c = Interlocked.Add(ref completed, batch.Length);
                        progress?.Report((Math.Min(c, totalToFetch), totalToFetch));
                    }
                }).ToList();

                await Task.WhenAll(tasks);

                try
                {
                    string saveFile = Path.Combine(_cacheDir, $"app_prices_{cc}.json");
                    string saveJson = JsonSerializer.Serialize(pricesCache.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(saveFile, saveJson);
                }
                catch { }
            }

            // Calculate account sums & exclude free games from the average division
            decimal totalInitial = 0m;
            decimal totalFinal = 0m;
            int paidCount = 0;
            double paidHours = 0;
            double freeHours = 0;

            foreach (var g in games)
            {
                if (pricesCache.TryGetValue(g.AppId, out var p) && (p.InitialCents > 0 || p.FinalCents > 0))
                {
                    paidCount++;
                    totalInitial += p.InitialCents / 100m;
                    totalFinal += (p.FinalCents > 0 ? p.FinalCents : p.InitialCents) / 100m;
                    paidHours += g.PlaytimeHours;
                }
                else
                {
                    freeHours += g.PlaytimeHours;
                }
            }

            model.PaidGamesCount = paidCount;
            model.TotalStoreValueBrl = Math.Round(totalInitial, 2);
            model.TotalCurrentDiscountValueBrl = Math.Round(totalFinal, 2);
            model.PaidGamesPlaytimeHours = Math.Round(paidHours, 1);
            model.FreeGamesPlaytimeHours = Math.Round(freeHours, 1);
        }

        public async Task<List<SteamNewsItem>> GetRecentGamesNewsAsync(List<GameModel> games)
        {
            var results = new List<SteamNewsItem>();
            if (games == null || games.Count == 0) return results;

            string newsCacheFile = Path.Combine(_cacheDir, "recent_news.json");

            if (File.Exists(newsCacheFile))
            {
                try
                {
                    var fileInfo = new FileInfo(newsCacheFile);
                    if ((DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalHours < 6)
                    {
                        string cachedJson = await File.ReadAllTextAsync(newsCacheFile);
                        var cachedList = JsonSerializer.Deserialize<List<SteamNewsItem>>(cachedJson);
                        if (cachedList != null && cachedList.Count > 0)
                            return cachedList;
                    }
                }
                catch { }
            }

            var topGames = games.Take(3).ToList();
            foreach (var g in topGames)
            {
                try
                {
                    string url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={g.AppId}&count=1&maxlength=150&format=json";
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                    var resp = await _httpClient.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        string json = await resp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("appnews", out var appnews) &&
                            appnews.TryGetProperty("newsitems", out var newsitems) &&
                            newsitems.GetArrayLength() > 0)
                        {
                            var item = newsitems[0];
                            string title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                            string newsUrl = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                            string author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                            string contents = item.TryGetProperty("contents", out var c) ? c.GetString() ?? "" : "";
                            long unix = item.TryGetProperty("date", out var d) ? d.GetInt64() : 0;

                            // Clean BBCode / HTML tags from summary
                            contents = Regex.Replace(contents, @"\[\/?(?:b|i|u|url|img|list|\*)[^\]]*\]", "", RegexOptions.IgnoreCase);
                            contents = Regex.Replace(contents, @"<[^>]+>", "");

                            results.Add(new SteamNewsItem
                            {
                                AppId = g.AppId,
                                GameName = g.Name,
                                Title = title,
                                Url = newsUrl,
                                Author = author,
                                Contents = contents.Trim(),
                                Date = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : DateTime.UtcNow
                            });
                        }
                    }
                }
                catch { }
            }

            if (results.Count > 0)
            {
                try
                {
                    string saveJson = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(newsCacheFile, saveJson);
                }
                catch { }
            }

            return results;
        }
    }
}
