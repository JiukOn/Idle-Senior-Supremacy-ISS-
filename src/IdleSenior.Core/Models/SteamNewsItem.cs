using System;

namespace IdleSenior.Core.Models
{
    public class SteamNewsItem
    {
        public int AppId { get; set; }
        public string GameName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Author { get; set; } = "";
        public string Contents { get; set; } = "";
        public DateTime Date { get; set; } = DateTime.UtcNow;

        public string DateFormatted => Date.ToString("dd/MM/yyyy");
    }
}
