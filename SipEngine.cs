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
using System.Linq;

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
        public event Action<LogLevel, string>? OnLog;
        public event EventHandler<CallStatusEventArgs>? OnCallStatusChange;

        private SIPTransport? _sipTransport;
        private CancellationTokenSource? _cts;
        private AiCore _aiCore;
        
        public AudisConfig CurrentConfig { get; set; } = new AudisConfig();
        public bool IsGlobalRecordingEnabled { get; set; } = false;

        public string ListenIp { get; set; } = "0.0.0.0";
        public string BaseDir { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        private string AudioDir => Path.Combine(BaseDir, "audio");
        private string RecDir => Path.Combine(BaseDir, "recordings");
        private string VoicemailDir => Path.Combine(BaseDir, "voicemail");

        private static readonly HttpClient _httpClient = new HttpClient();
        private ConcurrentDictionary<string, string> _activeCalls = new();

        public SipEngine()
        {
            _aiCore = new AiCore();
        }

        private void Log(LogLevel level, string msg) => OnLog?.Invoke(level, msg);

        public void Start(AudisConfig config)
        {
            CurrentConfig = config;
            _cts = new CancellationTokenSource();

            Directory.CreateDirectory(AudioDir);
            Directory.CreateDirectory(RecDir);
            Directory.CreateDirectory(VoicemailDir);

            Log(LogLevel.Information, $"Audio Directory: {AudioDir}");
            Log(LogLevel.Information, $"Recordings Directory: {RecDir}");

            Task.Run(async () => 
            {
                try {
                    Log(LogLevel.Information, "Starting SIP Engine...");
                    
                    var sipChannel = new SIPUDPChannel(new IPEndPoint(IPAddress.Parse(ListenIp), CurrentConfig.Port));
                    _sipTransport = new SIPTransport();
                    _sipTransport.AddSIPChannel(sipChannel);
                    _sipTransport.ContactHost = CurrentConfig.PublicIp;

                    var userAgent = new SIPUserAgent(_sipTransport, null);
                    
                    userAgent.OnIncomingCall += (ua, req) => 
                    {
                        var callId = req.Header.CallId;
                        if (_activeCalls.TryAdd(callId, "active"))
                        {
                            Log(LogLevel.Information, $"Incoming Call: {callId}");
                            OnCallStatusChange?.Invoke(this, new CallStatusEventArgs { CallId = callId, Status = "RINGING", IsActive = true });
                            _ = HandleCallAsync(ua, req, callId);
                        }
                    };

                    Log(LogLevel.Information, $"Listening on {ListenIp}:{CurrentConfig.Port}");
                    Log(LogLevel.Information, $"Contact Host (NAT): {CurrentConfig.PublicIp}");
                    
                    while (!_cts.Token.IsCancellationRequested) await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, $"CRITICAL ERROR: {ex.Message}");
                }
            }, _cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            if (_sipTransport != null)
            {
                _sipTransport.Shutdown();
            }
            _activeCalls.Clear();
            Log(LogLevel.Information, "SIP Engine Stopped.");
        }

        private async Task HandleCallAsync(SIPUserAgent ua, SIPRequest request, string callId)
        {
            // CRITICAL: Track actual call timing
            var callStartTime = DateTime.Now;
            Log(LogLevel.Information, $"[TIMING] Call START at {callStartTime:HH:mm:ss.fff}");
            
            var rtpSession = new VoIPMediaSession();
            rtpSession.AcceptRtpFromAny = true;
            
            try {
                if (rtpSession.AudioExtrasSource != null)
                {
                    await rtpSession.AudioExtrasSource.CloseAudio();
                }
            } catch { }
            
            var callState = new CallState();
            
            List<byte> incomingAudioBuffer = new List<byte>();
            List<byte> outgoingAudioBuffer = new List<byte>();
            List<byte> aiInputBuffer = new List<byte>();
            List<byte> voicemailBuffer = new List<byte>();
            
            // Track the audio payload type so we can decode correctly later
            // 0 = PCMU (μ-law), 8 = PCMA (A-law) — default to 0 (most common)
            int audioPayloadType = 0;

            DateTime lastDtmfTime = DateTime.MinValue;
            string lastDtmfKey = "";

            // CRITICAL FIX: Log hangup immediately with accurate timing
            ua.OnCallHungup += (dialogue) => {
                var hangupTime = DateTime.Now;
                var actualDuration = (hangupTime - callStartTime).TotalSeconds;
                
                Log(LogLevel.Information, $"[TIMING] HANGUP DETECTED at {hangupTime:HH:mm:ss.fff}");
                Log(LogLevel.Information, $"[TIMING] Actual call duration: {actualDuration:F2} seconds");
                
                callState.IsActive = false;
                Log(LogLevel.Information, $"Hangup: {callId}");
                OnCallStatusChange?.Invoke(this, new CallStatusEventArgs { CallId = callId, Status = "ENDED", IsActive = false });
            };

            // DTMF HANDLER 1: SIP INFO
            ua.OnDtmfTone += (tone, duration) => 
            {
                string d = tone.ToString().Replace("Tone", "");
                if (d == "Star") d = "*";
                if (d == "Pound") d = "#";
                if (d == "10") d = "*";  // Some phones send 10 for *
                if (d == "11") d = "#";  // Some phones send 11 for #
                
                Log(LogLevel.Information, $"[SIP INFO DTMF] Raw: {tone}, Converted: {d}");
                
                if (d == lastDtmfKey && (DateTime.Now - lastDtmfTime).TotalMilliseconds < 200)
                    return;
                
                lastDtmfKey = d;
                lastDtmfTime = DateTime.Now;
                
                callState.LastDigit = d;
                OnCallStatusChange?.Invoke(this, new CallStatusEventArgs { CallId = callId, Status = "INPUT", LastInput = d, IsActive = true });
                
                if (d == "#" && callState.IsAiRecording) callState.IsAiRecording = false;
            };

            // DTMF HANDLER 2: RTP EVENTS
            int rtpPacketCount = 0;
            int audioPacketCount = 0;
            int dtmfPacketCount = 0;
            
            rtpSession.OnRtpPacketReceived += (ep, type, packet) => 
            {
                rtpPacketCount++;
                int pType = (int)type;
                
                // CRITICAL DEBUG: Log first 20 RTP packets to see what we're getting
                if (rtpPacketCount <= 20)
                {
                    Log(LogLevel.Information, $"[RTP DEBUG] Packet #{rtpPacketCount}: Type={pType}, Size={packet.Payload?.Length ?? 0}");
                }
                
                // RFC 4733 DTMF Detection
                // Check all dynamic payload types (96-127) as different systems use different IDs
                // Common ones: 96, 97, 100, 101, 121
                if (pType >= 96 && pType <= 127 && packet.Payload?.Length >= 4)
                {
                    byte eventId = packet.Payload[0];
                    byte endFlag = packet.Payload[1];
                    bool isEndOfEvent = (endFlag & 0x80) != 0;
                    
                    // Log first 50 DTMF-like packets for debugging
                    dtmfPacketCount++;
                    if (dtmfPacketCount <= 50)
                    {
                        Log(LogLevel.Information, $"[RTP DTMF-LIKE] PayloadType={pType}, EventID={eventId}, EndFlag=0x{endFlag:X2}, IsEnd={isEndOfEvent}, Bytes={BitConverter.ToString(packet.Payload.Take(Math.Min(8, packet.Payload.Length)).ToArray())}");
                    }
                    
                    // Only process on end-of-event to avoid duplicates
                    if (isEndOfEvent)
                    {
                        string? d = ParseDtmf(eventId);
                        if (d != null)
                        {
                            // Debounce: ignore if same key within 200ms
                            if (d == lastDtmfKey && (DateTime.Now - lastDtmfTime).TotalMilliseconds < 200)
                            {
                                Log(LogLevel.Information, $"[RTP DTMF] Debounced duplicate: {d}");
                                return;
                            }
                            
                            lastDtmfKey = d;
                            lastDtmfTime = DateTime.Now;
                            
                            callState.LastDigit = d;
                            OnCallStatusChange?.Invoke(this, new CallStatusEventArgs { CallId = callId, Status = "INPUT", LastInput = d, IsActive = true });
                            Log(LogLevel.Information, $"[RTP DTMF] *** KEY DETECTED: {d} *** (PayloadType={pType}, EventID={eventId})");
                            
                            if (d == "#" && callState.IsAiRecording) callState.IsAiRecording = false;
                        }
                        else
                        {
                            Log(LogLevel.Warning, $"[RTP DTMF] Unknown EventID={eventId} on PayloadType={pType}");
                        }
                    }
                }
                else if (pType >= 96 && pType <= 127 && packet.Payload?.Length < 4)
                {
                    // Packet looks like DTMF payload type but too short
                    if (dtmfPacketCount <= 10)
                    {
                        Log(LogLevel.Warning, $"[RTP DTMF] Short packet on PayloadType={pType}, Size={packet.Payload?.Length ?? 0}");
                    }
                }

                if ((pType == 0 || pType == 1 || pType == 8) && packet.Payload != null && packet.Payload.Length > 0)
                {
                    audioPacketCount++;
                    
                    if (audioPacketCount <= 5)
                    {
                        Log(LogLevel.Information, $"[RTP AUDIO] Audio packet #{audioPacketCount}! Type={pType}, Size={packet.Payload.Length}");
                    }
                    
                    // Track the codec being used so we can decode correctly for Whisper
                    if (audioPacketCount == 1)
                    {
                        audioPayloadType = pType;
                        Log(LogLevel.Information, $"[RTP AUDIO] Codec detected: PayloadType={pType} ({(pType == 8 ? "A-law/PCMA" : pType == 0 ? "μ-law/PCMU" : "PCM")})");
                    }
                    
                    if (IsGlobalRecordingEnabled) lock(incomingAudioBuffer) incomingAudioBuffer.AddRange(packet.Payload);
                    
                    if (callState.IsAiRecording)
                    {
                        lock(aiInputBuffer) aiInputBuffer.AddRange(packet.Payload);
                        int bufSize = aiInputBuffer.Count;
                        if (bufSize % 8000 == 0) // Log every ~1 second of audio
                        {
                            Log(LogLevel.Information, $"[AI RECORD] Buffer: {bufSize} bytes (~{bufSize / 8000.0:F1}s)");
                        }
                    }
                    
                    if (callState.IsVoicemailRecording) lock(voicemailBuffer) voicemailBuffer.AddRange(packet.Payload);
                }
            };

            var uas = ua.AcceptCall(request);
            await ua.Answer(uas, rtpSession);
            
            var answerTime = DateTime.Now;
            Log(LogLevel.Information, $"[TIMING] Call ANSWERED at {answerTime:HH:mm:ss.fff}");
            Log(LogLevel.Information, $"[DTMF] Listening for RFC 4733 DTMF on payload types 96-127");
            Log(LogLevel.Information, $"[DTMF] Also listening for SIP INFO DTMF");
            Log(LogLevel.Information, $"[DTMF] If you don't see DTMF detection, check the RTP DEBUG logs above");
            
            OnCallStatusChange?.Invoke(this, new CallStatusEventArgs { CallId = callId, Status = "CONNECTED", IsActive = true });
            
            Log(LogLevel.Information, $"Call CONNECTED - Starting greeting");

            try
            {
                await PlayFile(rtpSession, "eliska.wav", callState, outgoingAudioBuffer);
                
                string currentFile = "";

                while (callState.IsActive)
                {
                    // CRITICAL FIX: Check IsActive more frequently
                    if (_cts != null && _cts.Token.IsCancellationRequested) break;
                    if (!callState.IsActive) break; // Double check

                    if (!string.IsNullOrEmpty(currentFile))
                    {
                        await PlayFile(rtpSession, currentFile, callState, outgoingAudioBuffer);
                        currentFile = "";
                    }

                    // CRITICAL FIX: Use Task.Delay with CancellationToken instead of tight loop
                    while (callState.IsActive && callState.LastDigit == null)
                    {
                        if (!callState.IsActive) break; // Check before each iteration
                        if (_cts != null && _cts.Token.IsCancellationRequested) break;
                        
                        await SendSilence(rtpSession, outgoingAudioBuffer);
                        
                        // CRITICAL FIX: Check immediately after silence
                        if (!callState.IsActive)
                        {
                            Log(LogLevel.Information, $"[TIMING] Breaking silence loop - hangup detected");
                            break;
                        }
                    }

                    if (!callState.IsActive) break; // Check before processing key

                    if (callState.LastDigit != null)
                    {
                        string d = callState.LastDigit;
                        callState.LastDigit = null;

                        Log(LogLevel.Information, $"Processing key: {d}");

                        if (d == "*") // AI MODE
                        {
                            Log(LogLevel.Information, "AI Mode activated - Using Gemma 1B");
                            Log(LogLevel.Information, $"[AI] Ollama URL: http://localhost:11434/api/generate");
                            Log(LogLevel.Information, $"[AI] Audio codec in use: PayloadType={audioPayloadType} ({(audioPayloadType == 8 ? "A-law/PCMA" : "μ-law/PCMU")})");
                            
                            await PlayTts(rtpSession, "Mluvte po zaznění tónu. Ukončete křížkem.", callState, outgoingAudioBuffer);
                            
                            lock(aiInputBuffer) aiInputBuffer.Clear();
                            callState.IsAiRecording = true;
                            Log(LogLevel.Information, $"[AI] Recording started — speak now, press # to stop");
                            
                            int timeout = 0;
                            while(callState.IsActive && callState.IsAiRecording && timeout < 500)
                            {
                                if (_cts != null && _cts.Token.IsCancellationRequested) break;
                                await SendSilence(rtpSession, outgoingAudioBuffer);
                                timeout++;
                            }
                            callState.IsAiRecording = false;

                            byte[] capturedAudio;
                            lock(aiInputBuffer) capturedAudio = aiInputBuffer.ToArray();
                            
                            Log(LogLevel.Information, $"[AI] Recording stopped. Captured: {capturedAudio.Length} bytes (~{capturedAudio.Length / 8000.0:F1}s), timeout={timeout >= 500}");

                            if (capturedAudio.Length > 1600)
                            {
                                Log(LogLevel.Information, $"[AI] Sending {capturedAudio.Length} bytes to Whisper (codec type={audioPayloadType})...");
                                
                                string userText = await _aiCore.TranscribeAudioAsync(capturedAudio, audioPayloadType);
                                Log(LogLevel.Information, $"[AI] Whisper result: '{userText}'");
                                
                                Log(LogLevel.Information, $"[AI] Sending to Ollama: '{userText}'");
                                string aiResponse = await _aiCore.AskLocalAiAsync(userText);
                                Log(LogLevel.Information, $"[AI] Gemma response: '{aiResponse}'");

                                byte[] responseAudio = await GenerateGoogleTtsBytes(aiResponse);
                                // Clear LastDigit so PlayPcmBytes can play
                                callState.LastDigit = null;
                                await PlayPcmBytes(rtpSession, responseAudio, callState, outgoingAudioBuffer);
                            }
                            else
                            {
                                Log(LogLevel.Warning, $"[AI] Insufficient audio: {capturedAudio.Length} bytes (need >1600). Is the caller sending RTP? Check [RTP AUDIO] logs above.");
                                await PlayTts(rtpSession, "Nezachytil jsem žádný zvuk.", callState, outgoingAudioBuffer);
                            }
                            
                            currentFile = "eliska.wav";
                        }
                        else if (d == "8") // VOICEMAIL
                        {
                            Log(LogLevel.Information, "Voicemail recording");
                            await PlayTts(rtpSession, "Zanechte vzkaz po tónu.", callState, outgoingAudioBuffer);
                            
                            voicemailBuffer.Clear();
                            callState.IsVoicemailRecording = true;
                            
                            int timeout = 0;
                            while(callState.IsActive && callState.LastDigit == null && timeout < 1500)
                            {
                                if (_cts != null && _cts.Token.IsCancellationRequested) break;
                                await SendSilence(rtpSession, outgoingAudioBuffer);
                                timeout++;
                            }
                            
                            callState.IsVoicemailRecording = false;
                            if (voicemailBuffer.Count > 0)
                            {
                                SaveWavFile(Path.Combine(VoicemailDir, $"msg_{DateTime.Now:yyyyMMdd_HHmmss}.wav"), voicemailBuffer.ToArray());
                                Log(LogLevel.Information, "Voicemail saved");
                            }
                            currentFile = "eliska.wav";
                        }
                        else if (d == "7") // INFO PACKAGE
                        {
                            Log(LogLevel.Information, "Info package requested");
                            string info = await GetInfoPackage();
                            byte[] infoAudio = await GenerateGoogleTtsBytes(info);
                            await PlayPcmBytes(rtpSession, infoAudio, callState, outgoingAudioBuffer);
                            currentFile = "eliska.wav";
                        }
                        else if (d == "6") // SYSTEM STATUS
                        {
                            Log(LogLevel.Information, "System status requested");
                            await PlayTts(rtpSession, "Systém funguje správně.", callState, outgoingAudioBuffer);
                            currentFile = "eliska.wav";
                        }
                        else if (CurrentConfig.KeyMappings.ContainsKey(d))
                        {
                            string mappedValue = CurrentConfig.KeyMappings[d];
                            
                            if (!string.IsNullOrEmpty(mappedValue) && !mappedValue.StartsWith("SYSTEM") && !mappedValue.StartsWith("INFO") && !mappedValue.StartsWith("AI") && !mappedValue.StartsWith("VOICEMAIL"))
                            {
                                currentFile = mappedValue;
                                Log(LogLevel.Information, $"Key {d} -> {currentFile}");
                            }
                        }
                    }
                }
                
                var endTime = DateTime.Now;
                var totalDuration = (endTime - callStartTime).TotalSeconds;
                Log(LogLevel.Information, $"[TIMING] Call loop ENDED at {endTime:HH:mm:ss.fff} (Total: {totalDuration:F2}s)");
            }
            catch (Exception ex) 
            { 
                Log(LogLevel.Error, $"Call Error: {ex.Message}");
                Log(LogLevel.Error, $"Stack: {ex.StackTrace}");
            }
            finally
            {
                if (IsGlobalRecordingEnabled && (incomingAudioBuffer.Count > 0 || outgoingAudioBuffer.Count > 0))
                {
                    string fname = Path.Combine(RecDir, $"call_{callId}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                    byte[] mixedAudio = MixAudioBuffers(incomingAudioBuffer.ToArray(), outgoingAudioBuffer.ToArray());
                    SaveWavFile(fname, mixedAudio);
                    Log(LogLevel.Information, $"Recording saved: {fname} ({mixedAudio.Length} bytes)");
                }

                ua.Hangup();
                _activeCalls.TryRemove(callId, out _);
                OnCallStatusChange?.Invoke(this, new CallStatusEventArgs { CallId = callId, IsActive = false, Status = "ENDED" });
                
                var finalTime = DateTime.Now;
                var finalDuration = (finalTime - callStartTime).TotalSeconds;
                Log(LogLevel.Information, $"[TIMING] Call FINALIZED at {finalTime:HH:mm:ss.fff} (Duration: {finalDuration:F2}s)");
            }
        }

        private byte[] MixAudioBuffers(byte[] incoming, byte[] outgoing)
        {
            int maxLength = Math.Max(incoming.Length, outgoing.Length);
            byte[] mixed = new byte[maxLength];
            
            for (int i = 0; i < maxLength; i++)
            {
                byte inSample = i < incoming.Length ? incoming[i] : (byte)0xFF;
                byte outSample = i < outgoing.Length ? outgoing[i] : (byte)0xFF;
                
                short inPcm = MuLawDecoder.MuLawToLinearSample(inSample);
                short outPcm = MuLawDecoder.MuLawToLinearSample(outSample);
                
                int mixedPcm = (inPcm + outPcm) / 2;
                mixedPcm = Math.Clamp(mixedPcm, short.MinValue, short.MaxValue);
                
                mixed[i] = MuLawEncoder.LinearToMuLawSample((short)mixedPcm);
            }
            
            return mixed;
        }

        private async Task PlayFile(VoIPMediaSession session, string filename, CallState state, List<byte> recordBuffer)
        {
            string path = Path.Combine(AudioDir, filename);
            
            if (!File.Exists(path))
            {
                Log(LogLevel.Error, $"File NOT FOUND: {path}");
                return;
            }
            
            Log(LogLevel.Information, $"Playing: {filename}");
            byte[] fileData = await File.ReadAllBytesAsync(path);
            await PlayPcmBytes(session, fileData, state, recordBuffer);
        }

        private async Task PlayTts(VoIPMediaSession session, string text, CallState state, List<byte> recordBuffer)
        {
            byte[] audio = await GenerateGoogleTtsBytes(text);
            if (audio.Length > 0)
            {
                await PlayPcmBytes(session, audio, state, recordBuffer);
            }
        }

        private async Task PlayPcmBytes(VoIPMediaSession session, byte[] pcmData, CallState state, List<byte> recordBuffer)
        {
            if (pcmData == null || pcmData.Length == 0) return;

            int pos = 0;
            
            if (pcmData.Length > 44 && pcmData[0] == 'R' && pcmData[1] == 'I' && pcmData[2] == 'F' && pcmData[3] == 'F')
            {
                pos = 44;
            }

            var sw = Stopwatch.StartNew();
            long packetNumber = 0;
            const int SAMPLES_PER_PACKET = 160;
            const int BYTES_PER_PACKET = 320;
            const long TICKS_PER_PACKET = 200000;
            
            while (pos + BYTES_PER_PACKET <= pcmData.Length && state.IsActive && state.LastDigit == null)
            {
                // CRITICAL: Check BEFORE any processing
                if (_cts != null && _cts.Token.IsCancellationRequested)
                {
                    Log(LogLevel.Information, $"[TIMING] PlayPcmBytes cancelled by token at packet {packetNumber}");
                    break;
                }
                
                if (!state.IsActive)
                {
                    Log(LogLevel.Information, $"[TIMING] PlayPcmBytes stopped - hangup detected at packet {packetNumber}/{(pcmData.Length - pos) / BYTES_PER_PACKET} remaining");
                    break;
                }
                
                byte[] g711 = new byte[SAMPLES_PER_PACKET];
                for (int i = 0; i < BYTES_PER_PACKET; i += 2)
                {
                    short sample = (short)(pcmData[pos + i] | (pcmData[pos + i + 1] << 8));
                    g711[i / 2] = MuLawEncoder.LinearToMuLawSample(sample);
                }
                
                if (IsGlobalRecordingEnabled && recordBuffer != null)
                {
                    lock(recordBuffer)
                    {
                        recordBuffer.AddRange(g711);
                    }
                }
                
                session.SendAudio((uint)SAMPLES_PER_PACKET, g711);
                
                pos += BYTES_PER_PACKET;
                packetNumber++;
                
                long expectedTicks = packetNumber * TICKS_PER_PACKET;
                long actualTicks = sw.ElapsedTicks;
                long ticksToWait = expectedTicks - actualTicks;
                
                if (ticksToWait > 0)
                {
                    int msToWait = (int)(ticksToWait / 10000);
                    if (msToWait > 0)
                    {
                        // CRITICAL: Use shorter delays and check more frequently
                        // Break 20ms delay into 5ms chunks to detect hangup faster
                        int chunksOf5ms = msToWait / 5;
                        int remainder = msToWait % 5;
                        
                        for (int i = 0; i < chunksOf5ms; i++)
                        {
                            await Task.Delay(5);
                            
                            // CRITICAL: Check IsActive after EVERY 5ms delay
                            if (!state.IsActive)
                            {
                                Log(LogLevel.Information, $"[TIMING] PlayPcmBytes interrupted during delay at packet {packetNumber}");
                                return; // Exit immediately
                            }
                        }
                        
                        if (remainder > 0)
                        {
                            await Task.Delay(remainder);
                            if (!state.IsActive)
                            {
                                Log(LogLevel.Information, $"[TIMING] PlayPcmBytes interrupted during final delay at packet {packetNumber}");
                                return;
                            }
                        }
                    }
                }
            }
            
            Log(LogLevel.Information, $"[TIMING] PlayPcmBytes complete - sent {packetNumber} packets");
        }

        private async Task SendSilence(VoIPMediaSession session, List<byte> recordBuffer)
        {
            byte[] silence = new byte[160];
            Array.Fill(silence, (byte)0xFF);
            
            if (IsGlobalRecordingEnabled && recordBuffer != null)
            {
                lock(recordBuffer)
                {
                    recordBuffer.AddRange(silence);
                }
            }
            
            session.SendAudio(160u, silence);
            await Task.Delay(20);
        }

        public async Task<byte[]> GenerateGoogleTtsBytes(string text)
        {
            string mp3File = Path.Combine(BaseDir, "temp_tts.mp3");
            string wavFile = Path.Combine(BaseDir, "temp_tts.wav");
            
            try {
                Log(LogLevel.Information, $"[TTS] Generating speech for: '{text.Substring(0, Math.Min(50, text.Length))}'...");
                
                string url = $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl=cs&q={Uri.EscapeDataString(text)}";
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                
                byte[] mp3Data = await _httpClient.GetByteArrayAsync(url);
                Log(LogLevel.Information, $"[TTS] Downloaded {mp3Data.Length} bytes from Google");
                
                await File.WriteAllBytesAsync(mp3File, mp3Data);
                
                var psi = new ProcessStartInfo {
                    FileName = "ffmpeg",
                    Arguments = $"-y -v error -i \"{mp3File}\" -ar 8000 -ac 1 -acodec pcm_s16le \"{wavFile}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    Log(LogLevel.Information, $"[TTS] ffmpeg exit code: {proc.ExitCode}");
                }
                else
                {
                    Log(LogLevel.Error, "[TTS] Failed to start ffmpeg process!");
                    return Array.Empty<byte>();
                }
                
                if (File.Exists(wavFile))
                {
                    byte[] wavData = await File.ReadAllBytesAsync(wavFile);
                    Log(LogLevel.Information, $"[TTS] Generated {wavData.Length} bytes of WAV audio");
                    
                    try { File.Delete(mp3File); } catch { }
                    try { File.Delete(wavFile); } catch { }
                    return wavData;
                }
                else
                {
                    Log(LogLevel.Error, "[TTS] WAV file was not created by ffmpeg!");
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"[TTS] Failed: {ex.Message}");
                Log(LogLevel.Error, $"[TTS] Exception type: {ex.GetType().Name}");
            }
            
            Log(LogLevel.Warning, "[TTS] Returning empty audio array");
            return Array.Empty<byte>();
        }
        private string? ParseDtmf(byte id)
        {
            if (id >= 0 && id <= 9) return id.ToString();
            if (id == 10) return "*";
            if (id == 11) return "#";
            return null;
        }

        private async Task<string> GetInfoPackage()
        {
            try {
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={CurrentConfig.WeatherLat.ToString(CultureInfo.InvariantCulture)}&longitude={CurrentConfig.WeatherLong.ToString(CultureInfo.InvariantCulture)}&current_weather=true";
                string json = await _httpClient.GetStringAsync(url);
                using JsonDocument doc = JsonDocument.Parse(json);
                double temp = doc.RootElement.GetProperty("current_weather").GetProperty("temperature").GetDouble();
                
                string currentTime = DateTime.Now.ToString("HH:mm");
                return $"Aktuální čas je {currentTime}. V {CurrentConfig.WeatherCity} je {temp} stupňů Celsia.";
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Info error: {ex.Message}");
                return "Informace nejsou dostupné.";
            }
        }

        private void SaveWavFile(string filepath, byte[] mulawData)
        {
            try {
                using var fs = new FileStream(filepath, FileMode.Create);
                using var bw = new BinaryWriter(fs);
                
                byte[] pcmData = new byte[mulawData.Length * 2];
                for(int i = 0; i < mulawData.Length; i++)
                {
                    short sample = MuLawDecoder.MuLawToLinearSample(mulawData[i]);
                    pcmData[i * 2] = (byte)(sample & 0xFF);
                    pcmData[i * 2 + 1] = (byte)(sample >> 8);
                }
                
                bw.Write(new char[] { 'R', 'I', 'F', 'F' });
                bw.Write(36 + pcmData.Length);
                bw.Write(new char[] { 'W', 'A', 'V', 'E' });
                bw.Write(new char[] { 'f', 'm', 't', ' ' });
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)1);
                bw.Write(8000);
                bw.Write(16000);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write(new char[] { 'd', 'a', 't', 'a' });
                bw.Write(pcmData.Length);
                bw.Write(pcmData);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Save WAV error: {ex.Message}");
            }
        }

        private class CallState
        {
            // CRITICAL: volatile ensures changes are immediately visible across threads
            private volatile bool _isActive = true;
            public bool IsActive
            {
                get => _isActive;
                set => _isActive = value;
            }
            
            public string? LastDigit { get; set; }
            public bool IsAiRecording { get; set; }
            public bool IsVoicemailRecording { get; set; }
        }
    }
}
