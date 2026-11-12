using System;

namespace IdleSenior.Core.Models
{
    public class AccountAnalyticsModel
    {
        public string PersonaName { get; set; } = "Jogador Steam";
        public string AvatarUrl { get; set; } = "";
        public int SteamLevel { get; set; } = 0;
        public string ProfileUrl { get; set; } = "";

        // Account Status & Integrity
        public bool VacBanned { get; set; } = false;
        public int GameBansCount { get; set; } = 0;
        public bool CommunityBanned { get; set; } = false;
        public string TradeBanState { get; set; } = "None";
        public string MemberSince { get; set; } = "";
        public int TotalXp { get; set; } = 0;
        public int CurrentLevelXp { get; set; } = 0;
        public int XpToNextLevel { get; set; } = 0;
        public int NormalBadgesCount { get; set; } = 0;
        public int SpecialBadgesCount { get; set; } = 0;
        public double LevelProgressPercent => (CurrentLevelXp + XpToNextLevel) > 0
            ? Math.Round((double)CurrentLevelXp / (CurrentLevelXp + XpToNextLevel) * 100.0, 1)
            : 0.0;

        // Total Playtime
        public double TotalPlaytimeHours { get; set; }
        public double PaidGamesPlaytimeHours { get; set; }
        public double FreeGamesPlaytimeHours { get; set; }
        public double TotalPlaytimeDays => Math.Round(TotalPlaytimeHours / 24.0, 1);
        public double PlayedGamesPercent => TotalOwnedGames > 0 ? Math.Round((double)(TotalOwnedGames - NeverPlayedCount) / TotalOwnedGames * 100.0, 1) : 0.0;
        public double AveragePlaytimePerPlayedGameHours => (TotalOwnedGames - NeverPlayedCount) > 0
            ? Math.Round(TotalPlaytimeHours / (TotalOwnedGames - NeverPlayedCount), 1)
            : 0.0;

        // Library Diagnosis
        public int TotalOwnedGames { get; set; }
        public int NeverPlayedCount { get; set; }
        public double NeverPlayedPercent => TotalOwnedGames > 0 ? Math.Round((double)NeverPlayedCount / TotalOwnedGames * 100.0, 1) : 0.0;
        public int PlayedUnder5hCount { get; set; }
        public double PlayedUnder5hPercent => TotalOwnedGames > 0 ? Math.Round((double)PlayedUnder5hCount / TotalOwnedGames * 100.0, 1) : 0.0;
        public int PlayedOver5hCount { get; set; }
        public double PlayedOver5hPercent => TotalOwnedGames > 0 ? Math.Round((double)PlayedOver5hCount / TotalOwnedGames * 100.0, 1) : 0.0;

        // Achievements & 100%
        public int TotalAchievementsUnlocked { get; set; }
        public int TotalAchievementsPossible { get; set; }
        public int PerfectGamesCount { get; set; }
        public double CompletionRate => TotalAchievementsPossible > 0 ? Math.Round((double)TotalAchievementsUnlocked / TotalAchievementsPossible * 100.0, 1) : 0.0;
        public int HunterPoints { get; set; }

        // Account Value
        public string CurrencySymbol { get; set; } = "R$";
        public int PaidGamesCount { get; set; }
        public decimal TotalStoreValueBrl { get; set; }
        public decimal TotalCurrentDiscountValueBrl { get; set; }
        public decimal EffectiveTodayValueBrl => TotalCurrentDiscountValueBrl > 0 ? TotalCurrentDiscountValueBrl : TotalStoreValueBrl;
        public decimal AverageGameValueBrl => PaidGamesCount > 0 ? Math.Round(EffectiveTodayValueBrl / PaidGamesCount, 2) : 0m;
        public decimal AveragePricePerHourPaidBrl => PaidGamesPlaytimeHours > 0 ? Math.Round(EffectiveTodayValueBrl / (decimal)PaidGamesPlaytimeHours, 2) : 0m;
        public decimal AveragePricePerHourAllBrl => TotalPlaytimeHours > 0 ? Math.Round(EffectiveTodayValueBrl / (decimal)TotalPlaytimeHours, 2) : 0m;

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
