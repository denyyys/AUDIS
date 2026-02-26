# AUDIS — Enterprise SIP Manager
**v1.3 · Kybl Enterprise**

A Windows desktop SIP server with an integrated AI voice assistant. Audis answers incoming SIP calls, plays audio responses based on DTMF input, transcribes caller speech using Whisper, queries a local LLM (Ollama/Gemma), and speaks the answer back using Google TTS — all over a standard SIP/RTP connection.

---

## Table of Contents
- [Features](#features)
- [Requirements](#requirements)
- [Folder Structure](#folder-structure)
- [Key Mappings](#key-mappings)
- [Setup](#setup)
- [Inner Workings](#inner-workings)
- [Troubleshooting](#troubleshooting)

---

## Features

| Feature | Description |
|---|---|
| **SIP Server** | Listens for incoming UDP SIP calls on a configurable port (default 5060) |
| **DTMF Routing** | Routes calls by DTMF key press — plays WAV files or triggers system actions |
| **AI Voice Assistant** | Press `*` to speak to the AI. Whisper transcribes your speech, Gemma answers, Google TTS speaks it back |
| **Weather + Time Info** | Press `7` for a live weather report from Open-Meteo for your configured city |
| **Voicemail** | Press `8` to record a message; saved as a timestamped WAV file |
| **Call Recording** | Optional full-call recording (incoming + outgoing mixed) saved as WAV |
| **System Status** | Press `6` for a spoken system health confirmation |
| **UI Dashboard** | Live call view with status, duration, and last DTMF input |
| **Key Mapping Editor** | Editable key → action/filename mapping table in the UI |
| **Log Viewer + Export** | Live Consolas log panel with one-click export to timestamped `.txt` |
| **System Tray** | Minimizes to tray; double-click to restore |
| **Single Instance** | Mutex guard prevents running two copies simultaneously |
| **Crash Dump** | Unhandled exceptions write `audis_crash.txt` to the Desktop automatically |

---

## Requirements

### Runtime Software

| Software | Purpose | Notes |
|---|---|---|
| **Windows 10/11 x64** | OS | WPF target, Windows-only |
| **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)** | App framework | `net8.0-windows` |
| **[Ollama](https://ollama.ai)** | Local LLM server | Must be in system PATH |
| **[ffmpeg](https://ffmpeg.org/download.html)** | MP3→WAV conversion for TTS | Must be in system PATH |
| **`ggml-base.bin`** | Whisper speech recognition model | See [Whisper Model](#whisper-model) below |

### NuGet Packages (auto-restored on build)

| Package | Version | Purpose |
|---|---|---|
| `SIPSorcery` | 8.0.3 | SIP stack, RTP, G.711 codecs |
| `Whisper.net` | 1.7.1 | .NET bindings for Whisper |
| `Whisper.net.Runtime` | 1.7.1 | Native Whisper runtime |
| `Microsoft.Extensions.Logging` | 8.0.0 | Structured logging interface |

### Whisper Model

Download `ggml-base.bin` and place it in the application's output directory (next to the `.exe`):

```
https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin
```

The app will log its exact expected path at startup under `[WHISPER]` if the file is missing.

### Ollama Model

After installing Ollama, pull the default model:

```
ollama pull gemma3:1b
```

Any Ollama-compatible model can be used — change it in the **Config** tab. Larger models give better answers but take longer per response.

---

## Folder Structure

```
AudisService.exe
ggml-base.bin               ← Whisper model (manual download required)
icon.ico
icon.png
appsettings.json

audio/                      ← WAV files played on key presses
│   eliska.wav              ← Greeting (played on call connect and as menu return)
│   cibula.wav
│   sergei.wav
│   pam.wav
│   dollar.wav
│   smack.wav
│   (add your own .wav files and assign them in Key Mappings)

recordings/                 ← Call recordings (when recording is enabled)
│   call_{id}_{timestamp}.wav

voicemail/                  ← Voicemail messages left by callers
│   msg_{timestamp}.wav

logs/                       ← Exported log files
│   audis_log_{timestamp}.txt
```

> **Audio format:** All WAV files in `audio/` must be **8kHz mono 16-bit PCM**. Other formats will either play garbled or not at all. Convert using:
> ```
> ffmpeg -i input.mp3 -ar 8000 -ac 1 -acodec pcm_s16le output.wav
> ```

---

## Key Mappings

Default DTMF key assignments (editable in the **Key Mappings** tab):

| Key | Action |
|---|---|
| `0` | Play `eliska.wav` |
| `1` | Play `cibula.wav` |
| `2` | Play `sergei.wav` |
| `3` | Play `pam.wav` |
| `4` | Play `dollar.wav` |
| `5` | Play `smack.wav` |
| `6` | `SYSTEM_STATUS` — announces system health via TTS |
| `7` | `INFO_PACKAGE` — announces current time + live weather |
| `8` | `VOICEMAIL` — records caller message until `#` or 30s timeout |
| `9` | *(unassigned)* |
| `*` | **AI mode** — Whisper + Ollama voice assistant |
| `#` | Stops AI recording and submits for transcription |

Any key can be mapped to a `.wav` filename or a `SYSTEM_*` action string. If a key has no mapping or its file doesn't exist, it is silently ignored.

---

## Setup

1. **Install** .NET 8 Desktop Runtime, Ollama, and ffmpeg. Ensure both `ollama` and `ffmpeg` are accessible from a plain command prompt (`where ollama`, `where ffmpeg`).

2. **Download** `ggml-base.bin` and place it next to `AudisService.exe`.

3. **Pull the LLM model:**
   ```
   ollama pull gemma3:1b
   ```

4. **Place audio files** for your key mappings into the `audio/` subfolder.

5. **Launch Audis.** In the **Config** tab set your Public IP (the IP your SIP clients will send RTP to — on a LAN this is your machine's LAN IP) and SIP port.

6. **Click "Start AI"** — this runs `ollama serve` as a managed subprocess and preloads the configured model with a test prompt. The AI status indicator turns green when the HTTP API responds.

7. **Click "Start Service"** — the SIP UDP listener starts. Point your SIP client at the configured IP and port, dial any extension, and the call will be answered.

---

## Inner Workings

### Call Flow

```
Incoming SIP INVITE
       │
       ▼
SIPUserAgent.AcceptCall() + Answer()
       │
       ▼
VoIPMediaSession (SIPSorcery RTP)
       │
       ├── OnRtpPacketReceived ──► DTMF detection (RFC 4733)
       │                    └──► Audio buffering (AI / voicemail / recording)
       │
       ▼
Play greeting: eliska.wav
       │
       ▼
Wait loop: SendSilence() every 20ms (keeps RTP stream alive)
       │
       ▼
DTMF key received
       │
       ├── WAV key ──► PlayFile() ──► PlayPcmBytes()
       ├── Key 6/7  ──► TTS text ──► GenerateGoogleTtsBytes() ──► PlayPcmBytes()
       ├── Key 8    ──► Record voicemailBuffer ──► SaveWavFile()
       └── Key *    ──► AI Mode (see below)
```

### AI Mode Pipeline

```
* pressed
    │
    ▼
Google TTS: "Mluvte po zaznění tónu..."
    │
    ▼
callState.IsAiRecording = true
    │
    ▼
RTP audio packets ──► aiInputBuffer (raw G.711 bytes)
    │
    ▼                    (# pressed or 10s timeout)
callState.IsAiRecording = false
    │
    ▼
AiCore.TranscribeAudioAsync(capturedAudio, payloadType)
    │   ├── Detect codec: type 0 = μ-law (PCMU), type 8 = A-law (PCMA)
    │   ├── Decode G.711 → 16-bit PCM
    │   ├── Write 8kHz mono WAV to temp file
    │   └── Whisper.net processes WAV → Czech transcription
    │
    ▼
AiCore.AskLocalAiAsync(transcribedText)
    │   └── POST http://localhost:11434/api/generate
    │       model: gemma3:1b, max_tokens: 40, lang: Czech
    │
    ▼
GenerateGoogleTtsBytes(aiResponse)
    │   ├── GET translate.google.com/translate_tts?tl=cs&q=...
    │   ├── Save MP3
    │   └── ffmpeg: MP3 → 8kHz WAV
    │
    ▼
PlayPcmBytes() ──► μ-law encode ──► RTP packets at 20ms/160 samples
```

### RTP Audio Playback Timing

`PlayPcmBytes` sends 160-sample (20ms) μ-law packets. It uses a `Stopwatch` to calculate per-packet timing drift and sleeps in 5ms increments (not a single `Task.Delay(20)`) so it can detect hangups mid-playback and abort immediately without sending an extra 15ms of audio to a dead call.

### DTMF Detection

Two parallel methods run simultaneously so it works regardless of how the client sends tones:

- **RFC 4733 (RTP Events):** Monitored on all dynamic payload types (96–127). Only the end-of-event flag is acted on to prevent duplicate triggers. First 50 packets are fully logged.
- **SIP INFO:** Handled by `SIPUserAgent.OnDtmfTone`. Both methods share a 200ms debounce keyed on digit + timestamp.

### G.711 Codec Auto-Detection

The first audio RTP packet received sets `audioPayloadType` for the entire call. This value is passed to Whisper's decode path so the correct decoder (μ-law vs. A-law) is always used, regardless of what the client negotiated. This is the single most common cause of silent AI failures — see troubleshooting below.

---

## Troubleshooting

### AI always responds with the hardcoded fallback ("Kdo jsi?")

The fallback fires when Whisper returns an empty or whitespace transcription. Work through these in order:

**1. Check the codec being captured**
Look for this line in logs after pressing `*`:
```
[RTP AUDIO] Codec detected: PayloadType=X (μ-law/PCMU or A-law/PCMA)
```
If this line never appears, **no audio is arriving during AI recording** — your client may be holding RTP during DTMF or stopping the stream. Check your SIP client's "send audio while on hold" setting.

**2. Check the recording buffer**
```
[AI] Recording stopped. Captured: X bytes (~Xs)
```
If `X bytes` is 0 or less than 1600 (0.2s), audio is not reaching the buffer at all. Compare timestamps — if Codec detected appears early in the call but AI recording shows 0 bytes, the `IsAiRecording` flag is being set and cleared too fast (race condition on DTMF).

**3. Check what Whisper receives**
```
[WHISPER] First 16 bytes: FF-FF-FF-FF-...
```
If all bytes are `0xFF` (μ-law silence) or `0xD5` (A-law silence), the buffer filled with silence packets, not speech. Verify your SIP client is not muting its microphone when sending DTMF.

**4. Check Whisper segments**
```
[WHISPER] Segment #1: '...'
```
If there are zero segment lines, Whisper loaded but found no speech in the audio. The WAV is likely too short (under ~1 second) or is silence. The temp WAV file path is logged — you can listen to it before it's deleted by adding a `Thread.Sleep` after `SaveAsWav`.

**5. Whisper model not loaded**
```
[WHISPER] Model NOT found at: C:\...\ggml-base.bin
```
Download and place the file as described in [Whisper Model](#whisper-model). The app does not auto-download it.

---

### Ollama / AI is OFFLINE despite Ollama being installed

Audis polls `http://localhost:11434/api/generate` every 2 seconds. If the status stays red:

- Run `ollama serve` manually in a terminal and watch for port binding errors. Port 11434 may be in use by another process.
- Run `ollama list` — if your configured model is not in the list, the server will return 404/error. Pull it with `ollama pull gemma3:1b`.
- If Ollama was installed but `Start AI` says "not found in PATH", the installer did not add it to the system PATH. Add `C:\Users\<you>\AppData\Local\Programs\Ollama` (or wherever it installed) to PATH manually and restart Audis.
- Ollama's first response after a cold start can take 10–30 seconds while the model loads into VRAM/RAM. The status will flip to green after the first successful response.

---

### TTS not working / AI speaks nothing after responding

Google TTS requires ffmpeg to convert the downloaded MP3 to 8kHz WAV. Look for:
```
[TTS] Failed: ...
[TTS] ffmpeg exit code: 1
```

- Confirm `ffmpeg -version` works from a command prompt. If not, add ffmpeg's `bin/` directory to PATH.
- Google's TTS endpoint (`translate_tts`) is unofficial and rate-limited. If you see HTTP 429 or empty responses, you have hit the limit. Consider caching commonly-spoken strings as pre-generated WAV files in the `audio/` folder.

---

### One-Way Audio (caller can't hear Audis, or Audis can't hear caller)

**Audis can't hear caller:**  
`VoIPMediaSession.AcceptRtpFromAny = true` is already set, so the RTP source IP/port mismatch is bypassed. If `[RTP AUDIO] Audio packet #1` never appears, the client is sending RTP to the wrong IP. Verify `PublicIp` in Config matches the IP your SIP client resolves from the SIP response headers. On a NAT'd setup, this must be the external IP even if Audis binds on `0.0.0.0`.

**Caller can't hear Audis:**  
RTP is being sent but the client rejects it (firewall, wrong destination port, or symmetric NAT). Check that `PublicIp` is correct. If behind NAT, verify UDP port 5060 (SIP) and the RTP port range are forwarded to the machine running Audis.

---

### DTMF keys not being detected

Look for `[RTP DTMF-LIKE]` and `[SIP INFO DTMF]` lines after pressing a key.

- **No DTMF-LIKE lines at all:** Your client is sending DTMF in-band (inside the audio stream as tones, RFC 2833 disabled). In-band DTMF is not supported. Enable RFC 2833 / RFC 4733 in your SIP client settings.
- **DTMF-LIKE lines appear but no key is processed:** The payload type is in the 96–127 range but the end-of-event flag is never set, meaning you're only seeing the key-down packets. This is a SIP client bug — try a different client or check its DTMF transmit mode settings.
- **SIP INFO lines appear but key does nothing:** The raw `tone` string is being logged. If the digit value is something unexpected (e.g., a letter or `Tone10`), the string conversion in `OnDtmfTone` is not mapping it. Check the raw log value and add a mapping case if needed.

---

### Call is answered but Audis immediately hangs up / no greeting plays

The greeting file `eliska.wav` is missing from the `audio/` folder or is in the wrong format. Check:
```
[ERROR] File NOT FOUND: C:\...\audio\eliska.wav
```
Place a valid 8kHz mono 16-bit PCM WAV file named `eliska.wav` in the `audio/` directory. Without it, `PlayFile` returns immediately and the call loop exits on the first iteration.

---

### `audis_crash.txt` appears on the Desktop

The global exception handler caught an unhandled crash. The file contains the full exception type, message, and stack trace. Common causes:

- **WPF binding error on startup** — usually a missing DataContext property. Check the stack trace for `System.Windows` lines.
- **SIPSorcery port already in use** — if another SIP application is bound to port 5060, `SIPUDPChannel` throws on construction. Change the port in Config or stop the conflicting service.
- **Whisper.net native runtime failure** — `Whisper.net.Runtime` ships native binaries. If the wrong CPU architecture runtime is loaded, it throws at model load time. Ensure you're running the x64 build on an x64 machine.
