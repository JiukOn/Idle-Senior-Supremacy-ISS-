namespace IdleSenior.Core.Models
{
    /// <summary>
    /// A game in a farming context (card drops / hours idling).
    /// Inherits all shared identity data (AppId, Name, URLs, playtime, release year)
    /// from <see cref="GameModel"/> so both models stay consistent.
    /// </summary>
    public class SteamGame : GameModel
    {
        /// <summary>Card drops still available for this game.</summary>
        public int RemainingCards { get; set; }

        /// <summary>Whether a Worker process is currently idling this game.</summary>
        public bool IsIdling { get; set; }

        /// <summary>Whether this game is blocklisted from automatic card farming.</summary>
        public bool IsBlocked { get; set; }

        /// <summary>Average or lowest card price on the Steam community market.</summary>
        public double AverageCardPrice { get; set; }
        public string CardPriceText { get; set; } = "";

        /// <summary>Alias of <see cref="GameModel.PlaytimeHours"/> kept for farming code readability.</summary>
        public double HoursPlayed
        {
            get => PlaytimeHours;
            set => PlaytimeHours = value;
        }
    }
}
