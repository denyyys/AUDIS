# 🎙️ AUDIS

A professional SIP-based voice automation service for Windows that responds to incoming calls with DTMF-triggered audio playback, real-time weather information, and text-to-speech announcements in Czech language.

![.NET](https://img.shields.io/badge/.NET-8.0-blue)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![License](https://img.shields.io/badge/license-MIT-green)

## 📋 Overview

Audis Service is a WPF-based desktop application that acts as a SIP user agent, automatically answering incoming VoIP calls and playing custom audio files based on DTMF (touch-tone) input. It features a modern management console with real-time call monitoring, system tray integration, and dynamic configuration.

### Key Features

- ✅ **SIP Protocol Support** - Full VoIP call handling via SIPSorcery
- 🎹 **DTMF Recognition** - Responds to telephone keypad inputs (0-9, *, #)
- 🎵 **Audio Playback** - Plays custom WAV files mapped to specific keys
- 🌤️ **Weather Integration** - Real-time weather reports via Open-Meteo API
- 📅 **Czech Name Days** - Fetches daily Czech name day information
- 🗣️ **Text-to-Speech** - Google TTS integration for dynamic announcements
- 📊 **Real-time Monitoring** - Live call tracking with duration counters
- 🔍 **SIP Traffic Sniffer** - View all incoming/outgoing SIP messages
- 🖥️ **System Tray** - Runs minimized with tray notifications
- 🛡️ **Single Instance** - Prevents multiple instances from running
- ⚙️ **Dynamic Configuration** - No restart needed for audio mapping changes

## 🚀 Getting Started

### Prerequisites

- Windows 10/11 (x64)
- .NET 8.0 Runtime or SDK
- FFmpeg (for text-to-speech conversion)
- Network access on UDP port 5060 (default SIP port)

### Installation

1. **Clone the repository**

2. **Restore NuGet packages**
   ```bash
   dotnet restore
   ```

3. **Build the project**
   ```bash
   dotnet build -c Release
   ```

4. **Install FFmpeg**
   - Download from [ffmpeg.org](https://ffmpeg.org/download.html)
   - Add FFmpeg to your system PATH

5. **Create audio directory**
   ```bash
   mkdir C:\Scripts\AudisService\audio
   ```

6. **Add your audio files**
   - Place WAV files (8kHz, mono, PCM 16-bit) in `C:\Scripts\AudisService\audio\`
   - Default files: `eliska.wav`, `cibula.wav`, `sergei.wav`, `pam.wav`, `dollar.wav`, `smack.wav`

## 📖 Usage

### Starting the Service

1. Launch `AudisService.exe`
2. Configure network settings (Public IP and Port)
3. Click **Start Service**
4. The application will minimize to system tray

### Configuration

#### Network Settings
- **Public IP**: Your external IP address (used in SIP Contact header)
- **Port**: SIP listening port (default: 5060)

#### Weather Settings
- **City**: Display name for weather reports
- **Latitude/Longitude**: Geographic coordinates for weather data

#### Key Mappings
Configure which audio file plays for each DTMF key:

| Key | Default Action |
|-----|----------------|
| 1   | cibula.wav     |
| 2   | sergei.wav     |
| 3   | pam.wav        |
| 4   | dollar.wav     |
| 5   | smack.wav      |
| 0   | eliska.wav     |
| #   | INFO_PACKAGE (Weather + Name Day + Time) |
| 6-9, * | Not assigned |

### Special Functions

- **INFO_PACKAGE** (`#` key): Announces current weather, Czech name day, and time
- **SYSTEM_STATUS**: System diagnostics (uptime, memory usage)

### Call Flow

1. Incoming call arrives
2. System auto-answers with greeting (`eliska.wav`)
3. Caller presses DTMF key
4. Corresponding audio file plays or action executes
5. Returns to listening mode for next input
6. Call continues until caller hangs up

## 🏗️ Architecture

### Project Structure

```
AudisService/
├── App.xaml.cs              # Application entry point with mutex
├── MainWindow.xaml          # WPF UI layout
├── MainWindow.xaml.cs       # UI logic and event handlers
├── SipEngine.cs             # Core SIP/VoIP engine
├── Config.cs                # Configuration model
└── audio/                   # Audio files directory
    └── *.wav
```

### Technology Stack

- **Framework**: .NET 8.0 (WPF)
- **SIP Library**: [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) 8.0.3
- **Audio Processing**: RTP/G.711 µ-law codec
- **APIs**: 
  - Open-Meteo (Weather)
  - svatky.adresa.info (Name Days)
  - Google Translate TTS
- **UI**: Windows Presentation Foundation (WPF) + Windows Forms (Tray)

### Key Components

#### SipEngine.cs
- Manages SIP transport and user agent
- Handles incoming calls and DTMF detection
- Audio playback engine with RTP streaming
- API integration for weather and TTS

#### MainWindow.xaml.cs
- UI controller with real-time updates
- Call state management with "graveyard" anti-zombie system
- Configuration management
- System tray integration

#### Config.cs
- Serializable configuration model
- Default key mappings
- Network and location settings

## 🔧 Configuration Files

Configuration is stored in-memory and can be modified through the UI. Future versions may support persistent JSON config files.

Example configuration structure:
```csharp
{
  "PublicIp": "192.168.100.64",
  "Port": 5060,
  "WeatherCity": "Karviná",
  "WeatherLat": 49.85,
  "WeatherLong": 18.54,
  "KeyMappings": {
    "1": "cibula.wav",
    "2": "sergei.wav",
    "#": "INFO_PACKAGE"
  }
}
```

## 🐛 Troubleshooting

### Common Issues

**Service won't start on port 5060**
- Check if another SIP application is using the port
- Try using a different port (e.g., 5061)
- Ensure Windows Firewall allows UDP traffic

**No audio playback**
- Verify WAV files are 8kHz, mono, PCM 16-bit format
- Check that files exist in `C:\Scripts\AudisService\audio\`
- Review logs in the UI console

**DTMF not detected**
- Some SIP providers use in-band DTMF (not RFC 2833)
- Check SIP traffic sniffer for DTMF events
- Verify phone/softphone is configured for RFC 2833

**TTS not working**
- Ensure FFmpeg is installed and in PATH
- Test with: `ffmpeg -version`
- Check internet connectivity for Google TTS

**Calls won't connect**
- Verify Public IP matches your actual external IP
- Check NAT/firewall rules for UDP 5060
- Review SIP traffic sniffer for errors

## 🔒 Security Considerations

- **No Authentication**: Current version accepts all incoming calls
- **Public Exposure**: Binding to 0.0.0.0 exposes service to network
- **Rate Limiting**: No built-in protection against call flooding

**Recommendations:**
- Use firewall rules to restrict access
- Consider implementing SIP authentication
- Monitor call logs for suspicious activity

## 🛣️ Roadmap

- [ ] Persistent configuration file (JSON)
- [ ] Call recording capability
- [ ] Web-based configuration interface
- [ ] Call history database
- [ ] Custom TTS voice selection


## 📄 License

This project is licensed under the MIT License.

## 📊 System Requirements

- **OS**: Windows 10/11 (x64)
- **RAM**: Minimum 256MB, Recommended 512MB
- **.NET**: 8.0 Runtime
- **Network**: UDP port access for SIP
- **Disk**: ~50MB for application + audio files

---

*Version 1.1 - Kybl Enterprise*
