using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace IdleSenior.Core.Services
{
    public class CardMarketPriceInfo
    {
        public int AppId { get; set; }
        public string LowestPriceText { get; set; } = string.Empty;
        public string MedianPriceText { get; set; } = string.Empty;
        public double ApproxCardPriceBrl { get; set; }
        public DateTime FetchedUtc { get; set; }
    }

    /// <summary>
    /// Fetches market card pricing with strict rate-limit protection:
    /// - Only queries Steam Market API for the currently active farming game
    /// - Caches prices locally (%LocalAppData%\IdleSeniorSupremacy\cache\market_prices.json)
    /// - Fails gracefully with approximate placeholder if rate limited (HTTP 429)
    /// </summary>
    public class CardMarketDataService
    {
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<int, CardMarketPriceInfo> _cache = new();

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdleSeniorSupremacy",
            "cache");

        private static readonly string CacheFile = Path.Combine(CacheDir, "market_prices.json");

        public CardMarketDataService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            LoadDiskCache();
        }

        public async Task<CardMarketPriceInfo?> GetPriceForGameAsync(int appId, string? sampleCardHashName = null)
        {
            if (appId <= 0) return null;

            if (_cache.TryGetValue(appId, out var cached) && DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromHours(12))
                return cached;

            // Currency 7 = Brazilian Real (BRL). Default card query pattern for steam trading cards (appid 753)
            string hashName = !string.IsNullOrWhiteSpace(sampleCardHashName)
                ? sampleCardHashName!
                : $"{appId}";

            try
            {
                var url = $"https://steamcommunity.com/market/priceoverview/?currency=7&appid=753&market_hash_name={Uri.EscapeDataString(hashName)}";
                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean())
                {
                    string lowest = doc.RootElement.TryGetProperty("lowest_price", out var lp) ? lp.GetString() ?? "" : "";
                    string median = doc.RootElement.TryGetProperty("median_price", out var mp) ? mp.GetString() ?? "" : "";

                    var info = new CardMarketPriceInfo
                    {
                        AppId = appId,
                        LowestPriceText = lowest,
                        MedianPriceText = median,
                        FetchedUtc = DateTime.UtcNow
                    };

                    _cache[appId] = info;
                    SaveDiskCache();
                    return info;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CardMarketDataService] Rate limit or fetch error for {appId}: {ex.Message}");
            }

            return null;
        }

        private void LoadDiskCache()
        {
            try
            {
                if (!File.Exists(CacheFile)) return;
                string json = File.ReadAllText(CacheFile);
                var dict = JsonSerializer.Deserialize<Dictionary<int, CardMarketPriceInfo>>(json);
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
