using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace IdleSenior.Core.Services
{
    public class DiscoveredSteamAccount
    {
        public string SteamId64 { get; set; } = "";
        public string AccountName { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public bool IsMostRecent { get; set; }
        public long Timestamp { get; set; }
        public string ProfileUrl => $"https://steamcommunity.com/profiles/{SteamId64}";

        public string DisplayLabel =>
            !string.IsNullOrWhiteSpace(PersonaName)
                ? $"{PersonaName} ({SteamId64})"
                : !string.IsNullOrWhiteSpace(AccountName)
                    ? $"{AccountName} ({SteamId64})"
                    : SteamId64;
    }

    public class SteamLocalDiscoveryService
    {
        private const ulong SteamId64Base = 76561197960265728UL;
        private readonly HttpClient _httpClient;

        public SteamLocalDiscoveryService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        /// <summary>
        /// Automatically detects Steam accounts logged in on this Windows PC via Registry and loginusers.vdf.
        /// Returns accounts ordered with the currently active / most recent account first.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public List<DiscoveredSteamAccount> DiscoverLocalAccounts()
        {
            var accounts = new Dictionary<string, DiscoveredSteamAccount>(StringComparer.OrdinalIgnoreCase);
            string? activeSteamId64 = null;

            try
            {
                // 1. Check currently active Steam client process user (SteamID3 -> SteamID64)
                using var activeKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                if (activeKey?.GetValue("ActiveUser") is int activeUser && activeUser > 0)
                {
                    ulong id64 = SteamId64Base + (ulong)activeUser;
                    activeSteamId64 = id64.ToString();
                }
            }
            catch { }

            try
            {
                // 2. Locate Steam installation path and parse config/loginusers.vdf
                var candidatePaths = GetSteamInstallationPaths();

                foreach (var steamDir in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string vdfPath = Path.Combine(steamDir, "config", "loginusers.vdf");
                    if (!File.Exists(vdfPath)) continue;

                    string content = File.ReadAllText(vdfPath);
                    ParseLoginUsersVdf(content, accounts);
                    break;
                }
            }
            catch { }

            // 3. Ensure active registry user is present and marked as most recent
            if (!string.IsNullOrEmpty(activeSteamId64))
            {
                if (accounts.TryGetValue(activeSteamId64, out var existing))
                {
                    existing.IsMostRecent = true;
                    existing.Timestamp = long.MaxValue;
                }
                else
                {
                    accounts[activeSteamId64] = new DiscoveredSteamAccount
                    {
                        SteamId64 = activeSteamId64,
                        PersonaName = "Conta Steam Ativa",
                        IsMostRecent = true,
                        Timestamp = long.MaxValue
                    };
                }
            }

            return accounts.Values
                .OrderByDescending(a => a.IsMostRecent)
                .ThenByDescending(a => a.Timestamp)
                .ToList();
        }

        /// <summary>
        /// Returns all potential Steam installation directories on this machine.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public List<string> GetSteamInstallationPaths()
        {
            var paths = new List<string>();

            try
            {
                using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (steamKey?.GetValue("SteamPath") is string regPath && !string.IsNullOrWhiteSpace(regPath))
                    paths.Add(regPath.Replace('/', '\\'));
            }
            catch { }

            try
            {
                using var lmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (lmKey?.GetValue("InstallPath") is string lmPath && !string.IsNullOrWhiteSpace(lmPath))
                    paths.Add(lmPath.Replace('/', '\\'));
            }
            catch { }

            paths.Add(@"D:\Steam");
            paths.Add(@"C:\Program Files (x86)\Steam");
            paths.Add(@"C:\Program Files\Steam");

            return paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Discovers all Steam Library folders across all drives (from libraryfolders.vdf).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public List<string> GetSteamLibraryFolders()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var steamRoots = GetSteamInstallationPaths();

            foreach (var root in steamRoots)
            {
                folders.Add(root);

                string libVdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(libVdf)) continue;

                try
                {
                    string content = File.ReadAllText(libVdf);
                    var matchPath = Regex.Matches(content, @"""path""\s+""([^""]+)""");
                    foreach (Match m in matchPath)
                    {
                        string p = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(p))
                            folders.Add(p);
                    }
                }
                catch { }
            }

            // Also check standard library paths on primary drives
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                string candidate = Path.Combine(drive.RootDirectory.FullName, "SteamLibrary");
                if (Directory.Exists(candidate))
                    folders.Add(candidate);
            }

            return folders.ToList();
        }

        /// <summary>
        /// Reads all installed games from appmanifest_*.acf files across all discovered library folders.
        /// Returns a dictionary mapping AppId -> (Name, LastPlayed, SizeOnDisk).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public Dictionary<int, (string Name, long LastPlayed, long SizeOnDisk)> GetInstalledGames()
        {
            var result = new Dictionary<int, (string Name, long LastPlayed, long SizeOnDisk)>();
            var libraries = GetSteamLibraryFolders();

            foreach (var lib in libraries)
            {
                string steamapps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(steamapps)) continue;

                try
                {
                    var acfFiles = Directory.GetFiles(steamapps, "appmanifest_*.acf");
                    foreach (var acf in acfFiles)
                    {
                        try
                        {
                            string text = File.ReadAllText(acf);
                            var mAppId = Regex.Match(text, @"""appid""\s+""(\d+)""");
                            var mName = Regex.Match(text, @"""name""\s+""([^""]+)""");
                            var mLastPlayed = Regex.Match(text, @"""LastPlayed""\s+""(\d+)""");
                            var mSize = Regex.Match(text, @"""SizeOnDisk""\s+""(\d+)""");

                            if (mAppId.Success && int.TryParse(mAppId.Groups[1].Value, out int appId))
                            {
                                string name = mName.Success ? mName.Groups[1].Value : $"App {appId}";
                                long lastPlayed = mLastPlayed.Success && long.TryParse(mLastPlayed.Groups[1].Value, out long lp) ? lp : 0;
                                long size = mSize.Success && long.TryParse(mSize.Groups[1].Value, out long sz) ? sz : 0;

                                // Filter out Steamworks Common Redistributables or tool runtimes if desired, or keep games
                                if (appId != 228980 && !name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase))
                                {
                                    result[appId] = (name, lastPlayed, size);
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return result;
        }

        /// <summary>
        /// Reads all played and owned games for a user from their Steam client localconfig.vdf.
        /// Maps AppId -> (PlaytimeMinutes, LastPlayedUnix).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public Dictionary<int, (int PlaytimeMinutes, long LastPlayedUnix)> GetLocalUserPlaytime(string steamId64)
        {
            var result = new Dictionary<int, (int PlaytimeMinutes, long LastPlayedUnix)>();
            var steamRoots = GetSteamInstallationPaths();

            // Calculate account ID 32-bit integer from SteamID64
            string accountId3Str = "";
            if (ulong.TryParse(steamId64, out ulong id64) && id64 > SteamId64Base)
            {
                accountId3Str = (id64 - SteamId64Base).ToString();
            }

            foreach (var root in steamRoots)
            {
                string userdataDir = Path.Combine(root, "userdata");
                if (!Directory.Exists(userdataDir)) continue;

                var targetDirs = new List<string>();
                if (!string.IsNullOrEmpty(accountId3Str))
                {
                    string specific = Path.Combine(userdataDir, accountId3Str);
                    if (Directory.Exists(specific))
                        targetDirs.Add(specific);
                }

                // If not found, search all directories under userdata
                if (targetDirs.Count == 0)
                {
                    try
                    {
                        targetDirs.AddRange(Directory.GetDirectories(userdataDir));
                    }
                    catch { }
                }

                foreach (var userDir in targetDirs)
                {
                    string localconfigPath = Path.Combine(userDir, "config", "localconfig.vdf");
                    if (!File.Exists(localconfigPath)) continue;

                    try
                    {
                        string content = File.ReadAllText(localconfigPath);

                        // Important: Only parse the "Software" -> "Valve" -> "Steam" -> "apps" block!
                        // The root section of localconfig.vdf contains a "friends" block with user account IDs (e.g. 293498638)
                        // which must NEVER be confused with Steam AppIds.
                        int softwareIdx = content.IndexOf("\"Software\"", StringComparison.OrdinalIgnoreCase);
                        if (softwareIdx >= 0)
                        {
                            content = content.Substring(softwareIdx);
                        }

                        // Match "AppId" { ... } properly handling nested blocks like cloud { ... }
                        var appStartRegex = new Regex(@"""(\d{2,9})""\s*\{", RegexOptions.Multiline);
                        var playtimeRegex = new Regex(@"""Playtime""\s+""(\d+)""", RegexOptions.IgnoreCase);
                        var lastPlayedRegex = new Regex(@"""LastPlayed""\s+""(\d+)""", RegexOptions.IgnoreCase);

                        int searchPos = 0;
                        while (searchPos < content.Length)
                        {
                            var m = appStartRegex.Match(content, searchPos);
                            if (!m.Success) break;

                            if (int.TryParse(m.Groups[1].Value, out int appId))
                            {
                                int braceStart = m.Index + m.Length - 1;
                                int depth = 0;
                                int i = braceStart;
                                for (; i < content.Length; i++)
                                {
                                    if (content[i] == '{') depth++;
                                    else if (content[i] == '}')
                                    {
                                        depth--;
                                        if (depth == 0) break;
                                    }
                                }

                                if (depth == 0 && i > braceStart)
                                {
                                    string body = content.Substring(braceStart + 1, i - braceStart - 1);
                                    int playtime = 0;
                                    long lastPlayed = 0;

                                    var ptMatch = playtimeRegex.Match(body);
                                    if (ptMatch.Success)
                                        int.TryParse(ptMatch.Groups[1].Value, out playtime);

                                    var lpMatch = lastPlayedRegex.Match(body);
                                    if (lpMatch.Success)
                                        long.TryParse(lpMatch.Groups[1].Value, out lastPlayed);

                                    // Skip common redists or non-game tools
                                    if (appId != 228980 && (playtime > 0 || lastPlayed > 0))
                                    {
                                        result[appId] = (playtime, lastPlayed);
                                    }

                                    searchPos = i + 1;
                                    continue;
                                }
                            }

                            searchPos = m.Index + m.Length;
                        }

                        if (result.Count > 0)
                            return result; // Found user data
                    }
                    catch { }
                }
            }

            return result;
        }

        private static void ParseLoginUsersVdf(string vdfContent, Dictionary<string, DiscoveredSteamAccount> accounts)
        {
            // Match each "7656119..." { ... } block in loginusers.vdf
            var blockRegex = new Regex(@"""(7656119\d{10})""\s*\{([^}]*)\}", RegexOptions.Multiline);
            var kvRegex = new Regex(@"""([^""]+)""\s+""([^""]*)""");

            foreach (Match match in blockRegex.Matches(vdfContent))
            {
                string id64 = match.Groups[1].Value;
                string body = match.Groups[2].Value;

                var acc = new DiscoveredSteamAccount { SteamId64 = id64 };

                foreach (Match kv in kvRegex.Matches(body))
                {
                    string key = kv.Groups[1].Value;
                    string val = kv.Groups[2].Value;

                    if (key.Equals("AccountName", StringComparison.OrdinalIgnoreCase))
                        acc.AccountName = val;
                    else if (key.Equals("PersonaName", StringComparison.OrdinalIgnoreCase))
                        acc.PersonaName = val;
                    else if (key.Equals("MostRecent", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("mostrecent", StringComparison.OrdinalIgnoreCase))
                        acc.IsMostRecent = val == "1";
                    else if (key.Equals("Timestamp", StringComparison.OrdinalIgnoreCase) && long.TryParse(val, out long ts))
                        acc.Timestamp = ts;
                }

                accounts[id64] = acc;
            }
        }

        /// <summary>
        /// Resolves a custom profile URL, vanity nickname, or SteamID64 via Steam Community XML without requiring an API key.
        /// </summary>
        public async Task<DiscoveredSteamAccount?> ResolveProfileUrlOrVanityAsync(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            string trimmed = input.Trim().TrimEnd('/');
            string xmlUrl;

            if (Regex.IsMatch(trimmed, @"^7656119\d{10}$"))
            {
                xmlUrl = $"https://steamcommunity.com/profiles/{trimmed}/?xml=1";
            }
            else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                xmlUrl = $"{trimmed}/?xml=1";
            }
            else
            {
                xmlUrl = $"https://steamcommunity.com/id/{trimmed}/?xml=1";
            }

            try
            {
                string xml = await _httpClient.GetStringAsync(xmlUrl);
                var idMatch = Regex.Match(xml, @"<steamID64>(7656119\d{10})</steamID64>");
                if (!idMatch.Success) return null;

                string id64 = idMatch.Groups[1].Value;
                string persona = "";
                var nameMatch = Regex.Match(xml, @"<steamID><!\[CDATA\[(.*?)\]\]></steamID>");
                if (nameMatch.Success)
                    persona = nameMatch.Groups[1].Value;

                return new DiscoveredSteamAccount
                {
                    SteamId64 = id64,
                    PersonaName = persona,
                    IsMostRecent = true
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
