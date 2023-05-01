namespace VoiceChannelGrabber
{
    public class Config
    {
        public string SceneName { get; set; } = string.Empty;

        public string SourceName { get; set; } = string.Empty;

        public string WebsocketAddress { get; set; } = string.Empty;

        public string WebsocketPassword { get; set; } = string.Empty;

        public string ClientID { get; set; } = string.Empty;

        public string ClientSecret { get; set; } = string.Empty;

        public string RedirectUri { get; set; } = "http://localhost:3000/callback";
    }
}