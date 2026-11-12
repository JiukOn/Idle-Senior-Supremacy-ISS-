namespace IdleSenior.Core.Models
{
    public class AppSettings
    {
        // --- Steam identity (non-secret) ---
        public string ProfileUrl  { get; set; } = "";
        public string SteamId64   { get; set; } = "";

        // --- Sensitive credentials (encrypted on disk via DPAPI) ---
        public string SteamApiKey        { get; set; } = "";
        public string SessionId          { get; set; } = "";
        public string SteamLoginSecure   { get; set; } = "";
        public string SteamParental      { get; set; } = "";

        // --- App statistics ---
        public uint TotalCardsIdled   { get; set; } = 0;
        public uint TotalMinutesIdled { get; set; } = 0;

        // --- App preferences ---
        public string AppTheme  { get; set; } = "Dark";
        public string Language  { get; set; } = "pt-BR";
        public string Currency  { get; set; } = "BRL"; // "BRL" | "USD" | "EUR" | "GBP"
        public int GamesPerPage { get; set; } = 50;

        // --- Persisted farm queue ---
        public List<int> HoursFarmApps { get; set; } = new();

        // --- Hours farm preferences ---
        public bool HoursFarmAutoStart { get; set; } = true;
        public string HoursFarmMode { get; set; } = "Simultaneous"; // "Simultaneous" | "Sequential"
        public Dictionary<int, double> HoursFarmTargets { get; set; } = new();

        // --- Card farm preferences ---
        public string CardFarmMode { get; set; } = "Simultaneous"; // "Simultaneous" | "Sequential"
        public double CardFarmMinHours { get; set; } = 0.0;
        public List<int> CardFarmBlacklist { get; set; } = new();
        public string CardFarmSortBy { get; set; } = "Cards"; // "Cards" | "Price" | "Name" | "Year"
    }
}
