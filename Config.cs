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

        // Dynamic Key Mapping (Key -> Filename)
        public Dictionary<string, string> KeyMappings { get; set; } = new Dictionary<string, string>();

        public AudisConfig()
        {
            // Default Factory Defaults
            KeyMappings["1"] = "cibula.wav";
            KeyMappings["2"] = "sergei.wav";
            KeyMappings["3"] = "pam.wav";
            KeyMappings["4"] = "dollar.wav";
            KeyMappings["5"] = "smack.wav";
            KeyMappings["6"] = "";
            KeyMappings["7"] = "";
            KeyMappings["8"] = "";
            KeyMappings["9"] = "";
            KeyMappings["0"] = "eliska.wav";
            KeyMappings["*"] = "";
            KeyMappings["#"] = "INFO_PACKAGE"; // The Mega-Combo Key
        }
    }
}