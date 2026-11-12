using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using IdleSenior.Core.Models;
using IdleSenior.Core.Services;

namespace IdleSenior.UI.Services
{
    public class SteamAchievementService
    {
        private readonly HttpClient _httpClient;
        private readonly AppSettingsService _settingsService;
        private readonly SteamHuntersService _steamHuntersService;

        public SteamAchievementService(
            HttpClient httpClient,
            AppSettingsService settingsService,
            SteamHuntersService steamHuntersService)
        {
            _httpClient = httpClient;
            _settingsService = settingsService;
            _steamHuntersService = steamHuntersService;
        }

        // ── Reading achievements with Multi-Source Fallback ────────────────────────

        public async Task<List<AchievementModel>> GetAchievementsAsync(int appId)
        {
            _settingsService.NormalizeIdentity();
            var settings = _settingsService.Current;
            var achievements = new List<AchievementModel>();

            // Option 1 (Primary) — Schema (names, descriptions, icons) via Steam Web API
            if (!string.IsNullOrWhiteSpace(settings.SteamApiKey))
            {
                var schemaUrl = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={settings.SteamApiKey}&appid={appId}";
                try
                {
                    var response = await _httpClient.GetStringAsync(schemaUrl);
                    using var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("game", out var game) &&
                        game.TryGetProperty("availableGameStats", out var stats) &&
                        stats.TryGetProperty("achievements", out var achs))
                    {
                        int order = 1;
                        foreach (var ach in achs.EnumerateArray())
                        {
                            achievements.Add(new AchievementModel
                            {
                                AppId       = appId,
                                Id          = ach.GetProperty("name").GetString() ?? "",
                                Name        = ach.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                                Description = ach.TryGetProperty("description",  out var d)  ? d.GetString()  ?? "" : "",
                                IconUrl     = ach.TryGetProperty("icon",         out var i)  ? i.GetString()  ?? "" : "",
                                IconGrayUrl = ach.TryGetProperty("icongray",     out var ig) ? ig.GetString() ?? "" : "",
                                IsHidden    = ach.TryGetProperty("hidden",       out var h)  && h.GetInt32() == 1,
                                SchemaOrder = order++
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamAchievementService] Option 1 (Schema API) failed: {ex.Message}");
                }
            }

            // Option 2 (Secondary Fallback) — Build schema directly from SteamHunters API if Steam Web API failed or no API Key
            if (achievements.Count == 0)
            {
                try
                {
                    var shUrl = $"https://steamhunters.com/api/apps/{appId}/achievements";
                    var shJson = await _httpClient.GetStringAsync(shUrl);
                    using var shDoc = JsonDocument.Parse(shJson);
                    if (shDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        int order = 1;
                        foreach (var el in shDoc.RootElement.EnumerateArray())
                        {
                            string apiName = el.TryGetProperty("apiName", out var an) ? an.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(apiName)) continue;

                            achievements.Add(new AchievementModel
                            {
                                AppId             = appId,
                                Id                = apiName,
                                Name              = el.TryGetProperty("name", out var n) ? n.GetString() ?? apiName : apiName,
                                Description       = el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                                SchemaOrder       = el.TryGetProperty("achievementId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : order++,
                                GlobalPercentage  = el.TryGetProperty("steamPercentage", out var sp) && sp.ValueKind == JsonValueKind.Number ? Math.Round(sp.GetDouble(), 1) : 0,
                                HuntersPercentage = el.TryGetProperty("localPercentage", out var lp) && lp.ValueKind == JsonValueKind.Number ? Math.Round(lp.GetDouble(), 1) : 0,
                                Points            = el.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Number ? pts.GetInt32() : 0,
                                Obtainability     = el.TryGetProperty("obtainability", out var obt) && obt.ValueKind == JsonValueKind.Number ? obt.GetInt32() : 0
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamAchievementService] Option 2 (SteamHunters Schema Fallback) failed: {ex.Message}");
                }
            }

            // Step 2 — Accurate status + unlock time & SteamHunters rarity enrichment in parallel
            var statusTask = FetchStatusWithFallbackAsync(appId, achievements);
            var huntersTask = _steamHuntersService.EnrichAchievementsAsync(appId, achievements);
            await Task.WhenAll(statusTask, huntersTask);

            return achievements;
        }

        private async Task FetchStatusWithFallbackAsync(int appId, List<AchievementModel> achievements)
        {
            if (achievements.Count == 0) return;
            var settings = _settingsService.Current;
            bool statusLoaded = false;

            // Option 1 (Primary): Steam Web API GetPlayerAchievements/v1
            if (!string.IsNullOrEmpty(settings.SteamApiKey) && !string.IsNullOrEmpty(settings.SteamId64))
            {
                var url = $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={settings.SteamApiKey}&steamid={settings.SteamId64}&appid={appId}";
                try
                {
                    var response = await _httpClient.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("playerstats", out var stats) &&
                        stats.TryGetProperty("achievements", out var achs))
                    {
                        var statusMap = achs.EnumerateArray().ToDictionary(
                            a => a.GetProperty("apiname").GetString() ?? "",
                            a => new
                            {
                                IsAchieved = a.GetProperty("achieved").GetInt32() == 1,
                                UnlockTime = a.TryGetProperty("unlocktime", out var ut) && ut.GetInt64() > 0
                                    ? (DateTime?)DateTimeOffset.FromUnixTimeSeconds(ut.GetInt64()).LocalDateTime
                                    : null
                            },
                            StringComparer.OrdinalIgnoreCase
                        );

                        foreach (var ach in achievements)
                        {
                            if (statusMap.TryGetValue(ach.Id, out var status))
                            {
                                ach.IsAchieved = status.IsAchieved;
                                ach.UnlockTime = status.UnlockTime;
                            }
                        }
                        statusLoaded = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteamAchievementService] Option 1 (GetPlayerAchievements) failed: {ex.Message}");
                }
            }

            // Option 2 (Secondary Fallback): Steam Community XML Stats Feed ({profileUrl}/stats/{appId}/?xml=1)
            if (!statusLoaded)
            {
                string profileUrl = _settingsService.GetEffectiveProfileUrl();
                if (!string.IsNullOrEmpty(profileUrl))
                {
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, $"{profileUrl}/stats/{appId}/?xml=1");
                        if (!string.IsNullOrEmpty(settings.SessionId) && !string.IsNullOrEmpty(settings.SteamLoginSecure))
                        {
                            req.Headers.Add("Cookie", $"sessionid={settings.SessionId}; steamLoginSecure={settings.SteamLoginSecure}");
                        }

                        var resp = await _httpClient.SendAsync(req);
                        var xmlText = await resp.Content.ReadAsStringAsync();

                        if (!string.IsNullOrEmpty(xmlText) && xmlText.TrimStart().StartsWith("<"))
                        {
                            var xdoc = XDocument.Parse(xmlText);
                            var achById = achievements.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

                            foreach (var node in xdoc.Descendants("achievement"))
                            {
                                string apiName = node.Element("apiname")?.Value ?? "";
                                if (string.IsNullOrEmpty(apiName) || !achById.TryGetValue(apiName, out var model))
                                    continue;

                                bool closed = node.Attribute("closed")?.Value == "1";
                                model.IsAchieved = closed;

                                if (closed && long.TryParse(node.Element("unlockTimestamp")?.Value, out long ts) && ts > 0)
                                {
                                    model.UnlockTime = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
                                }

                                // Also populate IconUrl from XML if Schema API wasn't used
                                if (string.IsNullOrEmpty(model.IconUrl))
                                {
                                    model.IconUrl = (closed ? node.Element("iconClosed")?.Value : node.Element("iconOpen")?.Value) ?? "";
                                    model.IconGrayUrl = node.Element("iconOpen")?.Value ?? "";
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SteamAchievementService] Option 2 (XML Stats Fallback) failed: {ex.Message}");
                    }
                }
            }
        }

        // ── Writing achievements via Isolated Worker Process ───────────────────────

        /// <summary>
        /// Saves achievement states by executing the dedicated IdleSenior.AchievementWorker.exe process.
        /// Isolating this operation in a child process ensures the Steam Client IPC session remains
        /// clean and allows switching between different games without AppIdMismatch errors.
        /// </summary>
        public bool SaveAchievements(int appId, List<AchievementModel> achievements)
        {
            string workerExe = FindAchievementWorkerPath();
            if (string.IsNullOrEmpty(workerExe) || !File.Exists(workerExe))
            {
                Debug.WriteLine("[SteamAchievementService] IdleSenior.AchievementWorker.exe não encontrado!");
                return false;
            }

            string tempFile = Path.Combine(Path.GetTempPath(), $"iss_ach_{appId}_{Guid.NewGuid():N}.json");

            try
            {
                var payload = new
                {
                    appId = appId,
                    achievements = achievements.Select(a => new
                    {
                        id = a.Id,
                        achieved = a.IsAchieved
                    }).ToList()
                };

                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempFile, json);

                var startInfo = new ProcessStartInfo
                {
                    FileName = workerExe,
                    Arguments = $"\"{tempFile}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(workerExe) ?? "",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) return false;

                process.OutputDataReceived += (s, e) => { if (e.Data != null) Debug.WriteLine($"[AchWorker] {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) Debug.WriteLine($"[AchWorker ERR] {e.Data}"); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait up to 25 seconds for callbacks and Steam server sync
                bool finished = process.WaitForExit(25000);
                if (!finished)
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamAchievementService] Falha ao salvar conquistas: {ex.Message}");
                return false;
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        private static string FindAchievementWorkerPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, "IdleSenior.AchievementWorker.exe");
            if (File.Exists(path)) return path;

            // Search in development output folders
            string current = baseDir;
            for (int i = 0; i < 8; i++)
            {
                string[] possiblePaths = {
                    Path.Combine(current, "src", "IdleSenior.AchievementWorker", "bin", "Debug", "net9.0", "IdleSenior.AchievementWorker.exe"),
                    Path.Combine(current, "src", "IdleSenior.AchievementWorker", "bin", "Release", "net9.0", "IdleSenior.AchievementWorker.exe"),
                    Path.Combine(current, "IdleSenior.AchievementWorker", "bin", "Debug", "net9.0", "IdleSenior.AchievementWorker.exe"),
                    Path.Combine(current, "IdleSenior.AchievementWorker", "bin", "Release", "net9.0", "IdleSenior.AchievementWorker.exe")
                };

                foreach (var p in possiblePaths)
                {
                    if (File.Exists(p)) return p;
                }

                current = Path.GetDirectoryName(current) ?? "";
                if (string.IsNullOrEmpty(current)) break;
            }

            return string.Empty;
        }
    }
}
