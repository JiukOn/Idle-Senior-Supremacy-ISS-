using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Net.Http.Headers;

namespace IdleSenior.Core.Services
{
    public class HowLongToBeatService
    {
        private readonly HttpClient _httpClient;
        private string? _cachedToken;
        private string? _cachedHpKey;
        private string? _cachedHpVal;
        private DateTime _lastHandshake = DateTime.MinValue;

        public HowLongToBeatService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            // Only set headers if not already configured by the DI factory
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            if (!_httpClient.DefaultRequestHeaders.Contains("Referer"))
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://howlongtobeat.com/");
            if (!_httpClient.DefaultRequestHeaders.Contains("Origin"))
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://howlongtobeat.com");
        }

        private async Task EnsureHandshakeAsync()
        {
            if (DateTime.Now - _lastHandshake < TimeSpan.FromMinutes(20) && _cachedToken != null)
                return;

            try
            {
                var initUrl = $"https://howlongtobeat.com/api/find/init?t={DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
                var response = await _httpClient.GetStringAsync(initUrl);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                
                _cachedToken = root.GetProperty("token").GetString();
                _cachedHpKey = root.GetProperty("hpKey").GetString();
                _cachedHpVal = root.GetProperty("hpVal").GetString();
                _lastHandshake = DateTime.Now;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HLTB Handshake failed: {ex.Message}");
                throw;
            }
        }

        public Task<HltbResult?> SearchAsync(string gameName) => SearchAsync(gameName, retriesLeft: 1);

        private async Task<HltbResult?> SearchAsync(string gameName, int retriesLeft)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;
            try
            {
                await EnsureHandshakeAsync();

                if (_cachedToken == null || _cachedHpKey == null || _cachedHpVal == null)
                    return null;

                var searchTerms = gameName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                
                var payload = new Dictionary<string, object>
                {
                    ["searchType"] = "games",
                    ["searchTerms"] = searchTerms,
                    ["searchPage"] = 1,
                    ["size"] = 20,
                    ["searchOptions"] = new
                    {
                        games = new
                        {
                            userId = 0,
                            platform = "",
                            sortCategory = "popular",
                            rangeCategory = "main",
                            rangeTime = new { min = 0, max = 0 },
                            gameplay = new { perspective = "", flow = "", genre = "", difficulty = "" },
                            rangeYear = new { min = 1900, max = 2100 },
                            modifier = ""
                        }
                    },
                    ["useCache"] = true,
                    [_cachedHpKey] = _cachedHpVal // The dynamic key/value
                };

                var request = new HttpRequestMessage(HttpMethod.Post, "https://howlongtobeat.com/api/find");
                request.Headers.Add("x-auth-token", _cachedToken);
                request.Headers.Add("x-hp-key", _cachedHpKey);
                request.Headers.Add("x-hp-val", _cachedHpVal);
                
                var jsonPayload = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // Token might be expired — force a new handshake and retry at most once
                    _cachedToken = null;
                    _lastHandshake = DateTime.MinValue;
                    if (retriesLeft <= 0) return null;
                    return await SearchAsync(gameName, retriesLeft - 1);
                }

                if (!response.IsSuccessStatusCode)
                    return null;

                var jsonResponse = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var dataArray) && dataArray.GetArrayLength() > 0)
                {
                    var bestMatch = dataArray[0];
                    return new HltbResult
                    {
                        Name = bestMatch.GetProperty("game_name").GetString() ?? "",
                        MainHours = bestMatch.TryGetProperty("comp_main", out var main) ? Math.Round(main.GetDouble() / 3600.0, 1) : 0,
                        CompletionistHours = bestMatch.TryGetProperty("comp_100", out var comp100) ? Math.Round(comp100.GetDouble() / 3600.0, 1) : 0
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HLTB Search failed: {ex.Message}");
            }

            return null;
        }
    }

    public class HltbResult
    {
        public string Name { get; set; } = "";
        public double MainHours { get; set; }
        public double CompletionistHours { get; set; }
    }
}
