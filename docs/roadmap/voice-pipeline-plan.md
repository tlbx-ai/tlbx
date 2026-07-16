# Audio Pipeline Architecture - Voice Integration Plan

## Overview

MidTerm.Voice enables voice interaction with terminal sessions through browser audio streaming and AI providers.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Browser (MidTerm frontend)                                 │
│  ├─ Terminal UI (xterm.js)                                  │
│  ├─ Audio capture (Web Audio API)                           │
│  └─ Two WebSocket connections:                              │
│      ├─ /ws/mux → MidTerm server (terminal I/O)             │
│      └─ /voice → MidTerm.Voice (audio stream)               │
└─────────────────────────────────────────────────────────────┘
           │                              │
           ▼                              ▼
┌─────────────────────┐     ┌─────────────────────────────────┐
│  MidTerm Server     │◄───►│  MidTerm.Voice Server           │
│  (mt.exe)           │     │  ├─ /voice (browser audio)      │
│  └─ Terminal I/O    │     │  ├─ VoiceAssistant → AI         │
└─────────────────────┘     │  └─ WebSocketAudioHardware      │
                            └─────────────────────────────────┘
```

## Implementation Status

### ✅ Phase 1: Server-Side Infrastructure (COMPLETE)

Created `Ai.Tlbx.MidTerm.Voice` project:

| File | Description | Status |
|------|-------------|--------|
| `Ai.Tlbx.MidTerm.Voice.csproj` | Project with VoiceAssistant + OpenAI provider refs | ✅ |
| `WebSockets/WebSocketAudioHardware.cs` | `IAudioHardwareAccess` impl for WebSocket | ✅ |
| `WebSockets/VoiceWebSocketHandler.cs` | `/voice` endpoint handler | ✅ |
| `WebSockets/VoiceJsonContext.cs` | AOT-safe JSON serialization | ✅ |
| `Services/VoiceSessionService.cs` | Configuration (API keys, prompts) | ✅ |
| `Program.cs` | Voice server entry point (port 3000) | ✅ |

**WebSocket Protocol:**
- `{ "type": "start" }` - Start voice session with OpenAI
- `{ "type": "stop" }` - Stop voice session
- Binary frames (browser→server): PCM 16-bit audio from mic
- Binary frames (server→browser): PCM 16-bit audio from AI
- `{ "type": "config", "sampleRate": 24000 }` - Configure sample rate
- `{ "type": "error", "message": "..." }` - Error notification

### 🔲 Phase 2: Browser Audio Code (PENDING)

Need to add audio capture/playback to tlbx frontend.

**Option A (Quick):** Copy existing JS from VoiceAssistant.Hardware.Web
```
VoiceAssistant/Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/wwwroot/js/
├── webAudioAccess.js      → MidTerm/src/static/js/
└── audio-processor.js     → MidTerm/src/static/js/
```

**Option B (Better, Future):** Create npm package from TypeScript source
- Port JS to TypeScript in VoiceAssistant repo
- Publish as `@ai-tlbx/voice-audio` npm package
- Both tlbx and Hardware.Web consume from single source

**tlbx Frontend Tasks:**
| File | Description | Status |
|------|-------------|--------|
| `src/static/js/webAudioAccess.js` | Copy from VoiceAssistant | 🔲 |
| `src/static/js/audio-processor.js` | Copy from VoiceAssistant | 🔲 |
| `src/ts/modules/voice.ts` | WebSocket + audio glue | 🔲 |
| `src/static/index.html` | Add voice button | 🔲 |
| `Settings/MidTermSettings.cs` | Add VoiceServiceUrl | 🔲 |

### 🔲 Phase 3: Terminal Integration (PENDING)

Connect voice AI to terminal operations:

| Task | Description | Status |
|------|-------------|--------|
| Terminal tools | Add tools for VoiceAssistant to execute terminal commands | 🔲 |
| Session bridge | Connect VoiceAssistant to tlbx session API | 🔲 |
| Context awareness | Voice AI understands current terminal state | 🔲 |

### 🔲 Phase 4: Single Source Refactor (FUTURE)

Port to TypeScript npm package for DRY audio code:

```
VoiceAssistant/
├── packages/
│   └── voice-audio/                    # npm: @ai-tlbx/voice-audio
│       ├── src/
│       │   ├── webAudioAccess.ts       # Port from JS
│       │   ├── audioProcessor.ts       # Port from JS (worklet)
│       │   └── index.ts                # Exports
│       └── package.json
│
└── Hardware/
    └── Ai.Tlbx.VoiceAssistant.Hardware.Web/
        └── wwwroot/js/voice-audio.umd.js  # Built from npm package
```

## Audio Processing Details

The browser audio code (from VoiceAssistant.Hardware.Web) includes:
- 48kHz capture with echo cancellation, noise suppression, auto gain
- De-esser, compressor, anti-aliasing filters
- Provider-specific downsampling: 16kHz (Google) or 24kHz (OpenAI/xAI)
- AudioWorklet processor for efficient real-time processing

## Configuration

**Environment Variables:**
- `OPENAI_API_KEY` - OpenAI API key for voice provider

**appsettings.json:**
```json
{
  "OpenAI": {
    "ApiKey": "sk-..."
  },
  "Voice": {
    "SystemPrompt": "You are a helpful assistant...",
    "MidTermServerUrl": "https://localhost:2000"
  },
  "Port": 3000
}
```

## Running the Voice Server

```bash
cd Ai.Tlbx.MidTerm.Voice
dotnet run
# Listening on http://0.0.0.0:3000
# WebSocket endpoint: /voice
```

## Testing

1. Run tlbx on localhost:2000
2. Run MidTerm.Voice on localhost:3000
3. Connect browser WebSocket to ws://localhost:3000/voice
4. Send `{ "type": "start" }` to begin session
5. Send binary PCM audio frames
6. Receive binary PCM responses from AI
