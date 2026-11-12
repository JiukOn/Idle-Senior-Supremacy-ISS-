using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using IdleSenior.Core.Models;

namespace IdleSenior.Core.Services
{
    public class SteamHuntersAppInfo
    {
        public int AppId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int FastestCompletionMinutes { get; set; }
        public int MedianCompletionMinutes { get; set; }
        public int PlayersPerfectedCount { get; set; }
        public int AchievementCount { get; set; }
        public int UnobtainableAchievementCount { get; set; }
        public int Points { get; set; }

        public double FastestHours => Math.Round(FastestCompletionMinutes / 60.0, 1);
        public double MedianHours => Math.Round(MedianCompletionMinutes / 60.0, 1);
    }

    public class SteamHuntersAchievementData
    {
        public int AchievementId { get; set; }
        public string ApiName { get; set; } = string.Empty;
        public double SteamPercentage { get; set; }
        public double LocalPercentage { get; set; }
        public int Points { get; set; }
        public int Obtainability { get; set; }
    }

    /// <summary>
    /// Fetches community achievement statistics, completion times, rarity curves,
    /// and obtainability flags from the public SteamHunters REST API, with automatic
    /// fallback to Valve's official ISteamUserStats global percentage API.
    /// </summary>
    public class SteamHuntersService
    {
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<int, SteamHuntersAppInfo> _appCache = new();
        private readonly ConcurrentDictionary<int, Dictionary<string, SteamHuntersAchievementData>> _achCache = new();

        public SteamHuntersService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            }
        }

        public async Task<SteamHuntersAppInfo?> GetAppInfoAsync(int appId)
        {
            if (appId <= 0) return null;
            if (_appCache.TryGetValue(appId, out var cached))
                return cached;

            try
            {
                var url = $"https://steamhunters.com/api/apps/{appId}";
                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var info = new SteamHuntersAppInfo
                {
                    AppId = appId,
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    FastestCompletionMinutes = root.TryGetProperty("fastestCompletionTime", out var fc) && fc.ValueKind == JsonValueKind.Number ? fc.GetInt32() : 0,
                    MedianCompletionMinutes = root.TryGetProperty("medianCompletionTime", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : 0,
                    PlayersPerfectedCount = root.TryGetProperty("playersPerfectedCount", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetInt32() : 0,
                    AchievementCount = root.TryGetProperty("achievementCount", out var ac) && ac.ValueKind == JsonValueKind.Number ? ac.GetInt32() : 0,
                    UnobtainableAchievementCount = root.TryGetProperty("unobtainableAchievementCount", out var uac) && uac.ValueKind == JsonValueKind.Number ? uac.GetInt32() : 0,
                    Points = root.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Number ? pts.GetInt32() : 0
                };

                _appCache[appId] = info;
                return info;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamHuntersService] GetAppInfoAsync({appId}) failed: {ex.Message}");
                return null;
            }
        }

        public async Task<Dictionary<string, SteamHuntersAchievementData>> GetAchievementStatsAsync(int appId)
        {
            if (appId <= 0) return new Dictionary<string, SteamHuntersAchievementData>(StringComparer.OrdinalIgnoreCase);
            if (_achCache.TryGetValue(appId, out var cached))
                return cached;

            var result = new Dictionary<string, SteamHuntersAchievementData>(StringComparer.OrdinalIgnoreCase);

            // 1. Primary source: SteamHunters /api/apps/{appId}/achievements
            try
            {
                var url = $"https://steamhunters.com/api/apps/{appId}/achievements";
                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string apiName = el.TryGetProperty("apiName", out var an) ? an.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(apiName)) continue;

                        result[apiName] = new SteamHuntersAchievementData
                        {
                            AchievementId = el.TryGetProperty("achievementId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
                            ApiName = apiName,
                            SteamPercentage = el.TryGetProperty("steamPercentage", out var sp) && sp.ValueKind == JsonValueKind.Number ? Math.Round(sp.GetDouble(), 1) : 0,
                            LocalPercentage = el.TryGetProperty("localPercentage", out var lp) && lp.ValueKind == JsonValueKind.Number ? Math.Round(lp.GetDouble(), 1) : 0,
                            Points = el.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Number ? pts.GetInt32() : 0,
                            Obtainability = el.TryGetProperty("obtainability", out var obt) && obt.ValueKind == JsonValueKind.Number ? obt.GetInt32() : 0
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamHuntersService] SteamHunters achievements fetch failed for {appId}: {ex.Message}");
            }

            // 2. Fallback: Official Steam Web API GetGlobalAchievementPercentagesForApp
            if (result.Count == 0)
            {
                try
                {
                    var fallbackUrl = $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}&format=json";
                    var json = await _httpClient.GetStringAsync(fallbackUrl);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("achievementpercentages", out var ap) &&
                        ap.TryGetProperty("achievements", out var achs) &&
                        achs.ValueKind == JsonValueKind.Array)
                    {
                        int idx = 1;
                        foreach (var el in achs.EnumerateArray())
                        {
                            string apiName = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(apiName)) continue;

                            double pct = 0;
                            if (el.TryGetProperty("percent", out var p))
                            {
                                if (p.ValueKind == JsonValueKind.Number)
                                    pct = p.GetDouble();
                                else if (p.ValueKind == JsonValueKind.String)
                                    double.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out pct);
                            }

                            result[apiName] = new SteamHuntersAchievementData
                            {
                                AchievementId = idx++,
                                ApiName = apiName,
                                SteamPercentage = Math.Round(pct, 1),
                                LocalPercentage = Math.Round(pct, 1),
                                Points = 0,
                                Obtainability = 0
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamHuntersService] Fallback global percentages failed for {appId}: {ex.Message}");
                }
            }

            if (result.Count > 0)
                _achCache[appId] = result;

            return result;
        }

        /// <summary>
        /// Enriches a list of <see cref="AchievementModel"/> with SteamHunters rarity, points, and obtainability.
        /// </summary>
        public async Task EnrichAchievementsAsync(int appId, List<AchievementModel> achievements)
        {
            if (achievements == null || achievements.Count == 0) return;

            var statsMap = await GetAchievementStatsAsync(appId);
            for (int i = 0; i < achievements.Count; i++)
            {
                var ach = achievements[i];
                if (ach.SchemaOrder == 0)
                    ach.SchemaOrder = i + 1;

                if (statsMap.TryGetValue(ach.Id, out var sh))
                {
                    if (sh.AchievementId > 0)
                        ach.SchemaOrder = sh.AchievementId;
                    ach.GlobalPercentage = sh.SteamPercentage;
                    ach.HuntersPercentage = sh.LocalPercentage;
                    ach.Points = sh.Points;
                    ach.Obtainability = sh.Obtainability;
                }

                ach.NotifyMetadataChanged();
            }
        }
    }
}
