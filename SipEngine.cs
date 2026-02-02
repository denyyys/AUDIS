using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AudisService
{
    public class CallStatusEventArgs : EventArgs
    {
        public string CallId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string LastInput { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    public class SipEngine
    {
        private readonly ILogger _logger;
        private SIPTransport? _sipTransport;
        private SIPUDPChannel? _sipChannel;
        private CancellationTokenSource? _cts;
        
        public AudisConfig CurrentConfig { get; set; } = new AudisConfig();

        public string ListenIp { get; set; } = "0.0.0.0";
        public string BaseDir { get; set; } = @"C:\Scripts\AudisService";
        private string AudioDir => Path.Combine(BaseDir, "audio");

        private ConcurrentDictionary<string, string> _activeCalls = new();
        private static readonly HttpClient _httpClient = new HttpClient();

        public event EventHandler<CallStatusEventArgs>? CallStateChanged;
        public event Action<string>? SipTrafficReceived;

        public SipEngine(ILogger logger)
        {
            _logger = logger;
        }

        public void Start()
        {
            if (_sipTransport != null) return;

            try
            {
                _logger.LogInformation("INITIALIZING SERVICES...");
                
                if (!Directory.Exists(AudioDir))
                {
                    Directory.CreateDirectory(AudioDir);
                }

                _logger.LogInformation($"BINDING UDP {ListenIp}:{CurrentConfig.Port}...");
                _sipChannel = new SIPUDPChannel(new IPEndPoint(IPAddress.Parse(ListenIp), CurrentConfig.Port));
                _sipTransport = new SIPTransport();
                _sipTransport.AddSIPChannel(_sipChannel);

                // FIX: Intelligent Contact Handling
                // If the user leaves IP as default or 0.0.0.0, we try not to force a specific ContactHost
                // which allows SIPSorcery/OS to use the interface IP.
                if (!string.IsNullOrWhiteSpace(CurrentConfig.PublicIp) && CurrentConfig.PublicIp != "0.0.0.0")
                {
                    _sipTransport.ContactHost = CurrentConfig.PublicIp;
                    _logger.LogInformation($"SIP Contact Address set to: {CurrentConfig.PublicIp}");
                }
                else
                {
                    _logger.LogWarning("Public IP not configured. Using default interface IP for SIP Contact.");
                }

                _sipTransport.SIPTransportRequestReceived += (local, remote, req) => 
                {
                    SipTrafficReceived?.Invoke($"[REQ] {remote} >>>\r\n{req.ToString()}");
                    return Task.CompletedTask;
                };
                
                _sipTransport.SIPTransportResponseReceived += (local, remote, resp) => 
                {
                    SipTrafficReceived?.Invoke($"[RES] {remote} <<<\r\n{resp.ToString()}");
                    return Task.CompletedTask;
                };

                var userAgent = new SIPUserAgent(_sipTransport, null);
                userAgent.OnIncomingCall += OnIncomingCall;

                _cts = new CancellationTokenSource();
                
                _logger.LogInformation($"AUDIS ENGINE v1.1 STARTED.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"STARTUP FATAL: {ex.Message}");
                Stop();
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _sipTransport?.Shutdown();
            _sipTransport = null;
            _activeCalls.Clear();
            _logger.LogInformation("AUDIS ENGINE STOPPED.");
        }

        private async void OnIncomingCall(SIPUserAgent ua, SIPRequest request)
        {
            var callId = request.Header.CallId?.Trim() ?? Guid.NewGuid().ToString();
            if (!_activeCalls.TryAdd(callId, "active")) return;

            _logger.LogInformation($"INCOMING: {request.Header.From.FromURI.User} ({callId})");
            NotifyCallUpdate(callId, "Connecting", "", true);

            _ = Task.Run(async () => await HandleCallAsync(ua, request, callId));
        }

        private async Task HandleCallAsync(SIPUserAgent ua, SIPRequest request, string callId)
        {
            var rtpSession = new VoIPMediaSession();
            rtpSession.AcceptRtpFromAny = true;
            if (rtpSession.AudioExtrasSource != null) await rtpSession.AudioExtrasSource.CloseAudio();

            bool callActive = true;
            string currentDtmf = "-";

            // Event listener for hangup
            ua.OnCallHungup += (dialogue) => 
            { 
                _logger.LogInformation($"[SIP] Hangup Event received for {callId}");
                callActive = false; 
            };

            var uas = ua.AcceptCall(request);
            await ua.Answer(uas, rtpSession);

            NotifyCallUpdate(callId, "Connected", "-", true);

            try
            {
                string? lastDigit = null;
                string lastRtpKey = "";
                DateTime lastRtpTime = DateTime.MinValue;

                ua.OnDtmfTone += (tone, duration) =>
                {
                    string t = tone.ToString().Replace("Tone", "");
                    if (t == "10" || t == "Star") t = "*";
                    else if (t == "11" || t == "Pound") t = "#";
                    lastDigit = t;
                    currentDtmf = t;
                    NotifyCallUpdate(callId, "Active", t, true);
                };

                rtpSession.OnRtpPacketReceived += (ep, type, packet) =>
                {
                    int pType = (int)type;
                    if (pType == 1 || pType == 101 || pType >= 96)
                    {
                        if (packet.Payload != null && packet.Payload.Length > 0)
                        {
                            string? d = ParseDtmf(packet.Payload[0]);
                            if (d != null)
                            {
                                if (d == lastRtpKey && (DateTime.Now - lastRtpTime).TotalMilliseconds < 300) return;
                                lastRtpKey = d;
                                lastRtpTime = DateTime.Now;
                                lastDigit = d;
                                currentDtmf = d;
                                NotifyCallUpdate(callId, "Active", d, true);
                            }
                        }
                    }
                };

                await PlayFile(rtpSession, "eliska.wav", () => lastDigit != null || !callActive);

                // FIX: Watchdog loop that polls status
                while (callActive && !_cts!.IsCancellationRequested)
                {
                    // Watchdog: If the SIP stack thinks the call is dead, break immediately
                    // This handles cases where OnCallHungup doesn't fire cleanly
                    if (!ua.IsCallActive)
                    {
                        _logger.LogWarning($"[WATCHDOG] Stack reports call {callId} inactive. Terminating.");
                        callActive = false;
                        break;
                    }

                    if (lastDigit != null)
                    {
                        string d = lastDigit;
                        lastDigit = null;

                        string action = "";
                        if (CurrentConfig.KeyMappings.ContainsKey(d)) action = CurrentConfig.KeyMappings[d];

                        if (action == "INFO_PACKAGE")
                        {
                            NotifyCallUpdate(callId, "Fetching Info...", d, true);
                            string textToSpeak = await GetWeatherTextAsync() + " " + await GetNamedayAsync() + " " + GetTimeDateString();
                            byte[] ttsBytes = await GenerateGoogleTtsBytes(textToSpeak);
                            if (ttsBytes.Length > 0)
                            {
                                NotifyCallUpdate(callId, "Speaking Info", d, true);
                                await PlayPcmBytes(rtpSession, ttsBytes, () => lastDigit != null || !callActive);
                            }
                            await PlayPcmBytes(rtpSession, new byte[48000], () => lastDigit != null || !callActive);
                            await PlayFile(rtpSession, "eliska.wav", () => lastDigit != null || !callActive);
                        }
                        else if (action == "SYSTEM_STATUS")
                        {
                            NotifyCallUpdate(callId, "System Diag...", d, true);
                            string sysStatus = GetSystemStatus();
                            byte[] ttsBytes = await GenerateGoogleTtsBytes(sysStatus);
                            if (ttsBytes.Length > 0) await PlayPcmBytes(rtpSession, ttsBytes, () => lastDigit != null || !callActive);
                        }
                        else if (!string.IsNullOrEmpty(action))
                        {
                            NotifyCallUpdate(callId, $"Playing {action}", d, true);
                            await PlayFile(rtpSession, action, () => lastDigit != null || !callActive);
                        }
                    }

                    await SendSilence(rtpSession);
                    if (rtpSession.IsClosed) break;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Call Error"); }
            finally
            {
                NotifyCallUpdate(callId, "Disconnected", currentDtmf, false);
                try { if(ua.IsCallActive) ua.Hangup(); } catch {}
                _activeCalls.TryRemove(callId, out _);
            }
        }

        // --- Helpers ---

        public async Task<string> GetNamedayAsync()
        {
            try {
                string json = await _httpClient.GetStringAsync("https://svatky.adresa.info/json");
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.GetArrayLength() > 0) return $"Dnes má svátek {doc.RootElement[0].GetProperty("name").GetString()}.";
            } catch { }
            return "";
        }

        public string GetTimeDateString()
        {
            var cz = new CultureInfo("cs-CZ");
            var now = DateTime.Now;
            return $"Dnes je {now.ToString("dddd d. MMMM", cz)}. Je {now.Hour} hodin a {now.Minute} minut.";
        }

        public async Task<string> GetWeatherTextAsync()
        {
            try {
                string lat = CurrentConfig.WeatherLat.ToString(CultureInfo.InvariantCulture);
                string lon = CurrentConfig.WeatherLong.ToString(CultureInfo.InvariantCulture);
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true";
                string json = await _httpClient.GetStringAsync(url);
                using JsonDocument doc = JsonDocument.Parse(json);
                var current = doc.RootElement.GetProperty("current_weather");
                double temp = current.GetProperty("temperature").GetDouble();
                int code = current.GetProperty("weathercode").GetInt32();
                return $"V městě {CurrentConfig.WeatherCity} je {temp} stupňů.";
            } catch { return "Chyba počasí."; }
        }

        public string GetSystemStatus()
        {
            try {
                using var proc = Process.GetCurrentProcess();
                long mem = proc.WorkingSet64 / 1024 / 1024;
                TimeSpan up = DateTime.Now - proc.StartTime;
                return $"Systém běží {up.Hours} hodin. Využití paměti je {mem} megabajtů.";
            } catch { return "Chyba diagnostiky."; }
        }

        private void NotifyCallUpdate(string id, string status, string input, bool active)
        {
            CallStateChanged?.Invoke(this, new CallStatusEventArgs 
            { CallId = id, Status = status, LastInput = input, IsActive = active });
        }

        private async Task PlayPcmBytes(VoIPMediaSession session, byte[] pcmBytes, Func<bool> checkInterrupt)
        {
            if (pcmBytes.Length == 0) return;
            int pos = 0;
            if (pcmBytes.Length > 44 && pcmBytes[0] == 'R' && pcmBytes[2] == 'F') pos = 44;
            Stopwatch sw = Stopwatch.StartNew();
            long nextTickMs = 20;

            while (pos + 320 < pcmBytes.Length)
            {
                if (checkInterrupt()) return;
                byte[] g711 = new byte[160];
                for (int i = 0; i < 320; i += 2)
                {
                    short sample = (short)(pcmBytes[pos + i] | (pcmBytes[pos + i + 1] << 8));
                    g711[i / 2] = MuLawEncoder.LinearToMuLawSample(sample);
                }
                session.SendAudio(160u, g711);
                pos += 320;
                while (sw.ElapsedMilliseconds < nextTickMs) await Task.Delay(1);
                nextTickMs += 20;
            }
        }

        private async Task PlayFile(VoIPMediaSession session, string filename, Func<bool> checkInterrupt)
        {
            var path = Path.Combine(AudioDir, filename);
            if (!File.Exists(path)) return;
            byte[] pcmBytes = await File.ReadAllBytesAsync(path);
            await PlayPcmBytes(session, pcmBytes, checkInterrupt);
        }

        private async Task SendSilence(VoIPMediaSession session)
        {
            byte[] silence = new byte[160];
            Array.Fill(silence, (byte)0xFF);
            session.SendAudio(160u, silence);
            await Task.Delay(20);
        }

        public async Task<byte[]> GenerateGoogleTtsBytes(string text)
        {
            string mp3File = Path.Combine(BaseDir, "temp.mp3");
            string wavFile = Path.Combine(BaseDir, "temp.wav");
            try {
                string url = $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl=cs&q={Uri.EscapeDataString(text)}";
                await File.WriteAllBytesAsync(mp3File, await _httpClient.GetByteArrayAsync(url));
                var psi = new ProcessStartInfo {
                    FileName = "ffmpeg", Arguments = $"-y -v 0 -i \"{mp3File}\" -ar 8000 -ac 1 -acodec pcm_s16le \"{wavFile}\"",
                    CreateNoWindow = true, UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync();
                if (File.Exists(wavFile)) return await File.ReadAllBytesAsync(wavFile);
            } catch { _logger.LogError("TTS Failed"); }
            return Array.Empty<byte>();
        }

        private string? ParseDtmf(byte id)
        {
            if (id >= 0 && id <= 9) return id.ToString();
            if (id == 10) return "*";
            if (id == 11) return "#";
            return null;
        }
    }
}