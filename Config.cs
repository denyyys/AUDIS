using System.Collections.Generic;

namespace AudisService
{
    public class AudisConfig
    {
        public string PublicIp { get; set; } = "192.168.100.64";
        public int Port { get; set; } = 5060;
        
        // Weather Location Settings
        public string WeatherCity { get; set; } = "Karviná";
        public double WeatherLat { get; set; } = 49.85;
        public double WeatherLong { get; set; } = 18.54;

        // AI Settings
        public string OllamaModel { get; set; } = "gemma3:1b";

        // Dynamic Key Mapping (Key -> Action/Filename)
        public Dictionary<string, string> KeyMappings { get; set; } = new Dictionary<string, string>();

        public AudisConfig()
        {
            // Default Enterprise Mappings
            KeyMappings["0"] = "eliska.wav";
            KeyMappings["1"] = "cibula.wav";
            KeyMappings["2"] = "sergei.wav";
            KeyMappings["3"] = "pam.wav";
            KeyMappings["4"] = "dollar.wav";
            KeyMappings["5"] = "smack.wav";
            KeyMappings["6"] = "SYSTEM_STATUS";
            KeyMappings["7"] = "INFO_PACKAGE";
            KeyMappings["8"] = "VOICEMAIL";
            KeyMappings["9"] = "";
            
            // AI Controls
            KeyMappings["10"] = "AI_START";
            KeyMappings["11"] = "AI_STOP";
        }
    }
}
