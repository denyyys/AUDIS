using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AudisService
{
    /// <summary>
    /// Stores the chosen icon for a single toolbar button.
    /// DllPath + Index = a specific icon from a specific DLL.
    /// Index == -1 means "use the default Windows file-type icon" (DllPath is ignored).
    /// </summary>
    public class ButtonIconEntry
    {
        public string DllPath { get; set; } = "";
        public int    Index   { get; set; } = -1; // -1 = default
    }

    public class AudisConfig
    {
        public string PublicIp { get; set; } = "192.168.100.64";
        public int Port { get; set; } = 5060;

        // Weather Location Settings
        public string WeatherCity { get; set; } = "Karviná";
        public double WeatherLat  { get; set; } = 49.85;
        public double WeatherLong { get; set; } = 18.54;

        // AI Settings
        public string OllamaModel { get; set; } = "gemma3:1b";

        // Dynamic Key Mapping (Key -> Action/Filename)
        public Dictionary<string, string> KeyMappings { get; set; } = new Dictionary<string, string>();

        // Toolbar button icon overrides — key = button name
        public Dictionary<string, ButtonIconEntry> ButtonIcons { get; set; } = new()
        {
            ["SipClient"]    = new ButtonIconEntry { DllPath = @"C:\Windows\System32\compstui.dll", Index = 83 },
            ["SipSettings"]  = new ButtonIconEntry { DllPath = @"C:\Windows\System32\mmcndmgr.dll", Index = 43 },
            ["ServerConfig"] = new ButtonIconEntry { DllPath = @"C:\Windows\System32\mmcndmgr.dll", Index = 93 },
            ["CallLog"]      = new ButtonIconEntry { DllPath = @"C:\Windows\System32\mmcndmgr.dll", Index = 21 },
            ["Help"]         = new ButtonIconEntry { DllPath = @"C:\Windows\System32\mmcndmgr.dll", Index = 63 },
        };

        public AudisConfig()
        {
            // Default Enterprise Mappings
            KeyMappings["0"]  = "eliska.wav";
            KeyMappings["1"]  = "cibula.wav";
            KeyMappings["2"]  = "sergei.wav";
            KeyMappings["3"]  = "pam.wav";
            KeyMappings["4"]  = "dollar.wav";
            KeyMappings["5"]  = "smack.wav";
            KeyMappings["6"]  = "SYSTEM_STATUS";
            KeyMappings["7"]  = "INFO_PACKAGE";
            KeyMappings["8"]  = "VOICEMAIL";
            KeyMappings["9"]  = "";
            KeyMappings["10"] = "AI_START";
            KeyMappings["11"] = "AI_STOP";
        }

        // ── Persistence ──────────────────────────────────────────────────────────
        private static readonly string _configFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audis_server_config.json");

        private static readonly JsonSerializerOptions _json =
            new JsonSerializerOptions { WriteIndented = true };

        public static AudisConfig Load()
        {
            try
            {
                if (File.Exists(_configFile))
                {
                    var loaded = JsonSerializer.Deserialize<AudisConfig>(
                        File.ReadAllText(_configFile), _json);
                    if (loaded != null)
                    {
                        // Ensure all keys exist for forward-compat with old config files,
                        // using the same hardcoded defaults so existing installs get the
                        // new icons automatically on first run after upgrade.
                        loaded.ButtonIcons.TryAdd("SipClient",    new ButtonIconEntry { DllPath = @"C:\Windows\System32\compstui.dll", Index = 83 });
                        loaded.ButtonIcons.TryAdd("SipSettings",  new ButtonIconEntry { DllPath = @"C:\Windows\System32\mmcndmgr.dll", Index = 43 });
                        loaded.ButtonIcons.TryAdd("ServerConfig", new ButtonIconEntry { DllPath = @"C:\Windows\System32\mmcndmgr.dll", Index = 93 });
                        loaded.ButtonIcons.TryAdd("CallLog",      new ButtonIconEntry { DllPath = @"C:\Windows\System32\mmcndmgr.dll", Index = 21 });
                        loaded.ButtonIcons.TryAdd("Help",         new ButtonIconEntry { DllPath = @"C:\Windows\System32\mmcndmgr.dll", Index = 63 });
                        return loaded;
                    }
                }
            }
            catch { }
            return new AudisConfig();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(_configFile, JsonSerializer.Serialize(this, _json));
            }
            catch { }
        }
    }
}
