using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudisService
{
    public enum DtmfMethod
    {
        Rfc2833,
        SipInfo
    }

    public enum SipAudioMode
    {
        Standard,   // Behaves exactly like AUDIS (plays eliska.wav + KeyMappings DTMF)
        CustomWav   // Plays a chosen .wav from the audio folder on answer
    }

    public enum SipTransportProtocol
    {
        UDP,
        TCP,
        TLS
    }

    public class SipContact
    {
        public string Name      { get; set; } = "";
        public string Extension { get; set; } = "";
    }

    public class SipClientConfig
    {
        // Network
        public int LocalSipPort  { get; set; } = 5061;
        public int RtpPortStart  { get; set; } = 12000;
        public int RtpPortEnd    { get; set; } = 12100;
        public string PublicIp   { get; set; } = "192.168.100.64";

        // Media
        public DtmfMethod DtmfMethod        { get; set; } = DtmfMethod.Rfc2833;
        public string     PreferredCodec    { get; set; } = "PCMU";

        // SIP Account (optional – for outbound registration)
        public bool                  UseRegistration { get; set; } = false;
        public string                SipServer       { get; set; } = "";
        public string                SipProxy        { get; set; } = "";
        public string                Username        { get; set; } = "";
        public string                Domain          { get; set; } = "";
        public string                Password        { get; set; } = "";
        public string                DisplayName     { get; set; } = "Audis Client";
        public SipTransportProtocol  Transport       { get; set; } = SipTransportProtocol.UDP;

        // Contacts (displayed as dropdown options in SIP Client)
        public List<SipContact> Contacts { get; set; } = new();

        // ── Audio mode ───────────────────────────────────────────────────────────
        // Standard = full AUDIS behaviour (eliska.wav greeting + KeyMappings).
        // CustomWav = play the file named by CustomWav from the audio/ folder.
        public SipAudioMode AudioMode { get; set; } = SipAudioMode.Standard;
        public string       CustomWav { get; set; } = "";

        // ── Recording ────────────────────────────────────────────────────────────
        // When true, every call is mixed and saved to recordings/ as a WAV file.
        // Defaults to true so recording works out-of-the-box without any setup.
        public bool RecordCalls { get; set; } = true;

        // ── persistence ─────────────────────────────────────────────────────────
        private static readonly string _configFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sip_client_config.json");

        private static readonly JsonSerializerOptions _json =
            new JsonSerializerOptions { WriteIndented = true,
                                        Converters = { new JsonStringEnumConverter() } };

        public static SipClientConfig Load()
        {
            try
            {
                if (File.Exists(_configFile))
                    return JsonSerializer.Deserialize<SipClientConfig>(
                        File.ReadAllText(_configFile), _json) ?? new SipClientConfig();
            }
            catch { }
            return new SipClientConfig();
        }

        public void Save()
        {
            try { File.WriteAllText(_configFile, JsonSerializer.Serialize(this, _json)); }
            catch { }
        }
    }
}
