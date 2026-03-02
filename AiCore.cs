using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SIPSorcery.Media;
using Whisper.net;
using Whisper.net.Ggml;

namespace AudisService
{
    public class AiCore
    {
        private readonly HttpClient _http;
        private const string OLLAMA_URL = "http://localhost:11434/api/generate";
        
        // Whisper model path
        private static readonly string WhisperModelPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ggml-base.bin"
        );
        
        // Lazy initialization - only create when first needed
        private static WhisperFactory? _whisperFactory = null;
        private static readonly object _whisperLock = new object();
        
        // System Prompt - SHORT answers
        private const string SYSTEM_PROMPT = 
            "Jmenuješ se AUDIS, informační systém organizace Kybl Enterprise. " +
            "Odpovídej JEDINOU KRÁTKOU větou. Maximálně 8 slov. Bez úvodu.";

        public AiCore()
        {
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(60);
            Console.WriteLine("[AI] AiCore initialized");
        }

        public async Task<string> AskLocalAiAsync(string userText)
        {
            try
            {
                Console.WriteLine($"[OLLAMA] User: '{userText}'");
                
                var requestObj = new
                {
                    model = "gemma3:1b", 
                    prompt = $"{SYSTEM_PROMPT}\n\nUživatel: {userText}\n\nAudis:",
                    stream = false,
                    options = new {
                        num_predict = 40,
                        temperature = 0.7
                    }
                };

                string jsonReq = JsonSerializer.Serialize(requestObj);
                var content = new StringContent(jsonReq, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(OLLAMA_URL, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    return "Chyba AI.";
                }

                string jsonRes = await response.Content.ReadAsStringAsync();
                using JsonDocument doc = JsonDocument.Parse(jsonRes);
                string text = doc.RootElement.GetProperty("response").GetString() ?? "";
                
                text = text.Replace("\n", " ").Trim();
                Console.WriteLine($"[OLLAMA] Response: '{text}'");
                
                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OLLAMA] Error: {ex.Message}");
                return "AI není dostupná.";
            }
        }

        /// <summary>
        /// Decode A-law (PCMA, payload type 8) to 16-bit PCM
        /// </summary>
        public static byte[] DecodeAlawToPcm(byte[] alawData)
        {
            byte[] pcm = new byte[alawData.Length * 2];
            int pcmIndex = 0;
            for (int i = 0; i < alawData.Length; i++)
            {
                short sample = ALawDecoder.ALawToLinearSample(alawData[i]);
                pcm[pcmIndex++] = (byte)(sample & 0xFF);
                pcm[pcmIndex++] = (byte)(sample >> 8);
            }
            return pcm;
        }

        /// <summary>
        /// Decode μ-law (PCMU, payload type 0) to 16-bit PCM
        /// </summary>
        public static byte[] DecodeMulawToPcm(byte[] mulawData)
        {
            byte[] pcm = new byte[mulawData.Length * 2];
            int pcmIndex = 0;
            for (int i = 0; i < mulawData.Length; i++)
            {
                short sample = MuLawDecoder.MuLawToLinearSample(mulawData[i]);
                pcm[pcmIndex++] = (byte)(sample & 0xFF);
                pcm[pcmIndex++] = (byte)(sample >> 8);
            }
            return pcm;
        }

        private void EnsureWhisperModel()
        {
            if (File.Exists(WhisperModelPath))
            {
                Console.WriteLine($"[WHISPER] Model found: {WhisperModelPath}");
                return;
            }
            
            Console.WriteLine($"[WHISPER] Model NOT found at: {WhisperModelPath}");
            Console.WriteLine($"[WHISPER] Please download ggml-base.bin from:");
            Console.WriteLine($"[WHISPER] https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin");
            Console.WriteLine($"[WHISPER] Place it in: {AppDomain.CurrentDomain.BaseDirectory}");
        }

        /// <param name="audioData">Raw G.711 bytes captured from RTP stream</param>
        /// <param name="payloadType">RTP payload type: 0=PCMU(μ-law), 8=PCMA(A-law)</param>
        public async Task<string> TranscribeAudioAsync(byte[] audioData, int payloadType = 0)
        {
            Console.WriteLine($"[WHISPER] ====== TRANSCRIBE START ======");
            Console.WriteLine($"[WHISPER] Input: {audioData.Length} bytes, PayloadType={payloadType} ({(payloadType == 8 ? "A-law/PCMA" : payloadType == 0 ? "μ-law/PCMU" : "unknown")})");
            Console.WriteLine($"[WHISPER] Duration estimate: ~{audioData.Length / 8000.0:F1}s at 8kHz");

            // Show first 16 bytes to verify data is not silence/garbage
            if (audioData.Length >= 16)
            {
                string hexDump = BitConverter.ToString(audioData, 0, 16);
                Console.WriteLine($"[WHISPER] First 16 bytes: {hexDump}");
                
                // Check if buffer is all silence (μ-law silence = 0xFF, A-law silence = 0xD5)
                byte silenceByte = (payloadType == 8) ? (byte)0xD5 : (byte)0xFF;
                int silenceCount = audioData.Take(100).Count(b => b == silenceByte);
                Console.WriteLine($"[WHISPER] Silence check: {silenceCount}/100 bytes are silence value (0x{silenceByte:X2})");
                if (silenceCount > 90)
                {
                    Console.WriteLine($"[WHISPER] WARNING: Buffer appears to be mostly silence! Audio may not be arriving.");
                }
            }
            
            try
            {
                // Check if model exists
                EnsureWhisperModel();
                
                if (!File.Exists(WhisperModelPath))
                {
                    Console.WriteLine($"[WHISPER] Model missing, using fallback");
                    return "Kdo jsi?";
                }
                
                // Convert G.711 to PCM WAV using the correct codec
                string wavFile = Path.Combine(Path.GetTempPath(), $"audis_{Guid.NewGuid()}.wav");
                Console.WriteLine($"[WHISPER] Converting G.711 (type={payloadType}) to WAV: {wavFile}");
                SaveAsWav(wavFile, audioData, payloadType);
                
                long wavSize = new FileInfo(wavFile).Length;
                Console.WriteLine($"[WHISPER] WAV created: {wavSize} bytes (expected ~{audioData.Length * 2 + 44})");
                
                if (wavSize < 1000)
                {
                    Console.WriteLine($"[WHISPER] ERROR: WAV file suspiciously small ({wavSize} bytes)!");
                }
                
                // Lazy init Whisper (thread-safe)
                if (_whisperFactory == null)
                {
                    lock (_whisperLock)
                    {
                        if (_whisperFactory == null)
                        {
                            Console.WriteLine($"[WHISPER] Loading model from: {WhisperModelPath}");
                            _whisperFactory = WhisperFactory.FromPath(WhisperModelPath);
                            Console.WriteLine($"[WHISPER] Model loaded OK!");
                        }
                    }
                }
                
                // Create processor
                Console.WriteLine($"[WHISPER] Building processor (lang=cs, threads=4)...");
                using var processor = _whisperFactory.CreateBuilder()
                    .WithLanguage("cs")
                    .WithThreads(4)
                    .Build();
                
                Console.WriteLine($"[WHISPER] Starting transcription...");
                var transcribeStart = DateTime.Now;
                
                string transcription = "";
                int segmentCount = 0;
                
                using var fileStream = File.OpenRead(wavFile);
                await foreach (var segment in processor.ProcessAsync(fileStream))
                {
                    segmentCount++;
                    transcription += segment.Text + " ";
                    Console.WriteLine($"[WHISPER] Segment #{segmentCount}: '{segment.Text}' (start={segment.Start}, end={segment.End})");
                }
                
                var transcribeElapsed = (DateTime.Now - transcribeStart).TotalMilliseconds;
                transcription = transcription.Trim();
                
                Console.WriteLine($"[WHISPER] Transcription done in {transcribeElapsed:F0}ms, {segmentCount} segments");
                Console.WriteLine($"[WHISPER] Raw result: '{transcription}'");
                
                // Cleanup
                try { File.Delete(wavFile); } catch { }
                
                if (string.IsNullOrWhiteSpace(transcription))
                {
                    Console.WriteLine($"[WHISPER] No speech detected - returning fallback");
                    return "Kdo jsi?";
                }
                
                Console.WriteLine($"[WHISPER] ====== TRANSCRIBE END: '{transcription}' ======");
                return transcription;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WHISPER] ERROR: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[WHISPER] Stack: {ex.StackTrace}");
                return "Kdo jsi?";
            }
        }

        private void SaveAsWav(string filepath, byte[] g711Data, int payloadType = 0)
        {
            // Decode G.711 to 16-bit PCM using the correct codec
            byte[] pcmData;
            if (payloadType == 8)
            {
                Console.WriteLine($"[WHISPER] Decoding as A-law (PCMA)...");
                pcmData = DecodeAlawToPcm(g711Data);
            }
            else // Default to μ-law (PCMU, type 0) - most common in SIP
            {
                Console.WriteLine($"[WHISPER] Decoding as μ-law (PCMU)...");
                pcmData = DecodeMulawToPcm(g711Data);
            }

            Console.WriteLine($"[WHISPER] PCM data: {pcmData.Length} bytes ({pcmData.Length / 2 / 8000.0:F1}s at 8kHz mono 16bit)");
            
            using var fs = new FileStream(filepath, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            
            // WAV header - 8kHz mono 16-bit PCM
            bw.Write(new char[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + pcmData.Length);        // file size - 8
            bw.Write(new char[] { 'W', 'A', 'V', 'E' });
            
            // fmt chunk
            bw.Write(new char[] { 'f', 'm', 't', ' ' });
            bw.Write(16);                          // chunk size
            bw.Write((short)1);                    // PCM format
            bw.Write((short)1);                    // mono
            bw.Write(8000);                        // sample rate
            bw.Write(16000);                       // byte rate (8000 * 1 * 2)
            bw.Write((short)2);                    // block align (1 ch * 2 bytes)
            bw.Write((short)16);                   // bits per sample
            
            // data chunk
            bw.Write(new char[] { 'd', 'a', 't', 'a' });
            bw.Write(pcmData.Length);
            bw.Write(pcmData);
        }
    }
}