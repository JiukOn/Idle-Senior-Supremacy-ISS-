using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using IdleSenior.Core.Models;

namespace IdleSenior.Core.Services
{
    /// <summary>
    /// Aggregates security indicators across multiple sources:
    /// 1. Steam Store API: Category 8 = Valve Anti-Cheat (VAC)
    /// 2. AreWeAntiCheatYet Database: Kernel/Driver-level anti-cheats (Easy Anti-Cheat, BattlEye, Vanguard, etc.)
    /// 3. Persistent disk cache: %LocalAppData%\IdleSeniorSupremacy\cache\security_advisories.json
    /// </summary>
    public class SecurityAdvisoryService
    {
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<int, SecurityAdvisoryInfo> _cache = new();
        private Dictionary<string, string>? _awacyBySteamId;
        private bool _awacyLoaded;

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdleSeniorSupremacy",
            "cache");

        private static readonly string CacheFile = Path.Combine(CacheDir, "security_advisories.json");

        public SecurityAdvisoryService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            LoadDiskCache();
        }

        public async Task<SecurityAdvisoryInfo> GetAdvisoryAsync(int appId, IEnumerable<int>? storeCategoryIds = null)
        {
            if (appId <= 0)
                return new SecurityAdvisoryInfo { Level = SecurityRiskLevel.Safe };

            if (_cache.TryGetValue(appId, out var cached))
                return cached;

            await EnsureAwacyDatabaseLoadedAsync();

            bool hasVac = storeCategoryIds?.Contains(8) == true;
            string antiCheat = string.Empty;

            if (_awacyBySteamId != null && _awacyBySteamId.TryGetValue(appId.ToString(), out var acName))
            {
                antiCheat = acName;
            }

            var advisory = new SecurityAdvisoryInfo
            {
                HasVac = hasVac,
                AntiCheatName = antiCheat
            };

            if (!string.IsNullOrEmpty(antiCheat))
            {
                advisory.Level = SecurityRiskLevel.HighRisk;
                advisory.AdvisoryBadgeText = $"🛡️ {antiCheat}";
                advisory.AdvisoryMessage = $"Detectado {antiCheat}. Seguro para farmar com o jogo fechado, mas NUNCA execute o cliente real do jogo ao mesmo tempo!";
            }
            else if (hasVac)
            {
                advisory.Level = SecurityRiskLevel.Caution;
                advisory.AdvisoryBadgeText = "🛡️ VAC Ativo";
                advisory.AdvisoryMessage = "Protegido por Valve Anti-Cheat (VAC). O Idle isolado é seguro, mas não abra o jogo real enquanto estiver farmando!";
            }
            else
            {
                advisory.Level = SecurityRiskLevel.Safe;
                advisory.AdvisoryBadgeText = "🟢 Sem Anti-Cheat";
                advisory.AdvisoryMessage = "Nenhum sistema anti-cheat restritivo detectado.";
            }

            _cache[appId] = advisory;
            SaveDiskCache();
            return advisory;
        }

        private async Task EnsureAwacyDatabaseLoadedAsync()
        {
            if (_awacyLoaded) return;
            _awacyLoaded = true;

            string awacyDiskFile = Path.Combine(CacheDir, "awacy_db.json");
            string? json = null;

            if (File.Exists(awacyDiskFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(awacyDiskFile) < TimeSpan.FromDays(7))
            {
                try
                {
                    json = await File.ReadAllTextAsync(awacyDiskFile);
                }
                catch { }
            }

            if (string.IsNullOrEmpty(json))
            {
                try
                {
                    // Raw GitHub mirror from AreWeAntiCheatYet
                    var url = "https://raw.githubusercontent.com/AreWeAntiCheatYet/AreWeAntiCheatYet/master/games.json";
                    json = await _httpClient.GetStringAsync(url);
                    Directory.CreateDirectory(CacheDir);
                    await File.WriteAllTextAsync(awacyDiskFile, json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SecurityAdvisory] Failed to download AreWeAntiCheatYet DB: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("storeIds", out var stores) &&
                                stores.TryGetProperty("steam", out var steamIdProp) &&
                                steamIdProp.ValueKind == JsonValueKind.String)
                            {
                                string sId = steamIdProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(sId))
                                {
                                    string acName = "Anti-Cheat";
                                    if (item.TryGetProperty("anticheats", out var acs) &&
                                        acs.ValueKind == JsonValueKind.Array &&
                                        acs.GetArrayLength() > 0)
                                    {
                                        acName = acs[0].GetString() ?? "Anti-Cheat";
                                    }
                                    dict[sId] = acName;
                                }
                            }
                        }
                    }

                    _awacyBySteamId = dict;
                }
                catch { }
            }
        }

        private void LoadDiskCache()
        {
            try
            {
                if (!File.Exists(CacheFile)) return;
                string json = File.ReadAllText(CacheFile);
                var dict = JsonSerializer.Deserialize<Dictionary<int, SecurityAdvisoryInfo>>(json);
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
