using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IdleSenior.Core.Services
{
    public class SteamGuideSummary
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
    }

    /// <summary>
    /// Fetches top community achievement guides for a given AppId:
    /// 1. Option 1 (Primary with API Key): Steam IPublishedFileService/QueryFiles/v1 (Official Web API)
    /// 2. Option 2 (Fallback / No API Key): Steam Community Workshop/Guides public HTML parser
    /// Results are cached locally in %LocalAppData%\IdleSeniorSupremacy\cache\guides_cache.json
    /// </summary>
    public class SteamGuidesService
    {
        private readonly HttpClient _httpClient;
        private readonly AppSettingsService _settingsService;
        private readonly ConcurrentDictionary<int, List<SteamGuideSummary>> _cache = new();

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdleSeniorSupremacy",
            "cache");

        private static readonly string CacheFile = Path.Combine(CacheDir, "guides_cache.json");

        public SteamGuidesService(HttpClient httpClient, AppSettingsService settingsService)
        {
            _httpClient = httpClient;
            _settingsService = settingsService;
            LoadDiskCache();
        }

        public async Task<List<SteamGuideSummary>> GetTopGuidesAsync(int appId)
        {
            if (appId <= 0) return new List<SteamGuideSummary>();

            if (_cache.TryGetValue(appId, out var cached) && cached.Count > 0)
                return cached;

            var guides = new List<SteamGuideSummary>();
            var settings = _settingsService.Current;

            // Option 1: Official Steam Web API (if API Key is configured)
            if (!string.IsNullOrWhiteSpace(settings.SteamApiKey))
            {
                try
                {
                    var url = $"https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/?key={settings.SteamApiKey}&appid={appId}&requiredtags[0]=Guides&numperpage=4&return_short_description=true";
                    var json = await _httpClient.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("response", out var resp) &&
                        resp.TryGetProperty("publishedfiledetails", out var detailsArr) &&
                        detailsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in detailsArr.EnumerateArray())
                        {
                            string fileId = g.TryGetProperty("publishedfileid", out var fid) ? fid.GetString() ?? "" : "";
                            string title = g.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(fileId) && !string.IsNullOrEmpty(title))
                            {
                                guides.Add(new SteamGuideSummary
                                {
                                    Title = title,
                                    Url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={fileId}"
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamGuidesService] Option 1 (API) failed: {ex.Message}");
                }
            }

            // Option 2: Fallback to Steam Community public HTML page
            if (guides.Count == 0)
            {
                try
                {
                    var pageUrl = $"https://steamcommunity.com/app/{appId}/guides/?browsefilter=toprated";
                    var html = await _httpClient.GetStringAsync(pageUrl);
                    var pattern = @"(?s)<a[^>]+href=""(https://steamcommunity\.com/sharedfiles/filedetails/\?id=\d+)""[^>]*>.*?<div[^>]+class=""workshopItemTitle[^""]*""[^>]*>(.*?)</div>";
                    var matches = Regex.Matches(html, pattern);

                    var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match m in matches)
                    {
                        string gUrl = m.Groups[1].Value.Trim();
                        string gTitle = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());

                        if (seenUrls.Add(gUrl) && !string.IsNullOrEmpty(gTitle))
                        {
                            guides.Add(new SteamGuideSummary
                            {
                                Title = gTitle,
                                Url = gUrl
                            });
                        }
                        if (guides.Count >= 4) break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamGuidesService] Option 2 (HTML fallback) failed: {ex.Message}");
                }
            }

            if (guides.Count > 0)
            {
                _cache[appId] = guides;
                SaveDiskCache();
            }

            return guides;
        }

        private void LoadDiskCache()
        {
            try
            {
                if (!File.Exists(CacheFile)) return;
                string json = File.ReadAllText(CacheFile);
                var dict = JsonSerializer.Deserialize<Dictionary<int, List<SteamGuideSummary>>>(json);
                if (dict != null)
                {
                    foreach (var kv in dict)
                        _cache[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        private void SaveDiskCache()
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string json = JsonSerializer.Serialize(_cache);
                File.WriteAllText(CacheFile, json);
            }
            catch { }
        }
    }
}
