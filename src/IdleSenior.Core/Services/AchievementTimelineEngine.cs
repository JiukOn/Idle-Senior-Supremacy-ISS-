using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using IdleSenior.Core.Models;

namespace IdleSenior.Core.Services
{
    public enum AchievementPacingProfile
    {
        Fastest,
        Median,
        Completionist,
        Custom
    }

    public class TimelinePlanSummary
    {
        public int TotalAchievements { get; set; }
        public int AlreadyUnlockedCount { get; set; }
        public int UnobtainableSkippedCount { get; set; }
        public int ScheduledCount { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public double TargetHours => Math.Round(TotalDuration.TotalHours, 1);
    }

    /// <summary>
    /// Builds a realistic, human-like achievement unlock schedule by combining:
    /// 1. Unobtainable achievement filtering (SteamHunters obtainability flags)
    /// 2. Semantic prerequisite detection (tiered chains, chapters, and Platinum/100% milestones)
    /// 3. Community rarity curves (SteamHunters localPercentage + Steam schema order)
    /// 4. Non-linear time distribution with organic jitter
    /// </summary>
    public class AchievementTimelineEngine
    {
        private static readonly Regex PlatinumRegex = new(
            @"\b(unlock all|obtain all|earn all|collect all|all other achievements|every achievement|todas as conquistas|todas as outras conquistas|todos os trof[eé]us|platinum|platina)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NumberTokenRegex = new(
            @"\b(\d+(?:[.,]\d+)?)\b",
            RegexOptions.Compiled);

        private static readonly Regex IdNumberRegex = new(
            @"(\d+)",
            RegexOptions.Compiled);

        private static readonly Regex RomanTokenRegex = new(
            @"\b(I|II|III|IV|V|VI|VII|VIII|IX|X|XI|XII|XV|XX)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// Calculates the recommended total duration (in hours) for a given pacing profile
        /// based on SteamHunters, HowLongToBeat, and the user's current progress.
        /// </summary>
        public double EstimateTargetHours(
            AchievementPacingProfile profile,
            double customHours,
            SteamHuntersAppInfo? shInfo,
            HltbResult? hltbInfo,
            List<AchievementModel> achievements,
            double currentPlaytimeHours,
            bool discountExistingPlaytime)
        {
            if (profile == AchievementPacingProfile.Custom)
                return Math.Max(0.25, customHours);

            double shFastest = shInfo?.FastestHours ?? 0;
            double shMedian = shInfo?.MedianHours ?? 0;
            double hltbMain = hltbInfo?.MainHours ?? 0;
            double hltb100 = hltbInfo?.CompletionistHours ?? 0;

            double fullGameHours = profile switch
            {
                AchievementPacingProfile.Fastest => shFastest > 0
                    ? shFastest * 1.1
                    : (hltbMain > 0 ? hltbMain * 0.7 : 3.0),

                AchievementPacingProfile.Completionist => shMedian > 0
                    ? shMedian
                    : (hltb100 > 0 ? hltb100 : (hltbMain > 0 ? hltbMain * 1.8 : 15.0)),

                // Median (Recommended default): balanced between HLTB Main/100% and SH Fastest/Median
                _ => CalculateBalancedHours(shFastest, shMedian, hltbMain, hltb100)
            };

            // Scale down proportionally if the user already unlocked part of the obtainable achievements
            int obtainableTotal = achievements.Count(a => !a.IsUnobtainable);
            int remainingLocked = achievements.Count(a => !a.IsAchieved && !a.IsUnobtainable);

            if (obtainableTotal > 0 && remainingLocked > 0 && remainingLocked < obtainableTotal)
            {
                double ratio = (double)remainingLocked / obtainableTotal;
                // Keep a realistic curve (endgame achievements take slightly longer per achievement than tutorial ones)
                double weightedRatio = Math.Pow(ratio, 0.8);
                fullGameHours *= Math.Max(0.15, weightedRatio);
            }

            if (discountExistingPlaytime && currentPlaytimeHours > 0)
            {
                // Subtract already played hours while keeping at least a minimum realistic session (e.g. 20% or 0.5h)
                double minFloor = Math.Max(0.5, fullGameHours * 0.2);
                fullGameHours = Math.Max(minFloor, fullGameHours - currentPlaytimeHours);
            }

            return Math.Round(Math.Clamp(fullGameHours, 0.25, 500.0), 1);
        }

        private static double CalculateBalancedHours(double shFastest, double shMedian, double hltbMain, double hltb100)
        {
            if (shFastest > 0 && shMedian > 0)
            {
                // Geometric mean between fastest and median gives a very natural "dedicated player" pace
                double geo = Math.Sqrt(shFastest * shMedian);
                if (hltbMain > 0)
                    return (geo + hltbMain) / 2.0;
                return geo;
            }

            if (hltbMain > 0 && hltb100 > 0)
                return (hltbMain + hltb100) / 2.0;
            if (shMedian > 0)
                return shMedian * 0.75;
            if (hltbMain > 0)
                return hltbMain;
            return 6.0; // Safe default when neither API has completion times
        }

        /// <summary>
        /// Analyzes all achievements, populates PrerequisiteHint, SuggestedOrder, and ScheduledOffset,
        /// and returns the summary of the generated plan.
        /// </summary>
        public TimelinePlanSummary GenerateSchedule(
            List<AchievementModel> achievements,
            double targetDurationHours)
        {
            var summary = new TimelinePlanSummary
            {
                TotalAchievements = achievements.Count,
                AlreadyUnlockedCount = achievements.Count(a => a.IsAchieved),
                UnobtainableSkippedCount = achievements.Count(a => !a.IsAchieved && a.IsUnobtainable)
            };

            // Reset previous schedule markers
            foreach (var a in achievements)
            {
                a.PrerequisiteId = null;
                a.IsPlatinumMilestone = false;
                a.PrerequisiteHint = string.Empty;
                a.SuggestedOrder = 0;
                a.ScheduledOffset = null;
                a.UpdateScheduleBadgeText();
            }

            // Step 1: Detect Prerequisites & Platinum Milestones across ALL achievements
            DetectPrerequisitesAndChains(achievements);

            // Step 2: Filter to locked & obtainable achievements
            var candidates = achievements
                .Where(a => !a.IsAchieved && !a.IsUnobtainable)
                .ToList();

            if (candidates.Count == 0)
                return summary;

            // Step 3: Sort candidates by Rarity + SchemaOrder, then enforce Topological Prerequisite Order
            var ordered = OrderTopologically(candidates);

            // Step 4: Assign human-readable Prerequisite / Progression Hints for UI clarity
            AssignProgressionHints(ordered, achievements);

            // Step 5: Compute Non-Linear Timestamps with Jitter
            AssignTimestamps(ordered, TimeSpan.FromHours(Math.Max(0.1, targetDurationHours)));

            summary.ScheduledCount = ordered.Count;
            summary.TotalDuration = ordered.Last().ScheduledOffset ?? TimeSpan.Zero;
            return summary;
        }

        private void DetectPrerequisitesAndChains(List<AchievementModel> allAchievements)
        {
            // 1. Identify Platinum / "Unlock all achievements" milestone
            foreach (var ach in allAchievements)
            {
                string combined = $"{ach.Name} {ach.Description}";
                if (PlatinumRegex.IsMatch(combined) ||
                    ach.Id.Equals("PLATINUM", StringComparison.OrdinalIgnoreCase) ||
                    ach.Id.Equals("ALL_ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase))
                {
                    ach.IsPlatinumMilestone = true;
                    ach.PrerequisiteHint = "🏆 Platina (Requer todas as outras)";
                }
            }

            // 2. Group by numeric templates in Description / Name / Id to detect Tiered Chains
            //    (e.g. "Complete Chapter 1" -> "Complete Chapter 2", "Kill 100" -> "Kill 1000")
            var chainGroups = new Dictionary<string, List<(AchievementModel Ach, double Value)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var ach in allAchievements.Where(a => !a.IsPlatinumMilestone))
            {
                // Try Description numeric template first (most accurate for grind/chapter tiers)
                if (TryExtractNumericTemplate(ach.Description, out string descTemplate, out double descVal) && descTemplate.Length >= 8)
                {
                    string key = $"DESC:{descTemplate}";
                    if (!chainGroups.TryGetValue(key, out var list))
                        chainGroups[key] = list = new List<(AchievementModel, double)>();
                    list.Add((ach, descVal));
                    continue;
                }

                // Try Name numeric or Roman numeral template (e.g. "Act I", "Act II", "Level 5", "Level 10")
                if (TryExtractNumericTemplate(ach.Name, out string nameTemplate, out double nameVal) && nameTemplate.Length >= 4)
                {
                    string key = $"NAME:{nameTemplate}";
                    if (!chainGroups.TryGetValue(key, out var list))
                        chainGroups[key] = list = new List<(AchievementModel, double)>();
                    list.Add((ach, nameVal));
                    continue;
                }

                // Try API ID prefix template (e.g. "CHAP_1", "CHAP_2")
                if (TryExtractIdTemplate(ach.Id, out string idTemplate, out double idVal) && idTemplate.Length >= 4)
                {
                    string key = $"ID:{idTemplate}";
                    if (!chainGroups.TryGetValue(key, out var list))
                        chainGroups[key] = list = new List<(AchievementModel, double)>();
                    list.Add((ach, idVal));
                }
            }

            // Link tiers within each discovered chain
            foreach (var kvp in chainGroups)
            {
                // Distinct numeric tiers sorted ascending
                var tiers = kvp.Value
                    .GroupBy(x => x.Value)
                    .Select(g => g.First())
                    .OrderBy(x => x.Value)
                    .ToList();

                if (tiers.Count < 2) continue;

                // Validate that the chain makes sense (first tier shouldn't be way rarer than last tier unless percentages are 0)
                if (tiers[0].Ach.EffectivePercentage > 0 &&
                    tiers[^1].Ach.EffectivePercentage > tiers[0].Ach.EffectivePercentage + 15.0)
                {
                    // Probably an unrelated number (like a year "1999" vs "2000"), skip linking
                    continue;
                }

                for (int i = 1; i < tiers.Count; i++)
                {
                    var current = tiers[i].Ach;
                    var prev = tiers[i - 1].Ach;
                    if (current.Id != prev.Id)
                    {
                        current.PrerequisiteId = prev.Id;
                        current.PrerequisiteHint = $"🔗 Requer: {prev.Name}";
                    }
                }
            }
        }

        private static bool TryExtractNumericTemplate(string text, out string template, out double value)
        {
            template = string.Empty;
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var numMatches = NumberTokenRegex.Matches(text);
            if (numMatches.Count == 1)
            {
                string raw = numMatches[0].Groups[1].Value.Replace(",", "");
                if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                {
                    template = NumberTokenRegex.Replace(text.Trim(), "{#}").ToLowerInvariant();
                    return true;
                }
            }

            var romanMatches = RomanTokenRegex.Matches(text);
            if (romanMatches.Count == 1)
            {
                value = RomanToInt(romanMatches[0].Groups[1].Value);
                if (value > 0)
                {
                    template = RomanTokenRegex.Replace(text.Trim(), "{R}").ToLowerInvariant();
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractIdTemplate(string id, out string template, out double value)
        {
            template = string.Empty;
            value = 0;
            if (string.IsNullOrWhiteSpace(id)) return false;

            var matches = IdNumberRegex.Matches(id);
            if (matches.Count == 1)
            {
                if (double.TryParse(matches[0].Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                {
                    template = IdNumberRegex.Replace(id.Trim(), "{#}").ToUpperInvariant();
                    return true;
                }
            }
            return false;
        }

        private static int RomanToInt(string roman) => roman.ToUpperInvariant() switch
        {
            "I" => 1, "II" => 2, "III" => 3, "IV" => 4, "V" => 5,
            "VI" => 6, "VII" => 7, "VIII" => 8, "IX" => 9, "X" => 10,
            "XI" => 11, "XII" => 12, "XV" => 15, "XX" => 20,
            _ => 0
        };

        private static List<AchievementModel> OrderTopologically(List<AchievementModel> candidates)
        {
            // Base sort:
            // 1. Non-platinum first, Platinum last
            // 2. Higher EffectivePercentage first (most common = earliest in game)
            // 3. Lower SchemaOrder (developer-defined campaign order) as tie-breaker
            var baseSorted = candidates
                .OrderBy(a => a.IsPlatinumMilestone ? 1 : 0)
                .ThenByDescending(a => Math.Round(a.EffectivePercentage, 1))
                .ThenBy(a => a.SchemaOrder)
                .ToList();

            var candidateMap = baseSorted.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AchievementModel>(baseSorted.Count);

            void Visit(AchievementModel ach)
            {
                if (visited.Contains(ach.Id)) return;
                if (visiting.Contains(ach.Id)) return; // Cycle guard

                visiting.Add(ach.Id);

                if (!string.IsNullOrEmpty(ach.PrerequisiteId) &&
                    candidateMap.TryGetValue(ach.PrerequisiteId, out var prereq))
                {
                    Visit(prereq);
                }

                visiting.Remove(ach.Id);
                visited.Add(ach.Id);
                result.Add(ach);
            }

            foreach (var ach in baseSorted)
                Visit(ach);

            return result;
        }

        private static void AssignProgressionHints(List<AchievementModel> ordered, List<AchievementModel> allAchievements)
        {
            var allById = allAchievements.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < ordered.Count; i++)
            {
                var ach = ordered[i];
                ach.SuggestedOrder = i + 1;

                if (ach.IsPlatinumMilestone)
                {
                    ach.PrerequisiteHint = "🏆 Final (Após todas as conquistas)";
                    continue;
                }

                if (!string.IsNullOrEmpty(ach.PrerequisiteId) && allById.TryGetValue(ach.PrerequisiteId, out var prereq))
                {
                    string status = prereq.IsAchieved ? "✔ Já desbloqueada" : $"#{prereq.SuggestedOrder}";
                    ach.PrerequisiteHint = $"🔗 Requer: {prereq.Name} ({status})";
                    continue;
                }

                // Provide helpful context based on rarity & position
                if (i == 0 || ach.EffectivePercentage >= 80.0)
                {
                    ach.PrerequisiteHint = "🟢 Início imediato (Logo ao abrir / Tutorial)";
                }
                else if (ach.EffectivePercentage >= 45.0)
                {
                    ach.PrerequisiteHint = $"➡️ Progressão natural (após #{i}: {ordered[i - 1].Name})";
                }
                else if (ach.EffectivePercentage >= 15.0)
                {
                    ach.PrerequisiteHint = $"🔥 Meio/Fim de campanha (após #{i})";
                }
                else
                {
                    ach.PrerequisiteHint = $"💎 Rara / Endgame (após #{i})";
                }
            }
        }

        private static void AssignTimestamps(List<AchievementModel> ordered, TimeSpan totalDuration)
        {
            int count = ordered.Count;
            if (count == 0) return;

            double totalSeconds = Math.Max(120, totalDuration.TotalSeconds);
            var rng = new Random( unchecked(ordered[0].Id.GetHashCode() ^ (int)DateTime.Now.Ticks) );

            // First achievement ("logo que abre o jogo") pops within 2 to 8 minutes (or scaled if totalDuration is tiny)
            double firstMinSeconds = Math.Min(180, totalSeconds * 0.03);
            double firstMaxSeconds = Math.Min(540, totalSeconds * 0.08);
            double currentSeconds = firstMinSeconds + rng.NextDouble() * Math.Max(10, firstMaxSeconds - firstMinSeconds);

            if (count == 1)
            {
                ordered[0].ScheduledOffset = TimeSpan.FromSeconds(Math.Round(currentSeconds));
                return;
            }

            // Highest and lowest percentages among locked candidates to normalize rarity [0..1]
            double maxPct = ordered.Max(a => a.EffectivePercentage);
            double minPct = ordered.Min(a => a.EffectivePercentage);
            double pctRange = Math.Max(1.0, maxPct - minPct);

            double prevSeconds = currentSeconds;
            ordered[0].ScheduledOffset = TimeSpan.FromSeconds(Math.Round(currentSeconds));

            for (int i = 1; i < count; i++)
            {
                var ach = ordered[i];
                var prev = ordered[i - 1];

                // If Platinum milestone, pops 12 to 28 seconds right after the penultimate achievement!
                if (ach.IsPlatinumMilestone && i == count - 1)
                {
                    double platSeconds = prevSeconds + rng.Next(12, 29);
                    ach.ScheduledOffset = TimeSpan.FromSeconds(Math.Round(platSeconds));
                    break;
                }

                // Rank factor [0..1]
                double rankFactor = (double)i / (count - 1);

                // Rarity factor [0..1] (0 = most common, 1 = rarest)
                double rarityFactor = maxPct > 0
                    ? Math.Clamp((maxPct - ach.EffectivePercentage) / pctRange, 0.0, 1.0)
                    : rankFactor;

                // Blended progression factor with power curve (gamma = 1.32)
                double blended = 0.55 * rankFactor + 0.45 * rarityFactor;
                double curved = Math.Pow(blended, 1.32);

                double targetSec = firstMaxSeconds + (totalSeconds - firstMaxSeconds) * curved;

                // Add organic human jitter (+/- up to 3.5% of average interval)
                double avgStep = totalSeconds / count;
                double jitter = (rng.NextDouble() * 2.0 - 1.0) * (avgStep * 0.28);
                targetSec += jitter;

                // Enforce strict monotonic increase after previous achievement
                // If two achievements have nearly identical percentage (< 0.25% diff) and are not prerequisites of each other,
                // they might be a simultaneous unlock (e.g., "Beat game on Normal" + "Beat game on Hard"), allow a short 8-45s gap;
                // otherwise enforce a realistic gap of at least 90s (or 25% of avgStep).
                bool isSimultaneousPair =
                    string.IsNullOrEmpty(ach.PrerequisiteId) &&
                    Math.Abs(ach.EffectivePercentage - prev.EffectivePercentage) < 0.25 &&
                    ach.EffectivePercentage > 0;

                double minStep = isSimultaneousPair
                    ? rng.Next(8, 35)
                    : Math.Max(45.0, Math.Min(300.0, avgStep * 0.22)) + rng.Next(5, 45);

                if (targetSec < prevSeconds + minStep)
                    targetSec = prevSeconds + minStep;

                prevSeconds = targetSec;
                ach.ScheduledOffset = TimeSpan.FromSeconds(Math.Round(targetSec));
            }
        }
    }
}
