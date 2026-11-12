using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IdleSenior.Core.Models
{
    public class AchievementModel : INotifyPropertyChanged
    {
        private bool _isAchieved;
        private DateTime? _unlockTime;
        private int _suggestedOrder;
        private TimeSpan? _scheduledOffset;
        private string _prerequisiteHint = string.Empty;
        private string _scheduleBadgeText = string.Empty;

        public int AppId { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty;
        public string IconGrayUrl { get; set; } = string.Empty;
        public bool IsHidden { get; set; }
        public int SchemaOrder { get; set; }

        /// <summary>
        /// Ordered fallback chain for Achievement Icons:
        /// 1. Primary IconUrl (from Steam Schema)
        /// 2. Alternative Steam CDN host mirror (Cloudflare <-> Akamai <-> Media)
        /// 3. IconGrayUrl (if primary colored icon fails)
        /// 4. Game mini capsule fallback
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> IconFallbackUrls
        {
            get
            {
                var list = new System.Collections.Generic.List<string>(4);
                if (!string.IsNullOrWhiteSpace(IconUrl))
                {
                    list.Add(IconUrl);
                    if (IconUrl.Contains("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase))
                        list.Add(IconUrl.Replace("steamcdn-a.akamaihd.net", "cdn.cloudflare.steamstatic.com", StringComparison.OrdinalIgnoreCase));
                    else if (IconUrl.Contains("cdn.cloudflare.steamstatic.com", StringComparison.OrdinalIgnoreCase))
                        list.Add(IconUrl.Replace("cdn.cloudflare.steamstatic.com", "media.steampowered.com", StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(IconGrayUrl) && IconGrayUrl != IconUrl)
                    list.Add(IconGrayUrl);

                if (AppId > 0)
                    list.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/capsule_sm_120.jpg");

                return list;
            }
        }

        // ── SteamHunters & Global Rarity Metadata ─────────────────────────────
        public double GlobalPercentage { get; set; }
        public double HuntersPercentage { get; set; }
        public int Points { get; set; }

        /// <summary>
        /// SteamHunters obtainability status: 0 = Obtainable, >0 = Broken / Conditionally Obtainable / Unobtainable.
        /// </summary>
        public int Obtainability { get; set; }
        public bool IsUnobtainable => Obtainability != 0;

        /// <summary>
        /// Effective rarity percentage (0..100) prioritizing SteamHunters localPercentage when available.
        /// </summary>
        public double EffectivePercentage => HuntersPercentage > 0 ? HuntersPercentage : GlobalPercentage;

        public string RarityBadgeText
        {
            get
            {
                if (HuntersPercentage > 0 && Points > 0)
                    return $"{HuntersPercentage:0.0}% SH • {GlobalPercentage:0.0}% Steam • {Points} pts";
                if (HuntersPercentage > 0)
                    return $"{HuntersPercentage:0.0}% SH • {GlobalPercentage:0.0}% Steam";
                if (GlobalPercentage > 0)
                    return $"{GlobalPercentage:0.0}% Steam";
                return string.Empty;
            }
        }

        public string UnobtainableWarningText =>
            IsUnobtainable ? "⚠️ Inobtível / Quebrada (SteamHunters)" : string.Empty;

        // ── Prerequisite & Organic Timeline Data ──────────────────────────────
        public string? PrerequisiteId { get; set; }
        public bool IsPlatinumMilestone { get; set; }

        public string PrerequisiteHint
        {
            get => _prerequisiteHint;
            set
            {
                if (_prerequisiteHint == value) return;
                _prerequisiteHint = value;
                OnPropertyChanged();
            }
        }

        public int SuggestedOrder
        {
            get => _suggestedOrder;
            set
            {
                if (_suggestedOrder == value) return;
                _suggestedOrder = value;
                OnPropertyChanged();
            }
        }

        public TimeSpan? ScheduledOffset
        {
            get => _scheduledOffset;
            set
            {
                if (_scheduledOffset == value) return;
                _scheduledOffset = value;
                OnPropertyChanged();
                UpdateScheduleBadgeText();
            }
        }

        public string ScheduleBadgeText
        {
            get => _scheduleBadgeText;
            set
            {
                if (_scheduleBadgeText == value) return;
                _scheduleBadgeText = value;
                OnPropertyChanged();
            }
        }

        public DateTime? UnlockTime
        {
            get => _unlockTime;
            set
            {
                if (_unlockTime == value) return;
                _unlockTime = value;
                OnPropertyChanged();
            }
        }

        public bool IsAchieved
        {
            get => _isAchieved;
            set
            {
                if (_isAchieved == value) return;
                _isAchieved = value;
                OnPropertyChanged();
            }
        }

        public void UpdateScheduleBadgeText()
        {
            if (IsUnobtainable)
            {
                ScheduleBadgeText = "⚠️ Ignorada (Inobtível)";
                return;
            }

            if (_scheduledOffset == null || _suggestedOrder <= 0)
            {
                ScheduleBadgeText = string.Empty;
                return;
            }

            var ts = _scheduledOffset.Value;
            int totalHours = (int)ts.TotalHours;
            string timeFormatted = totalHours > 0
                ? $"+{totalHours:00}h {ts.Minutes:00}m {ts.Seconds:00}s"
                : $"+{ts.Minutes:00}m {ts.Seconds:00}s";

            ScheduleBadgeText = $"#{_suggestedOrder} • {timeFormatted}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyMetadataChanged()
        {
            OnPropertyChanged(nameof(RarityBadgeText));
            OnPropertyChanged(nameof(IsUnobtainable));
            OnPropertyChanged(nameof(UnobtainableWarningText));
            OnPropertyChanged(nameof(EffectivePercentage));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
