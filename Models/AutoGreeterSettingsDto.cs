namespace RadegastWeb.Models
{
    public class AutoGreeterSettingsDto
    {
        public bool Enabled { get; set; }
        public string Message { get; set; } = "Greetings {name}, welcome!";
        public bool ReturnEnabled { get; set; }
        public string ReturnMessage { get; set; } = "Welcome back {name}!";
        public int ReturnTimeHours { get; set; } = 3;
    }
}
