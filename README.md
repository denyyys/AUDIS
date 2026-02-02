# AUDIS

**Audio Delivery and Interactive Service** - A C# SIP-based voice service that provides an interactive voice menu system with real-time TTS.

## Overview

AUDIS is a SIP (Session Initiation Protocol) voice service built with .NET that answers incoming calls and provides an interactive menu system. Users can navigate through audio options using DTMF (touch-tone) inputs and receive real-time weather updates via text-to-speech.

## Features

- **SIP Call Handling** - Accepts and manages incoming SIP calls on UDP port 5060
- **Interactive Voice Menu** - Navigate through pre-recorded audio files using keypad input
- **Real-Time Weather** - Fetches current weather for Czech Republic via Open-Meteo API
- **Text-to-Speech** - Converts weather data to Czech-language speech using Google TTS
- **DTMF Detection** - Robust dual-mode DTMF tone detection (UA events + RTP packet parsing)
- **Stable Audio Playback** - Precise 20ms-interval RTP streaming optimized for legacy hardware

## Menu Options

- **Key 0** - Replay main menu (eliska.wav)
- **Key 1** - Play cibula.wav
- **Key 2** - Play sergei.wav
- **Key 3** - Play pam.wav
- **Key 4** - Play dollar.wav
- **Key 5** - Play smack.wav
- **Key 7** - Get current weather

## Configuration

Edit the constants in `Worker.cs` to match your environment:

```csharp
private const string LISTEN_IP = "0.0.0.0";        // Interface to listen on
private const string PUBLIC_IP = "192.168.100.64"; // Your public/external IP
private const int SIP_PORT = 5060;                 // SIP port
private const string BASE_DIR = @"C:\Scripts\audis"; // Base directory
```

For weather location, modify the coordinates in `GetWeatherTextAsync()`:
```csharp
string url = "https://api.open-meteo.com/v1/forecast?latitude=49.85&longitude=18.54&current_weather=true";
```

## Running the Service

### Development
```bash
dotnet run
```

### Production (Windows Service)
```bash
dotnet publish -c Release
sc create AUDIS binPath="C:\path\to\AudisService.exe"
sc start AUDIS
```

### Production (Linux systemd)
```bash
dotnet publish -c Release
sudo systemctl enable audis.service
sudo systemctl start audis.service
```

## Architecture

### Technology Stack
- **SIPSorcery** - SIP protocol and RTP media handling
- **Open-Meteo API** - Weather data retrieval
- **Google Translate TTS** - Text-to-speech synthesis
- **FFmpeg** - Audio format conversion

### Call Flow
1. Incoming SIP call received
2. RTP session established
3. Main menu played (eliska.wav)
4. Service waits for DTMF input
5. On key press:
   - Keys 1-5, 0: Play corresponding audio file
   - Key 7: Fetch weather → TTS generation → Playback → 3s pause → Replay menu
6. Loop continues until hangup

### DTMF Detection
AUDIS uses a dual-mode approach for maximum compatibility:
- **UA Event Handler** - Standard SIPUserAgent DTMF events
- **RTP Packet Parser** - Direct RTP payload inspection for legacy devices

### Audio Playback Timing
The service uses a precise Stopwatch-based timing loop to maintain 20ms intervals between RTP packets, ensuring smooth playback on legacy SIP hardware like Linksys phones.

## Dependencies

```xml
<PackageReference Include="SIPSorcery" Version="6.x.x" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="6.x.x" />
```

## Logging

AUDIS provides detailed logging for:
- Service startup/shutdown
- Incoming calls
- DTMF key detection (both UA and RTP events)
- Audio playback events
- Weather API requests
- Error conditions

## Troubleshooting

### No audio playback
- Verify WAV files are 8000Hz, mono, 16-bit PCM
- Check RTP ports are not blocked by firewall
- Ensure `AcceptRtpFromAny = true` for NAT scenarios

### DTMF not detected
- Check both log outputs: `[UA EVENT]` and `[RTP EVENT]`
- Verify SIP client sends RFC 2833 DTMF events
- Some clients may require payload type 101 configuration

### Weather not working
- Verify internet connectivity
- Check FFmpeg is in system PATH
- Ensure write permissions to `C:\Scripts\audis\`

## Future Enhancements

- [ ] Multiple language support
- [ ] Database integration for call logging
- [ ] Configurable menu via JSON/XML
- [ ] Additional weather locations
- [ ] Voicemail capability
- [ ] Call recording

## License

This project is licensed under the MIT License.

## Acknowledgments

- [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) - SIP and RTP implementation
- [Open-Meteo](https://open-meteo.com/) - Free weather API
- Google Translate TTS - Text-to-speech synthesis

---
