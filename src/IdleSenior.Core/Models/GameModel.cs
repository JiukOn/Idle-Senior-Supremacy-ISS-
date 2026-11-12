using System;
using System.Collections.Generic;

namespace IdleSenior.Core.Models
{
    public class GameModel
    {
        public int AppId { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Optional custom banner URL (e.g. returned directly by Store API with asset hash).
        /// </summary>
        public string? CustomBannerUrl { get; set; }

        /// <summary>
        /// Optional 40-char SHA1 icon hash returned by IPlayerService/GetOwnedGames (img_icon_url).
        /// </summary>
        public string? IconHash { get; set; }

        /// <summary>
        /// Primary Banner URL (Option 1).
        /// </summary>
        public string BannerUrl => !string.IsNullOrWhiteSpace(CustomBannerUrl)
            ? CustomBannerUrl!
            : $"https://shared.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";

        /// <summary>
        /// Ordered fallback chain for Game Banners:
        /// 1. Custom Store URL (if present)
        /// 2. Direct SteamStatic header.jpg
        /// 3. Akamai SteamStatic header.jpg
        /// 4. SteamStatic capsule_231x87.jpg
        /// 5. Legacy AkamaiHD header.jpg
        /// </summary>
        public IReadOnlyList<string> BannerFallbackUrls
        {
            get
            {
                var list = new List<string>(6);
                if (!string.IsNullOrWhiteSpace(CustomBannerUrl))
                    list.Add(CustomBannerUrl!);

                if (AppId > 0)
                {
                    list.Add($"https://shared.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg");
                    list.Add($"https://cdn.akamai.steamstatic.com/steam/apps/{AppId}/header.jpg");
                    list.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg");
                    list.Add($"https://shared.steamstatic.com/store_item_assets/steam/apps/{AppId}/capsule_231x87.jpg");
                    list.Add($"https://steamcdn-a.akamaihd.net/steam/apps/{AppId}/header.jpg");
                }
                return list;
            }
        }

        /// <summary>
        /// Primary Icon URL (Option 1). Uses the real icon hash if available, otherwise falls back to mini capsule.
        /// </summary>
        public string IconUrl => !string.IsNullOrWhiteSpace(IconHash)
            ? $"https://media.steampowered.com/steamcommunity/public/images/apps/{AppId}/{IconHash}.jpg"
            : $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/capsule_sm_120.jpg";

        /// <summary>
        /// Ordered fallback chain for Game Icons:
        /// 1. Steam Community Media CDN with IconHash (if available)
        /// 2. Cloudflare SteamStatic with IconHash (if available)
        /// 3. Cloudflare capsule_sm_120.jpg
        /// 4. Akamai capsule_sm_120.jpg
        /// 5. Akamai header.jpg
        /// </summary>
        public IReadOnlyList<string> IconFallbackUrls
        {
            get
            {
                var list = new List<string>(5);
                if (AppId > 0)
                {
                    if (!string.IsNullOrWhiteSpace(IconHash))
                    {
                        list.Add($"https://media.steampowered.com/steamcommunity/public/images/apps/{AppId}/{IconHash}.jpg");
                        list.Add($"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{AppId}/{IconHash}.jpg");
                    }
                    list.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/capsule_sm_120.jpg");
                    list.Add($"https://cdn.akamai.steamstatic.com/steam/apps/{AppId}/capsule_sm_120.jpg");
                    list.Add($"https://cdn.akamai.steamstatic.com/steam/apps/{AppId}/header.jpg");
                }
                return list;
            }
        }

        public double PlaytimeHours { get; set; }
        private string _lastPlayed = string.Empty;
        public string LastPlayed
        {
            get
            {
                if (!string.IsNullOrEmpty(_lastPlayed)) return _lastPlayed;
                if (LastPlayedUnix > 0)
                    return DateTimeOffset.FromUnixTimeSeconds(LastPlayedUnix).DateTime.ToString("dd/MM/yyyy");
                return "Nunca";
            }
            set => _lastPlayed = value;
        }
        public long LastPlayedUnix { get; set; }

        // Achievement data
        public int EarnedAchievements { get; set; }
        public int TotalAchievements { get; set; }

        // XAML binds to AchievementCount (total) and CompletionPercentage (0-100)
        private int _achievementCount;
        public int AchievementCount
        {
            get => TotalAchievements > 0 ? TotalAchievements : _achievementCount;
            set => _achievementCount = value;
        }
        private double _completionPercentage;
        public double CompletionPercentage
        {
            get => TotalAchievements > 0
                ? Math.Round((double)EarnedAchievements / TotalAchievements * 100, 1)
                : _completionPercentage;
            set => _completionPercentage = value;
        }

        public int ReleaseYear { get; set; }
    }
}
