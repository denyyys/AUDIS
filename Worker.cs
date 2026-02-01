using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json; // Needed for Weather

namespace AudisService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private SIPTransport? _sipTransport;
    
    private const string LISTEN_IP = "0.0.0.0";        
    private const string PUBLIC_IP = "192.168.100.64"; 
    private const int SIP_PORT = 5060;
    private const string BASE_DIR = @"C:\Scripts\audis";
    private string AUDIO_DIR => Path.Combine(BASE_DIR, "audio");

    private static ConcurrentDictionary<string, string> _activeCalls = new();
    private static readonly HttpClient _httpClient = new HttpClient(); // For Google/Weather

    public Worker(ILogger<Worker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AUDIS STARTING...");
        Directory.CreateDirectory(AUDIO_DIR);

        var sipChannel = new SIPUDPChannel(new IPEndPoint(IPAddress.Parse(LISTEN_IP), SIP_PORT));
        _sipTransport = new SIPTransport();
        _sipTransport.AddSIPChannel(sipChannel);
        _sipTransport.ContactHost = PUBLIC_IP; 

        var userAgent = new SIPUserAgent(_sipTransport, null); 
        userAgent.OnIncomingCall += OnIncomingCall;

        _logger.LogInformation($"AUDIS ONLINE. {PUBLIC_IP}:{SIP_PORT}");

        while (!stoppingToken.IsCancellationRequested) await Task.Delay(1000, stoppingToken);
        _sipTransport.Shutdown();
    }

    private async void OnIncomingCall(SIPUserAgent ua, SIPRequest request)
    {
        var callId = request.Header.CallId;
        if (!_activeCalls.TryAdd(callId, "active")) return;
        _logger.LogInformation($"--- INCOMING CALL: {callId} ---");
        _ = Task.Run(async () => await HandleCallAsync(ua, request, callId));
    }

    private async Task HandleCallAsync(SIPUserAgent ua, SIPRequest request, string callId)
    {
        var rtpSession = new VoIPMediaSession();
        rtpSession.AcceptRtpFromAny = true; 
        if (rtpSession.AudioExtrasSource != null) await rtpSession.AudioExtrasSource.CloseAudio();
        
        bool callActive = true;
        ua.OnCallHungup += (dialogue) => { _logger.LogInformation("Call hung up."); callActive = false; };
        
        var uas = ua.AcceptCall(request);
        await ua.Answer(uas, rtpSession);

        try
        {
            string? lastDigit = null;

            // --- THE WORKING DTMF LOGIC ---
            ua.OnDtmfTone += (tone, duration) => 
            {
                lastDigit = tone.ToString().Replace("Tone", "");
                _logger.LogInformation($"[UA EVENT] Key: {lastDigit}");
            };

            rtpSession.OnRtpPacketReceived += (ep, type, packet) => 
            {
                int pType = (int)type;
                if (pType == 1 || pType == 101 || pType >= 96)
                {
                    if (packet.Payload != null && packet.Payload.Length > 0)
                    {
                        string? d = ParseDtmf(packet.Payload[0]);
                        if (d != null) {
                            lastDigit = d;
                            _logger.LogInformation($"[RTP EVENT] Key: {d}");
                        }
                    }
                }
            };
            
            // 1. Play Intro
            await PlayFile(rtpSession, "eliska.wav", () => lastDigit != null || !callActive);

            _logger.LogInformation("Intro ended. Waiting for input loop...");
            
            while (callActive)
            {
                if (lastDigit != null)
                {
                    string d = lastDigit;
                    lastDigit = null; 

                    if (d == "7")
                    {
                         // --- WEATHER MODULE ---
                        _logger.LogInformation("Key 7: Fetching Weather...");
                        
                        string weatherText = await GetWeatherTextAsync();
                        _logger.LogInformation($"Speaking: {weatherText}");

                        // Generate Bytes
                        byte[] ttsBytes = await GenerateGoogleTtsBytes(weatherText);

                        if (ttsBytes.Length > 0)
                        {
                            // 1. Speak the Weather
                            await PlayPcmBytes(rtpSession, ttsBytes, () => lastDigit != null || !callActive);
                        }

                        // 2. WAIT 3 SECONDS (Send Silence)
                        // We use the same 'PlayPcmBytes' function to ensure the timing stays perfect for the Linksys.
                        // 3 seconds * 8000Hz * 2 bytes = 48000 bytes of zeros.
                        byte[] silence3s = new byte[48000]; 
                        await PlayPcmBytes(rtpSession, silence3s, () => lastDigit != null || !callActive);

                        
                        // 3. Replay menu
                        await PlayFile(rtpSession, "eliska.wav", () => lastDigit != null || !callActive);
                    }
                    else 
                    {
                        string file = d switch {
                            "1" => "cibula.wav",
                            "2" => "sergei.wav",
                            "3" => "pam.wav",
                            "4" => "dollar.wav",
                            "5" => "smack.wav",
                            "0" => "eliska.wav",
                            _ => ""
                        };

                        if (!string.IsNullOrEmpty(file))
                        {
                            _logger.LogInformation($"Playing: {file}");
                            await PlayFile(rtpSession, file, () => lastDigit != null || !callActive);
                        }
                    }
                }

                await SendSilence(rtpSession);
                if (rtpSession.IsClosed) break;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Call Error"); }
        finally
        {
            _logger.LogInformation($"Cleanup: {callId}");
            ua.Hangup();
            _activeCalls.TryRemove(callId, out _);
        }
    }

    // --- SHARED STABILIZER (The Secret Sauce) ---
    private async Task PlayPcmBytes(VoIPMediaSession session, byte[] pcmBytes, Func<bool> checkInterrupt)
    {
        int pos = 0;
        // Skip WAV Header if present
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
                g711[i/2] = SIPSorcery.Media.MuLawEncoder.LinearToMuLawSample(sample);
            }

            session.SendAudio(160u, g711);
            pos += 320;

            // THE WORKING TIMING LOOP
            while (sw.ElapsedMilliseconds < nextTickMs)
            {
                await Task.Delay(1); 
            }
            nextTickMs += 20;
        }
    }

    private async Task PlayFile(VoIPMediaSession session, string filename, Func<bool> checkInterrupt)
    {
        var path = Path.Combine(AUDIO_DIR, filename);
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

    // --- WEATHER & GOOGLE TTS ---
    private async Task<string> GetWeatherTextAsync()
    {
        try
        {
            string url = "https://api.open-meteo.com/v1/forecast?latitude=49.85&longitude=18.54&current_weather=true";
            string json = await _httpClient.GetStringAsync(url);
            using JsonDocument doc = JsonDocument.Parse(json);
            var current = doc.RootElement.GetProperty("current_weather");
            double temp = current.GetProperty("temperature").GetDouble();
            int code = current.GetProperty("weathercode").GetInt32();

            string condition = code switch
            {
                0 => "je jasno",
                1 => "je skoro jasno",
                2 => "je polojasno",
                3 => "je zataženo",
                45 or 48 => "je mlhavo",
                51 or 53 or 55 => "mrholí", 
                61 or 63 or 65 => "prší",   
                71 or 73 or 75 => "sněží",
                95 or 96 or 99 => "je bouřka",
                _ => "je oblačno"
            };
            return $"V Karviné je {temp} stupňů a {condition}.";
        }
        catch { return "Chyba připojení."; }
    }

    private async Task<byte[]> GenerateGoogleTtsBytes(string text)
    {
        string mp3File = Path.Combine(BASE_DIR, "temp.mp3");
        string wavFile = Path.Combine(BASE_DIR, "temp.wav");
        try {
            string url = $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl=cs&q={Uri.EscapeDataString(text)}";
            await File.WriteAllBytesAsync(mp3File, await _httpClient.GetByteArrayAsync(url));

            var psi = new ProcessStartInfo {
                FileName = "ffmpeg",
                Arguments = $"-y -v 0 -i \"{mp3File}\" -ar 8000 -ac 1 -acodec pcm_s16le \"{wavFile}\"",
                CreateNoWindow = true, UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            await proc!.WaitForExitAsync();

            if (File.Exists(wavFile)) return await File.ReadAllBytesAsync(wavFile);
        }
        catch (Exception ex) { _logger.LogError($"TTS Error: {ex.Message}"); }
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