namespace RadegastWeb.Models
{
    public class AutoRelogSettingsDto
    {
        public bool Enabled { get; set; }
        public int Minutes { get; set; } = 30;
    }
}
