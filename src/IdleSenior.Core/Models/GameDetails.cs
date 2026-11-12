namespace IdleSenior.Core.Models
{
    public class GameDetails
    {
        public int AppId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string BannerUrl { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // ── Unified Playtime Estimates ────────────────────────────────────────
        /// <summary>Estimated main story duration (e.g. "8.5h"). Primary source: HLTB.</summary>
        public string MainStoryHours { get; set; } = "N/A";
        public string MainStorySource { get; set; } = string.Empty;

        /// <summary>Estimated 100% / Platinum duration (e.g. "25.0h"). Primary source: SteamHunters.</summary>
        public string CompletionistHours { get; set; } = "N/A";
        public string CompletionistSource { get; set; } = string.Empty;

        public string Developer { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public int PlayerCount { get; set; }
        public int Rating { get; set; }
        public string RatingDesc { get; set; } = string.Empty;
        public int MetacriticScore { get; set; }
        public string ReleaseDate { get; set; } = string.Empty;

        // ── Store Pricing ──────────────────────────────────────────────────────
        public bool IsFree { get; set; }
        public string PriceFormatted { get; set; } = string.Empty;
        public string InitialPriceFormatted { get; set; } = string.Empty;
        public int DiscountPercent { get; set; }

        // ── Security & Anti-Cheat Advisory ─────────────────────────────────────
        public SecurityAdvisoryInfo? Security { get; set; }
    }

    public enum SecurityRiskLevel
    {
        Safe,      // No anti-cheat or kernel DRM found
        Caution,   // VAC or server-side anti-cheat (safe for idling, do NOT run game client)
        HighRisk   // Kernel-level / Driver-level anti-cheat (Easy Anti-Cheat, BattlEye, Vanguard, etc.)
    }

    public class SecurityAdvisoryInfo
    {
        public SecurityRiskLevel Level { get; set; } = SecurityRiskLevel.Safe;
        public bool HasVac { get; set; }
        public string AntiCheatName { get; set; } = string.Empty;
        public string DrmName { get; set; } = string.Empty;
        public string AdvisoryBadgeText { get; set; } = string.Empty;
        public string AdvisoryMessage { get; set; } = string.Empty;
    }
}
