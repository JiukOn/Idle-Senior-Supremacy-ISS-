using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;
using IdleSenior.Core.Models;

namespace IdleSenior.Core.Services
{
    public class SteamLibraryService
    {
        private readonly HttpClient _httpClient;
        private readonly AppSettingsService _settingsService;
        private readonly SteamLocalDiscoveryService? _localDiscoveryService;

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdleSeniorSupremacy",
            "cache");

        private static readonly string OwnedGamesCachePath = Path.Combine(CacheDir, "owned_games.json");
        private static readonly string ReleaseYearsCachePath = Path.Combine(CacheDir, "release_years.json");

        private readonly ConcurrentDictionary<int, int> _releaseYearCache = new();
        private List<SteamGame>? _cachedCardGames;
        private DateTime _cardGamesCacheTime = DateTime.MinValue;

        public SteamLibraryService(
            HttpClient httpClient,
            AppSettingsService settingsService,
            SteamLocalDiscoveryService? localDiscoveryService = null)
        {
            _httpClient = httpClient;
            _settingsService = settingsService;
            _localDiscoveryService = localDiscoveryService;
            LoadReleaseYearsCacheFromDisk();
        }

        public async Task<List<SteamGame>> GetGamesWithCardDropsAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && _cachedCardGames != null && DateTime.UtcNow - _cardGamesCacheTime < TimeSpan.FromMinutes(3))
            {
                return _cachedCardGames;
            }

            var games = new List<SteamGame>();
            var profileUrl = _settingsService.GetEffectiveProfileUrl();

            if (string.IsNullOrWhiteSpace(profileUrl))
                return games;

            try
            {
                var page = 1;
                while (page <= 30)
                {
                    var url = $"{profileUrl}/badges/?p={page}&sort=p";
                    var html = await GetAuthenticatedAsync(url);
                    if (string.IsNullOrEmpty(html)) break;

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var allBadgeRows = doc.DocumentNode.SelectNodes("//div[contains(@class,'badge_row')]");
                    if (allBadgeRows == null || allBadgeRows.Count == 0) break;

                    var badges = doc.DocumentNode.SelectNodes("//div[contains(@class,'badge_row') and .//span[contains(@class,'progress_info_bold')]]");
                    if (badges != null && badges.Count > 0)
                    {
                        foreach (var badge in badges)
                        {
                            var cardNode = badge.SelectSingleNode(".//span[@class='progress_info_bold']");
                            if (cardNode == null) continue;

                            var cardText = cardNode.InnerText.Trim();
                            var cardMatch = Regex.Match(cardText, @"(\d+)");
                            if (!cardMatch.Success) continue;

                            var remaining = int.Parse(cardMatch.Value);
                            if (remaining == 0) continue;

                            var linkNode = badge.SelectSingleNode(".//a[contains(@href,'/gamecards/')]");
                            if (linkNode == null) continue;

                            var href = linkNode.GetAttributeValue("href", "");
                            var appIdMatch = Regex.Match(href, @"/gamecards/(\d+)");
                            if (!appIdMatch.Success) continue;

                            var nameNode = badge.SelectSingleNode(".//div[@class='badge_title']");
                            var hoursNode = badge.SelectSingleNode(".//div[contains(@class,'badge_title_stats_playtime')]");
                            var hoursMatch = hoursNode != null ? Regex.Match(hoursNode.InnerText, @"[\d.,]+") : Match.Empty;

                            double hoursPlayed = 0;
                            if (hoursMatch.Success)
                            {
                                double.TryParse(hoursMatch.Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out hoursPlayed);
                            }

                            int appId = int.Parse(appIdMatch.Groups[1].Value);
                            if (!games.Any(g => g.AppId == appId))
                            {
                                games.Add(new SteamGame
                                {
                                    AppId = appId,
                                    Name = WebUtility.HtmlDecode(nameNode?.FirstChild?.InnerText.Trim() ?? "Unknown"),
                                    RemainingCards = remaining,
                                    HoursPlayed = hoursPlayed
                                });
                            }
                        }
                    }
                    else
                    {
                        // When sorted by 'p', games with cards are always at the start.
                        // If a page has badge rows but zero card drops, we've reached the end of card drops.
                        break;
                    }

                    var nextPage = doc.DocumentNode.SelectSingleNode("//a[contains(@class,'pagebtn') and not(contains(@class,'disabled')) and (contains(text(),'>') or contains(text(),'&gt;'))]");
                    if (nextPage == null) break;
                    page++;
                }

                if (games.Count > 0)
                {
                    _cachedCardGames = games;
                    _cardGamesCacheTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error scraping Steam badges: {ex.Message}");
                if (_cachedCardGames != null)
                    return _cachedCardGames;
            }

            return games;
        }

        /// <summary>
        /// Fast single-game check via /gamecards/{appId}/ so we don't re-scrape all badge pages.
        /// Returns null if the request failed or session expired, or >= 0 for remaining card count.
        /// </summary>
        public async Task<int?> GetRemainingCardDropsForGameAsync(int appId)
        {
            var profileUrl = _settingsService.GetEffectiveProfileUrl();
            if (string.IsNullOrWhiteSpace(profileUrl))
                return null;

            try
            {
                var url = $"{profileUrl}/gamecards/{appId}/";
                var html = await GetAuthenticatedAsync(url);
                if (string.IsNullOrEmpty(html))
                    return null;

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // If redirected to login page, session is invalid
                if (doc.DocumentNode.SelectSingleNode("//input[@name='username']") != null ||
                    doc.DocumentNode.SelectSingleNode("//div[contains(@class,'login_box')]") != null)
                {
                    return null;
                }

                var dropsContainer = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'badge_title_stats_drops')]");
                if (dropsContainer != null)
                {
                    var boldNode = dropsContainer.SelectSingleNode(".//span[contains(@class,'progress_info_bold')]");
                    if (boldNode != null)
                    {
                        var match = Regex.Match(boldNode.InnerText.Trim(), @"(\d+)");
                        if (match.Success && int.TryParse(match.Value, out int remaining))
                            return remaining;

                        // Present but no digits means "No card drops remaining"
                        return 0;
                    }

                    return 0;
                }

                // Fallback: check if badge page loaded at all
                if (doc.DocumentNode.SelectSingleNode("//div[contains(@class,'badge_current')]") != null ||
                    doc.DocumentNode.SelectSingleNode("//div[contains(@class,'badge_detail_tasks')]") != null)
                {
                    return 0;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Checks whether the configured Steam session cookies are currently authenticated.
        /// </summary>
        public async Task<bool> ValidateSessionAsync()
        {
            var profileUrl = _settingsService.GetEffectiveProfileUrl();
            if (string.IsNullOrWhiteSpace(profileUrl) || string.IsNullOrWhiteSpace(_settingsService.Current.SteamLoginSecure))
                return false;

            try
            {
                var html = await GetAuthenticatedAsync($"{profileUrl}/badges/?p=1");
                if (string.IsNullOrEmpty(html)) return false;

                // Authenticated Steam pages include g_steamID with the logged-in user's ID
                return html.Contains("g_steamID = \"", StringComparison.OrdinalIgnoreCase) &&
                       !html.Contains("g_steamID = false", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // Returns Dictionary<appId, (Earned, Total)> for achievement progress.
        // Uses authenticated scraping of the badges page.
        public async Task<Dictionary<int, (int Earned, int Total)>> GetAchievementStatsAsync()
        {
            var stats = new Dictionary<int, (int Earned, int Total)>();
            var profileUrl = _settingsService.GetEffectiveProfileUrl();

            if (string.IsNullOrWhiteSpace(profileUrl))
                return stats;

            try
            {
                var page = 1;
                while (page <= 15)
                {
                    var url = $"{profileUrl}/badges/?p={page}";
                    var html = await GetAuthenticatedAsync(url);
                    if (string.IsNullOrEmpty(html)) break;

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var badgeRows = doc.DocumentNode.SelectNodes("//div[contains(@class,'badge_row')]");
                    if (badgeRows == null) break;

                    foreach (var row in badgeRows)
                    {
                        var linkNode = row.SelectSingleNode(".//a[contains(@href,'/gamecards/')]") ??
                                      row.SelectSingleNode(".//a[contains(@href,'/badges/')]");
                        if (linkNode == null) continue;

                        var href = linkNode.GetAttributeValue("href", "");
                        var appIdMatch = Regex.Match(href, @"/(?:gamecards|badges)/(\d+)");
                        if (!appIdMatch.Success) continue;

                        int appId = int.Parse(appIdMatch.Groups[1].Value);

                        var progressNode = row.SelectSingleNode(".//div[contains(@class,'badge_progress_info')]");
                        if (progressNode == null) continue;

                        var text = progressNode.InnerText.Trim();

                        // Handles: "8 of 24 achievements", "8 de 24 conquistas alcançadas", "8 / 24"
                        var match = Regex.Match(text, @"(\d+)\s*(?:of|de|/)\s*(\d+)", RegexOptions.IgnoreCase);
                        if (match.Success &&
                            int.TryParse(match.Groups[1].Value, out int earned) &&
                            int.TryParse(match.Groups[2].Value, out int total) &&
                            total > 0)
                        {
                            stats[appId] = (earned, total);
                        }
                    }

                    // Stop if there's no next page button
                    var nextBtn = doc.DocumentNode.SelectSingleNode("//a[contains(@class,'pagebtn') and not(contains(@class,'disabled')) and (text()='>' or text()='&gt;')]");
                    if (nextBtn == null) break;
                    page++;
                }
            }
            catch { }

            return stats;
        }

        /// <summary>
        /// Loads the user's owned games using a 4-tier fallback chain:
        /// 1. Primary: Official Steam Web API (IPlayerService/GetOwnedGames/v1)
        /// 2. Secondary: Authenticated HTML scraping (modern data-profile-gameslist + legacy var rgGames)
        /// 3. Tertiary: Steam Community XML feed ({profileUrl}/games?tab=all&amp;xml=1)
        /// 4. Quaternary: Persistent local disk cache (%LocalAppData%\IdleSeniorSupremacy\cache\owned_games.json)
        /// </summary>
        public async Task<List<SteamGame>> GetOwnedGamesAsync()
        {
            _settingsService.NormalizeIdentity();
            var settings = _settingsService.Current;
            var games = new List<SteamGame>();

            // ── Option 1 (Primary): Steam Web API ─────────────────────────────────
            if (!string.IsNullOrWhiteSpace(settings.SteamApiKey) && !string.IsNullOrWhiteSpace(settings.SteamId64))
            {
                try
                {
                    var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={settings.SteamApiKey}&steamid={settings.SteamId64}&include_appinfo=true&include_played_free_games=true";
                    var json = await _httpClient.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("response", out var resp) &&
                        resp.TryGetProperty("games", out var gamesArray))
                    {
                        games = gamesArray.EnumerateArray().Select(g => new SteamGame
                        {
                            AppId = g.GetProperty("appid").GetInt32(),
                            Name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                            IconHash = g.TryGetProperty("img_icon_url", out var ic) ? ic.GetString() : null,
                            HoursPlayed = g.TryGetProperty("playtime_forever", out var pt) ? Math.Round(pt.GetDouble() / 60.0, 1) : 0,
                            LastPlayedUnix = g.TryGetProperty("rtime_last_played", out var lp) ? lp.GetInt64() : 0
                        }).ToList();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamLibraryService] Option 1 (Web API) failed: {ex.Message}");
                }
            }

            // ── Option 2 (Secondary): Authenticated HTML Scraping (Modern + Legacy) ──
            if (games.Count == 0)
            {
                try
                {
                    string profileUrl = _settingsService.GetEffectiveProfileUrl();
                    if (!string.IsNullOrEmpty(profileUrl))
                    {
                        var html = await GetAuthenticatedAsync($"{profileUrl}/games/?tab=all");
                        if (!string.IsNullOrEmpty(html))
                        {
                            games = ParseGamesFromProfileHtml(html);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamLibraryService] Option 2 (HTML scrape) failed: {ex.Message}");
                }
            }

            // ── Option 3 (Tertiary): Steam Community XML Feed ─────────────────────
            if (games.Count == 0)
            {
                try
                {
                    string profileUrl = _settingsService.GetEffectiveProfileUrl();
                    if (!string.IsNullOrEmpty(profileUrl))
                    {
                        var xmlText = await GetAuthenticatedAsync($"{profileUrl}/games?tab=all&xml=1");
                        if (!string.IsNullOrEmpty(xmlText) && xmlText.TrimStart().StartsWith("<"))
                        {
                            var xdoc = XDocument.Parse(xmlText);
                            foreach (var gEl in xdoc.Descendants("game"))
                            {
                                if (!int.TryParse(gEl.Element("appID")?.Value, out int appId)) continue;
                                string name = gEl.Element("name")?.Value ?? "Unknown";
                                double.TryParse((gEl.Element("hoursOnRecord")?.Value ?? "0").Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out double hrs);

                                games.Add(new SteamGame
                                {
                                    AppId = appId,
                                    Name = name,
                                    HoursPlayed = Math.Round(hrs, 1)
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamLibraryService] Option 3 (XML feed) failed: {ex.Message}");
                }
            }

            // ── Option 4 (Local Discovery): Read from local Steam installation ────
            if (games.Count == 0 && _localDiscoveryService != null)
            {
                try
                {
                    var localGames = GetGamesFromLocalSteam(settings.SteamId64);
                    if (localGames.Count > 0)
                    {
                        games = localGames;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamLibraryService] Option 4 (Local Discovery) failed: {ex.Message}");
                }
            }

            // ── Option 5 (Quaternary): Local Disk Cache Fallback (Offline / Steam Down) ──
            if (games.Count == 0)
            {
                games = LoadOwnedGamesCacheFromDisk();
            }
            else
            {
                // Persist fresh library to disk cache for instant offline fallback
                SaveOwnedGamesCacheToDisk(games);
            }

            // Immediately apply any cached release years from disk (0ms latency)
            foreach (var g in games)
            {
                if (_releaseYearCache.TryGetValue(g.AppId, out int year))
                    g.ReleaseYear = year;
            }

            // Enrich missing release years asynchronously in background so the UI renders immediately!
            if (games.Count > 0)
            {
                var missingYears = games
                    .Where(g => g.ReleaseYear == 0)
                    .OrderByDescending(g => g.HoursPlayed)
                    .Take(60)
                    .ToList();

                if (missingYears.Count > 0)
                {
                    _ = Task.Run(() => EnrichWithReleaseDatesAsync(missingYears));
                }
            }

            return games;
        }

        private List<SteamGame> GetGamesFromLocalSteam(string steamId64)
        {
            var list = new List<SteamGame>();
            if (_localDiscoveryService == null) return list;

            // 1. Gather all locally installed games across all Steam libraries
            var installed = _localDiscoveryService.GetInstalledGames();

            // 2. Gather user's played / owned games history from localconfig.vdf
            var userPlaytimes = _localDiscoveryService.GetLocalUserPlaytime(steamId64);

            var seenAppIds = new HashSet<int>();

            // First add installed games (we know their verified names, last played and install size)
            foreach (var kv in installed)
            {
                int appId = kv.Key;
                string name = kv.Value.Name;
                long lastPlayed = kv.Value.LastPlayed;

                double hours = 0;
                if (userPlaytimes.TryGetValue(appId, out var pt))
                {
                    hours = Math.Round(pt.PlaytimeMinutes / 60.0, 1);
                    if (pt.LastPlayedUnix > lastPlayed)
                        lastPlayed = pt.LastPlayedUnix;
                }

                seenAppIds.Add(appId);
                list.Add(new SteamGame
                {
                    AppId = appId,
                    Name = name,
                    HoursPlayed = hours,
                    LastPlayedUnix = lastPlayed
                });
            }

            // Then add remaining games from localconfig.vdf
            var unverified = new List<SteamGame>();
            foreach (var kv in userPlaytimes)
            {
                int appId = kv.Key;
                if (seenAppIds.Contains(appId)) continue;

                var game = new SteamGame
                {
                    AppId = appId,
                    Name = $"Jogo {appId}",
                    HoursPlayed = Math.Round(kv.Value.PlaytimeMinutes / 60.0, 1),
                    LastPlayedUnix = kv.Value.LastPlayedUnix
                };

                seenAppIds.Add(appId);
                list.Add(game);
                unverified.Add(game);
            }

            // In background, enrich unverified game names from Steamcmd or Store
            if (unverified.Count > 0)
            {
                _ = Task.Run(() => ResolveGameNamesAsync(unverified));
            }

            return list.OrderByDescending(g => g.LastPlayedUnix).ThenByDescending(g => g.HoursPlayed).ToList();
        }

        private async Task ResolveGameNamesAsync(List<SteamGame> games)
        {
            // Resolve names in batches of 10
            foreach (var game in games)
            {
                try
                {
                    var storeJson = await _httpClient.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={game.AppId}&filters=basic");
                    using var doc = JsonDocument.Parse(storeJson);
                    if (doc.RootElement.TryGetProperty(game.AppId.ToString(), out var appObj) &&
                        appObj.TryGetProperty("success", out var succ) && succ.GetBoolean() &&
                        appObj.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("name", out var n))
                    {
                        string name = n.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            game.Name = name;
                        }
                    }
                }
                catch { }

                await Task.Delay(250); // Be respectful to Steam API rate limits
            }

            // Save refreshed names to cache
            SaveOwnedGamesCacheToDisk(games);
        }

        private static List<SteamGame> ParseGamesFromProfileHtml(string html)
        {
            var list = new List<SteamGame>();

            // Modern format (2023+): <div id="gameslist_config" data-profile-gameslist="{&quot;rgGames&quot;:[...]}"
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var configNode = doc.DocumentNode.SelectSingleNode("//div[@id='gameslist_config']");
            if (configNode != null)
            {
                string rawAttr = WebUtility.HtmlDecode(configNode.GetAttributeValue("data-profile-gameslist", ""));
                if (!string.IsNullOrWhiteSpace(rawAttr))
                {
                    using var jsonDoc = JsonDocument.Parse(rawAttr);
                    if (jsonDoc.RootElement.TryGetProperty("rgGames", out var rgGames) && rgGames.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in rgGames.EnumerateArray())
                        {
                            int appId = g.TryGetProperty("appid", out var idProp) ? idProp.GetInt32() : 0;
                            if (appId <= 0) continue;

                            double hours = 0;
                            if (g.TryGetProperty("playtime_forever", out var pf) && pf.ValueKind == JsonValueKind.Number)
                                hours = Math.Round(pf.GetDouble() / 60.0, 1);

                            list.Add(new SteamGame
                            {
                                AppId = appId,
                                Name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                                IconHash = g.TryGetProperty("img_icon_url", out var ic) ? ic.GetString() : null,
                                HoursPlayed = hours,
                                LastPlayedUnix = g.TryGetProperty("rtime_last_played", out var lp) && lp.ValueKind == JsonValueKind.Number ? lp.GetInt64() : 0
                            });
                        }
                        if (list.Count > 0) return list;
                    }
                }
            }

            // Legacy format: var rgGames = [...];
            var match = Regex.Match(html, @"var rgGames = (\[.*?\]);", RegexOptions.Singleline);
            if (match.Success)
            {
                using var jsonDoc = JsonDocument.Parse(match.Groups[1].Value);
                list = jsonDoc.RootElement.EnumerateArray().Select(g => new SteamGame
                {
                    AppId = g.GetProperty("appid").GetInt32(),
                    Name = g.GetProperty("name").GetString() ?? "Unknown",
                    HoursPlayed = g.TryGetProperty("hours_forever", out var h)
                        ? double.Parse((h.GetString() ?? "0").Replace(',', '.'), CultureInfo.InvariantCulture)
                        : 0
                }).ToList();
            }

            return list;
        }

        /// <summary>
        /// Enriches release years with a 2-option fallback per game:
        /// 1. Primary: SteamHunters API (/api/apps/{appId} releaseDate) — fast and no Store rate limit
        /// 2. Secondary: Steam Store API (appdetails?appids={appId}&amp;filters=release_date)
        /// Results are persisted to release_years.json so each game is only resolved once.
        /// </summary>
        public async Task EnrichWithReleaseDatesAsync(List<SteamGame> games)
        {
            if (games == null || games.Count == 0) return;
            bool anyNew = false;

            foreach (var game in games)
            {
                if (_releaseYearCache.TryGetValue(game.AppId, out int cachedYear) && cachedYear > 0)
                {
                    game.ReleaseYear = cachedYear;
                    continue;
                }

                int year = await FetchReleaseYearWithFallbackAsync(game.AppId);
                if (year > 0)
                {
                    game.ReleaseYear = year;
                    _releaseYearCache[game.AppId] = year;
                    anyNew = true;
                }

                await Task.Delay(80);
            }

            if (anyNew)
            {
                SaveReleaseYearsCacheToDisk();
            }
        }

        private async Task<int> FetchReleaseYearWithFallbackAsync(int appId)
        {
            // Option 1: SteamHunters API (fast, lightweight JSON)
            try
            {
                var shJson = await _httpClient.GetStringAsync($"https://steamhunters.com/api/apps/{appId}");
                using var shDoc = JsonDocument.Parse(shJson);
                if (shDoc.RootElement.TryGetProperty("releaseDate", out var rdProp) && rdProp.ValueKind == JsonValueKind.String)
                {
                    string dateStr = rdProp.GetString() ?? "";
                    if (DateTime.TryParse(dateStr, out var dt) && dt.Year >= 1980)
                        return dt.Year;
                }
            }
            catch { }

            // Option 2: Steam Store API (single appid)
            try
            {
                var storeJson = await _httpClient.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=release_date");
                using var doc = JsonDocument.Parse(storeJson);
                if (doc.RootElement.TryGetProperty(appId.ToString(), out var appData) &&
                    appData.TryGetProperty("success", out var success) && success.GetBoolean() &&
                    appData.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("release_date", out var rd))
                {
                    var dateStr = rd.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
                    var yearMatch = Regex.Match(dateStr, @"\b(19\d{2}|20\d{2})\b");
                    if (yearMatch.Success)
                        return int.Parse(yearMatch.Value);
                }
            }
            catch { }

            return 0;
        }

        private void LoadReleaseYearsCacheFromDisk()
        {
            try
            {
                if (!File.Exists(ReleaseYearsCachePath)) return;
                string json = File.ReadAllText(ReleaseYearsCachePath);
                var dict = JsonSerializer.Deserialize<Dictionary<int, int>>(json);
                if (dict != null)
                {
                    foreach (var kv in dict)
                        _releaseYearCache[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        private void SaveReleaseYearsCacheToDisk()
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string json = JsonSerializer.Serialize(_releaseYearCache);
                File.WriteAllText(ReleaseYearsCachePath, json);
            }
            catch { }
        }

        private static void SaveOwnedGamesCacheToDisk(List<SteamGame> games)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string json = JsonSerializer.Serialize(games);
                File.WriteAllText(OwnedGamesCachePath, json);
            }
            catch { }
        }

        private static List<SteamGame> LoadOwnedGamesCacheFromDisk()
        {
            try
            {
                if (!File.Exists(OwnedGamesCachePath)) return new List<SteamGame>();
                string json = File.ReadAllText(OwnedGamesCachePath);
                return JsonSerializer.Deserialize<List<SteamGame>>(json) ?? new List<SteamGame>();
            }
            catch
            {
                return new List<SteamGame>();
            }
        }

        public async Task<string> GetAuthenticatedAsync(string url)
        {
            var settings = _settingsService.Current;
            string sessionId = (settings.SessionId ?? "").Trim().Trim('"');
            string steamLoginSecure = (settings.SteamLoginSecure ?? "").Trim().Trim('"');
            string parental = (settings.SteamParental ?? "").Trim().Trim('"');

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");

            string cookieHeader = $"sessionid={sessionId}; steamLoginSecure={steamLoginSecure};";
            if (!string.IsNullOrEmpty(parental)) cookieHeader += $" steamparental={parental};";
            request.Headers.Add("Cookie", cookieHeader);

            try
            {
                var response = await _httpClient.SendAsync(request);
                return await response.Content.ReadAsStringAsync();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
