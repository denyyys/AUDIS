# AUDIS — Enterprise SIP Manager
**v1.4 · Kybl Enterprise**

A Windows desktop SIP telephony server and outbound client with an integrated AI voice assistant. AUDIS answers incoming SIP calls, plays DTMF-driven audio menus, transcribes caller speech via Whisper, queries a local LLM (Ollama/Gemma), and speaks the response back via Google TTS. An independent outbound SIP Client places calls with configurable greeting audio, call recording, and a browser-based remote interface.

**Full user manual: `AudisHelp.htm`** (opened from the Help toolbar button inside the app).

---

## Requirements

| Software | Notes |
|---|---|
| Windows 10/11 x64, .NET 8 Desktop Runtime | WPF target |
| [Ollama](https://ollama.ai) + `ollama pull gemma3:1b` | Must be on PATH |
| [ffmpeg](https://ffmpeg.org) | Must be on PATH |
| `ggml-base.bin` Whisper model | [Download from Hugging Face](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin) — place next to EXE |

**NuGet:** `SIPSorcery 8.0.3` · `Whisper.net 1.7.1` · `Microsoft.Extensions.Logging 8.0.0`

---

## Features

| | |
|---|---|
| SIP server — inbound calls, DTMF IVR | Outbound SIP Client (port 5061) |
| AI voice assistant (Whisper + Ollama + Google TTS) | SIP registration with digest auth |
| Voicemail recording | Server + client call recording (independent) |
| Weather + time info via Open-Meteo | Greeting audio mode — standard or custom WAV |
| Web Client — browser UI + REST API + WebSocket | Call log, key mapping editor, log export |
| Custom toolbar icons (Windows DLL icon picker) | System tray, single-instance guard, crash dump |

---

## Folder Structure

```
AudisService.exe
ggml-base.bin                              ← Whisper model
AudisHelp.htm                              ← User manual
audis_server_config.json                   ← Server settings, key mappings, icon choices
sip_client_config.json                     ← Client settings, contacts, audio mode, recording flag

audio/                                     ← WAV files — 8 kHz mono 16-bit PCM required
│   eliska.wav                             ← Default greeting (required)
│   cibula.wav, sergei.wav, pam.wav, ...

recordings/
│   call_{id}_{timestamp}.wav              ← Inbound server recordings
│   client_call_{id}_{timestamp}.wav       ← Outbound SIP Client recordings

voicemail/                                 ← Messages recorded by inbound callers
logs/                                      ← Exported log and call-log files
```

> `ffmpeg -i input.mp3 -ar 8000 -ac 1 -acodec pcm_s16le output.wav`

---

## Key Mappings

| Key | Default |
|---|---|
| 0–5 | WAV files (`eliska.wav`, `cibula.wav`, …) |
| 6 | `SYSTEM_STATUS` |
| 7 | `INFO_PACKAGE` — current time + live weather |
| 8 | `VOICEMAIL` |
| * / # | `AI_START` / `AI_STOP` — Whisper + Ollama |

Any key can map to a WAV filename or system keyword. Editable in the Key Mappings tab.

---

## How It Works

**Inbound call:** SIPSorcery accepts the INVITE → plays `eliska.wav` → holds the RTP stream open with silence packets → routes each DTMF digit to a WAV file or system action → records both audio legs (mixed mono WAV) if recording is enabled.

**AI mode (`*` → `#`):** Raw G.711 RTP bytes buffer while the caller speaks → decoded to PCM → written to a temp WAV → Whisper.net transcribes (Czech) → text sent to Ollama → response converted to speech by Google TTS via ffmpeg → played back over RTP.

**Outbound SIP Client:** Separate transport on port 5061. On answer, plays the configured greeting (standard `eliska.wav` or a custom WAV from the `audio/` folder), then routes DTMF via the same Key Mappings table. Both audio legs buffer throughout the call and are mixed into a WAV on hangup if recording is enabled — fully independent of the server's recording switch.

**Web Client:** Embedded `HttpListener` serves a single-page app and WebSocket. The browser shares the exact same `SipClientEngine` instance as the WPF window — state changes (call, registration, audio mode) push to all connected tabs in real time. REST API available at `/api/call`, `/api/hangup`, `/api/dtmf`, `/api/audiomode`, etc.

**DTMF:** RFC 4733 (RTP events, payload types 96–127) and SIP INFO run in parallel with a 200 ms debounce. In-band DTMF is not supported.

**Recording mix:** Both G.711 legs are decoded to 16-bit PCM, averaged sample-by-sample, and written as mono 8 kHz WAV. Server recordings are named `call_*`, SIP Client recordings `client_call_*` — both land in `recordings/`.

---

## Port Reference

| Port | Protocol | Purpose |
|---|---|---|
| 5060 | UDP | SIP server — inbound |
| 5061 | UDP/TCP | SIP Client — outbound |
| 10000–20000 | UDP | RTP — server |
| 12000–12100 | UDP | RTP — client (configurable) |
| 8765 | TCP | Web Client (HTTP + WebSocket) |
| 11434 | TCP | Ollama local API |
