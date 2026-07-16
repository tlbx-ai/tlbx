/**
 * Voice Module
 *
 * Handles WebSocket connection to tlbx.Voice server
 * and bridges audio capture/playback.
 */

import { createLogger } from './logging';
import { setVoiceStatus, setToggleRecording } from './sidebar/voiceSection';
import {
  addChatMessage,
  showChatPanel,
  toggleChatPanel,
  showToolConfirmation,
  finalizeToolGroup,
} from './chat';
import { processToolRequest } from './voiceTools';
import { $voiceServerPassword } from '../stores';
import type { VoiceToolName } from '../types';
import type { VoiceHealthResponse, VoiceProvider } from '../types';
import { showAlert } from '../utils/dialog';

const log = createLogger('voice');
const VOICE_SERVER_URL = 'https://api.tlbx.ai';

let ws: WebSocket | null = null;
let isSessionActive = false;
let voiceServerAvailable = false;
let audioFrameCount = 0;
let totalBytesSent = 0;

// Voice settings state
let voiceProviders: VoiceProvider[] = [];
let selectedProvider = '';
let selectedVoice = '';
let selectedSpeed = 1.0;

/**
 * Check if tlbx.Voice server is available and fetch providers
 */
export async function checkVoiceServerHealth(): Promise<boolean> {
  try {
    const url = `${VOICE_SERVER_URL}/api/health`;

    const controller = new AbortController();
    const timeoutId = setTimeout(() => {
      controller.abort();
    }, 2000);

    const response = await fetch(url, { signal: controller.signal });
    clearTimeout(timeoutId);

    if (response.ok) {
      const data = (await response.json()) as VoiceHealthResponse;
      voiceServerAvailable = data.status === 'ok';
      log.info(() => `Voice server available: v${data.version}`);

      // Store providers and defaults if available
      if (data.providers) {
        voiceProviders = data.providers;
        populateVoiceDropdown();
      }
      if (data.defaults) {
        selectedProvider = data.defaults.provider;
        selectedVoice = data.defaults.voice;
        selectedSpeed = data.defaults.speed;
        updateSpeedDisplay();
      }

      return voiceServerAvailable;
    }
  } catch {
    log.info(() => 'Voice server not available');
  }
  voiceServerAvailable = false;
  return false;
}

/**
 * Populate the voice dropdown with available providers and voices
 */
function populateVoiceDropdown(): void {
  const voiceSelect = document.getElementById('voice-select') as HTMLSelectElement | null;
  if (!voiceSelect) return;

  voiceSelect.innerHTML = '';

  for (const provider of voiceProviders) {
    if (!provider.available || provider.voices.length === 0) continue;

    const optgroup = document.createElement('optgroup');
    optgroup.label = provider.name;

    for (const voice of provider.voices) {
      const option = document.createElement('option');
      option.value = `${provider.id}:${voice.id}`;
      option.textContent = voice.name;

      if (provider.id === selectedProvider && voice.id === selectedVoice) {
        option.selected = true;
      }

      optgroup.appendChild(option);
    }

    voiceSelect.appendChild(optgroup);
  }

  log.info(() => `Voice dropdown populated with ${voiceProviders.length} providers`);
}

/** Microphone device info */
interface MicDevice {
  id: string;
  name: string;
  isDefault: boolean;
}

/**
 * Populate the microphone dropdown with available devices
 */
export async function populateMicDropdown(): Promise<void> {
  const micSelect = document.getElementById('mic-select') as HTMLSelectElement | null;
  if (!micSelect) return;

  try {
    if (window.getAvailableMicrophones) {
      const mics = (await window.getAvailableMicrophones()) as MicDevice[];
      micSelect.innerHTML = '<option value="">Default</option>';

      for (const mic of mics) {
        const option = document.createElement('option');
        option.value = mic.id;
        option.textContent = mic.name;
        if (mic.isDefault) {
          option.selected = true;
        }
        micSelect.appendChild(option);
      }

      log.info(() => `Microphone dropdown populated with ${mics.length} devices`);
    }
  } catch (error) {
    log.error(() => `Failed to get microphones: ${String(error)}`);
  }
}

/**
 * Update the speed display value
 */
function updateSpeedDisplay(): void {
  const speedValue = document.getElementById('voice-speed-value');
  const speedSlider = document.getElementById('voice-speed') as HTMLInputElement | null;

  if (speedValue) {
    speedValue.textContent = `${selectedSpeed}x`;
  }
  if (speedSlider) {
    speedSlider.value = String(selectedSpeed);
  }
}

/**
 * Check microphone permission status without triggering a prompt.
 * Returns 'granted', 'prompt', or 'denied'.
 */
export async function checkMicrophonePermissionStatus(): Promise<'granted' | 'prompt' | 'denied'> {
  try {
    const result = await navigator.permissions.query({ name: 'microphone' as PermissionName });
    return result.state as 'granted' | 'prompt' | 'denied';
  } catch {
    return 'prompt';
  }
}

/**
 * Populate the microphone dropdown passively (only if permission already granted).
 * Does not open audio devices or trigger permission prompts.
 */
export async function populateMicDropdownPassive(): Promise<void> {
  const micSelect = document.getElementById('mic-select') as HTMLSelectElement | null;
  if (!micSelect) return;

  try {
    const devices = await navigator.mediaDevices.enumerateDevices();
    const audioInputs = devices.filter((d) => d.kind === 'audioinput');
    const hasLabels = audioInputs.some((d) => d.label.length > 0);

    if (!hasLabels) {
      micSelect.innerHTML = '<option value="">Select microphone...</option>';
      return;
    }

    micSelect.innerHTML = '<option value="">Default</option>';
    for (const device of audioInputs) {
      const option = document.createElement('option');
      option.value = device.deviceId;
      option.textContent = device.label || `Microphone ${device.deviceId.slice(0, 8)}`;
      micSelect.appendChild(option);
    }
    log.info(() => `Microphone dropdown populated passively with ${audioInputs.length} devices`);
  } catch (error) {
    log.warn(() => `Failed to enumerate devices passively: ${String(error)}`);
    micSelect.innerHTML = '<option value="">Select microphone...</option>';
  }
}

/**
 * Request microphone permission and initialize audio
 */
async function requestMicrophonePermission(): Promise<boolean> {
  try {
    log.info(() => 'Requesting microphone permission');

    if (!window.initAudioWithUserInteraction) {
      log.error(() => 'Audio API not available');
      setVoiceStatus('Audio API not available');
      return false;
    }

    const result = await window.initAudioWithUserInteraction();
    if (!result) {
      setVoiceStatus('Audio init failed');
      return false;
    }

    if (window.requestMicrophonePermissionAndGetDevices) {
      await window.requestMicrophonePermissionAndGetDevices();
    }

    // Populate microphone dropdown after permission granted
    await populateMicDropdown();

    log.info(() => 'Microphone permission granted');
    setVoiceStatus('Ready');
    return true;
  } catch (error) {
    log.error(() => `Microphone permission error: ${String(error)}`);
    setVoiceStatus('Mic permission denied');
    return false;
  }
}

/**
 * Start a voice session - connect to tlbx.Voice and begin recording.
 * Single entry point: requests mic permission if needed, then starts session.
 */
export async function startVoiceSession(): Promise<void> {
  if (isSessionActive) {
    log.warn(() => 'Voice session already active');
    return;
  }

  setVoiceStatus('Initializing...');

  // Request permission if needed (this opens audio devices)
  const permissionGranted = await requestMicrophonePermission();
  if (!permissionGranted) {
    return;
  }

  try {
    let wsUrl = `wss://api.tlbx.ai/voice`;

    // Append password if configured
    const password = $voiceServerPassword.get();
    if (password) {
      wsUrl += `?password=${encodeURIComponent(password)}`;
    }

    // Reset counters
    audioFrameCount = 0;
    totalBytesSent = 0;

    log.info(() => `[WS] Connecting to ${wsUrl}`);
    setVoiceStatus('Connecting...');

    ws = new WebSocket(wsUrl);

    ws.onopen = async () => {
      log.info(() => '[WS] Connected, sending start command');
      setVoiceStatus('Connected');

      // Show chat panel when voice session starts
      showChatPanel();

      // Send start message with settings
      const startMsg = JSON.stringify({
        type: 'start',
        provider: selectedProvider,
        voice: selectedVoice,
        speed: selectedSpeed,
      });
      ws?.send(startMsg);
      log.info(() => `[WS] Sent: ${startMsg}`);

      // Start recording
      if (window.startRecording) {
        log.info(() => '[AUDIO] Calling startRecording(callback, 500ms, null, 24000Hz)');
        const success = await window.startRecording(
          (base64Audio: string) => {
            if (ws && ws.readyState === WebSocket.OPEN) {
              // Convert base64 to ArrayBuffer and send
              const bytes = base64ToArrayBuffer(base64Audio);
              audioFrameCount++;
              totalBytesSent += bytes.byteLength;
              ws.send(bytes);
            } else {
              log.warn(() => `[AUDIO] Frame dropped - WS not open (readyState: ${ws?.readyState})`);
            }
          },
          500,
          null,
          24000,
        );

        log.info(() => `[AUDIO] startRecording returned: ${success}`);

        if (success) {
          isSessionActive = true;
          setVoiceStatus('Listening...');
          setToggleRecording(true);
          log.info(() => '[SESSION] Voice session active');
        } else {
          log.error(() => '[AUDIO] Recording failed to start');
          setVoiceStatus('Recording failed');
          ws?.close();
        }
      } else {
        log.error(() => '[AUDIO] window.startRecording not available');
      }
    };

    ws.onmessage = async (event: MessageEvent) => {
      if (event.data instanceof Blob) {
        // Audio data from server - play without logging every frame
        const arrayBuffer = await event.data.arrayBuffer();
        const base64 = arrayBufferToBase64(arrayBuffer);

        if (window.playAudio) {
          await window.playAudio(base64, 24000);
        }
      } else if (typeof event.data === 'string') {
        // JSON message
        try {
          const msg = JSON.parse(event.data) as VoiceMessage;
          handleVoiceMessage(msg);
        } catch {
          log.warn(() => `[WS] Invalid JSON from voice server: ${event.data}`);
        }
      }
    };

    ws.onclose = (event: CloseEvent) => {
      log.info(
        () => `[WS] Closed: code=${event.code} reason="${event.reason}" clean=${event.wasClean}`,
      );
      log.info(() => `[SESSION] Stats: ${audioFrameCount} frames, ${totalBytesSent} bytes sent`);
      isSessionActive = false;
      setVoiceStatus('Disconnected');
      setToggleRecording(false);
    };

    ws.onerror = () => {
      log.error(() => '[WS] WebSocket error occurred');
      setVoiceStatus('Connection error');
    };
  } catch (error) {
    log.error(() => `[SESSION] Failed to start: ${String(error)}`);
    setVoiceStatus('Connection failed');
  }
}

/**
 * Stop the voice session
 */
export async function stopVoiceSession(): Promise<void> {
  if (!isSessionActive) {
    log.info(() => '[SESSION] Stop called but session not active');
    return;
  }

  log.info(() => '[SESSION] Stopping voice session...');

  // Stop recording
  if (window.stopRecording) {
    log.info(() => '[AUDIO] Calling stopRecording()');
    await window.stopRecording();
    log.info(() => '[AUDIO] stopRecording() completed');
  }

  // Stop playback
  if (window.stopAudioPlayback) {
    log.info(() => '[AUDIO] Calling stopAudioPlayback()');
    await window.stopAudioPlayback();
  }

  // Send stop message and close WebSocket
  if (ws && ws.readyState === WebSocket.OPEN) {
    const stopMsg = JSON.stringify({ type: 'stop' });
    log.info(() => `[WS] Sending: ${stopMsg}`);
    ws.send(stopMsg);
    log.info(() => '[WS] Closing WebSocket');
    ws.close();
  }

  isSessionActive = false;
  finalizeToolGroup();
  setVoiceStatus('Ready');
  setToggleRecording(false);
  log.info(() => '[SESSION] Voice session stopped');
}

/** Voice message from server */
interface VoiceMessage {
  type: string;
  status?: string;
  message?: string;
  role?: 'user' | 'assistant' | 'tool';
  content?: string;
  toolName?: string;
  timestamp?: string;
  requestId?: string;
  tool?: VoiceToolName;
  args?: Record<string, unknown>;
  result?: unknown;
  requiresConfirmation?: boolean;
}

function addVoiceChatMessage(msg: VoiceMessage): void {
  if (!msg.role || msg.content === undefined) {
    return;
  }

  const chatMsg = {
    role: msg.role,
    content: msg.content,
    timestamp: msg.timestamp || new Date().toISOString(),
  };
  if (msg.toolName) {
    addChatMessage({ ...chatMsg, toolName: msg.toolName });
    return;
  }

  addChatMessage(chatMsg);
}

function clearPendingVoiceAudio(): void {
  if (window.stopAudioPlayback) {
    void window.stopAudioPlayback();
  }
}

function handleVoiceToolRequestMessage(msg: VoiceMessage): void {
  if (msg.requestId && msg.tool) {
    void handleToolRequest(
      msg.requestId,
      msg.tool,
      msg.args ?? {},
      msg.requiresConfirmation ?? false,
    );
  }
}

function handleRealtimeTraceMessage(msg: VoiceMessage): void {
  addChatMessage({
    role: 'tool',
    toolName: 'realtime_trace',
    content:
      typeof msg.result === 'undefined'
        ? msg.message || 'Realtime response completed.'
        : JSON.stringify(msg.result, null, 2),
    timestamp: msg.timestamp || new Date().toISOString(),
  });
}

/**
 * Handle messages from the voice server
 */
function handleVoiceMessage(msg: VoiceMessage): void {
  log.info(() => `[MSG] Handling: type=${msg.type}`);
  switch (msg.type) {
    case 'status':
      if (msg.status) {
        setVoiceStatus(msg.status);
      }
      break;
    case 'speaking':
      setVoiceStatus('Speaking...');
      break;
    case 'listening':
      setVoiceStatus('Listening...');
      break;
    case 'chat':
      addVoiceChatMessage(msg);
      break;
    case 'clear_audio':
      clearPendingVoiceAudio();
      break;
    case 'error':
      log.error(() => `[MSG] Server error: ${msg.message || 'unknown'}`);
      setVoiceStatus('Server error');
      break;
    case 'tool_request':
      handleVoiceToolRequestMessage(msg);
      break;
    case 'realtime_trace':
      handleRealtimeTraceMessage(msg);
      break;
    default:
      log.info(() => `[MSG] Unhandled message type: ${msg.type}`);
  }
}

/**
 * Handle a tool request from the voice server
 */
async function handleToolRequest(
  requestId: string,
  tool: VoiceToolName,
  args: Record<string, unknown>,
  requiresConfirmation: boolean,
): Promise<void> {
  log.info(
    () => `[TOOL] Processing request: ${tool} (${requestId}), confirmation=${requiresConfirmation}`,
  );

  // If confirmation is required, show dialog first
  if (requiresConfirmation) {
    const justification = args.justification as string | undefined;
    const approved = await showToolConfirmation(tool, args, justification);

    if (!approved) {
      log.info(() => `[TOOL] User declined: ${tool} (${requestId})`);
      sendToolResponse({
        type: 'tool_response',
        requestId,
        result: null,
        declined: true,
      });
      return;
    }
  }

  const response = await processToolRequest({
    type: 'tool_request',
    requestId,
    tool,
    args,
  });

  sendToolResponse(response);
}

/**
 * Send a tool response back to the voice server
 */
function sendToolResponse(response: {
  type: string;
  requestId: string;
  result: unknown;
  error?: string;
  declined?: boolean;
}): void {
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    log.warn(() => `[TOOL] Cannot send response - WebSocket not open`);
    return;
  }

  const json = JSON.stringify(response);
  ws.send(json);
  log.info(
    () => `[TOOL] Sent response for ${response.requestId}${response.declined ? ' (declined)' : ''}`,
  );
}

/**
 * Convert base64 string to ArrayBuffer
 */
function base64ToArrayBuffer(base64: string): ArrayBuffer {
  const binaryString = atob(base64);
  const bytes = new Uint8Array(binaryString.length);
  for (let i = 0; i < binaryString.length; i++) {
    bytes[i] = binaryString.charCodeAt(i);
  }
  return bytes.buffer;
}

/**
 * Convert ArrayBuffer to base64 string
 */
function arrayBufferToBase64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let i = 0; i < bytes.byteLength; i++) {
    const byte = bytes[i];
    if (byte === undefined) break;
    binary += String.fromCharCode(byte);
  }
  return btoa(binary);
}

/**
 * Test voice server connection and show diagnostic popup
 */
async function testVoiceServerConnection(): Promise<void> {
  const healthUrl = `${VOICE_SERVER_URL}/api/health`;

  const results: string[] = [];
  results.push(`Voice Server: ${VOICE_SERVER_URL}`);
  results.push('');

  try {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => {
      controller.abort();
    }, 5000);

    const response = await fetch(healthUrl, { signal: controller.signal });
    clearTimeout(timeoutId);

    if (response.ok) {
      const data = (await response.json()) as VoiceHealthResponse;
      results.push('✓ Health endpoint: OK');
      results.push(`  Version: ${data.version}`);

      if (data.providers && Array.isArray(data.providers)) {
        results.push('');
        results.push('Providers:');
        for (const p of data.providers) {
          const status = p.available ? '✓' : '✗';
          const voiceCount = p.voices.length;
          results.push(`  ${status} ${p.name}: ${voiceCount} voices`);
        }
      }

      if (data.defaults) {
        results.push('');
        results.push(`Defaults: ${data.defaults.provider}/${data.defaults.voice}`);
      }

      if (data.providers) {
        voiceProviders = data.providers;
        populateVoiceDropdown();
      }
    } else {
      results.push(`✗ Health endpoint: HTTP ${response.status}`);
    }
  } catch (err) {
    if (err instanceof Error) {
      if (err.name === 'AbortError') {
        results.push('✗ Health endpoint: Timeout (5s)');
      } else {
        results.push(`✗ Health endpoint: ${err.message}`);
      }
    } else {
      results.push('✗ Health endpoint: Unknown error');
    }
    results.push('');
    results.push('Possible issues:');
    results.push('  - Voice server not running');
    results.push('  - HTTPS certificate not trusted');
    results.push('  - CORS blocked');
    results.push('');
    results.push(`Try visiting: ${healthUrl}`);
  }

  void showAlert(results.join('\n'), { title: 'Voice Diagnostics' });
}

/**
 * Bind voice button event handlers
 */
export function bindVoiceEvents(): void {
  const toggleBtn = document.getElementById('btn-voice-toggle');
  const voiceSelect = document.getElementById('voice-select') as HTMLSelectElement | null;
  const micSelect = document.getElementById('mic-select') as HTMLSelectElement | null;
  const speedSlider = document.getElementById('voice-speed') as HTMLInputElement | null;

  log.info(() => `[INIT] Binding voice events: toggleBtn=${!!toggleBtn}`);

  if (toggleBtn) {
    toggleBtn.addEventListener('click', () => {
      void (async () => {
        log.info(() => `[UI] Toggle button clicked (isSessionActive=${isSessionActive})`);
        if (isSessionActive) {
          await stopVoiceSession();
        } else {
          await startVoiceSession();
        }
      })();
    });
  }

  // Sync button (dev mode only) - test voice server connection
  const syncBtn = document.getElementById('btn-voice-sync');
  if (syncBtn) {
    syncBtn.addEventListener('click', () => {
      void testVoiceServerConnection();
    });
  }

  // Chat toggle button
  const chatBtn = document.getElementById('btn-voice-chat');
  if (chatBtn) {
    chatBtn.addEventListener('click', () => {
      toggleChatPanel();
    });
  }

  // Voice selection change
  if (voiceSelect) {
    voiceSelect.addEventListener('change', () => {
      const value = voiceSelect.value;
      if (value.includes(':')) {
        const parts = value.split(':');
        selectedProvider = parts[0] ?? '';
        selectedVoice = parts[1] ?? '';
        log.info(() => `[UI] Voice changed: ${selectedProvider}/${selectedVoice}`);
      }
    });
  }

  // Microphone dropdown focus - request permission if empty
  if (micSelect) {
    micSelect.addEventListener('focus', () => {
      void (async () => {
        const hasDevices =
          micSelect.options.length > 1 ||
          (micSelect.options.length === 1 && micSelect.options[0]?.value !== '');
        if (!hasDevices) {
          log.info(() => '[UI] Mic dropdown focused with no devices, requesting permission');
          const success = await requestMicrophonePermission();
          if (success) {
            await populateMicDropdown();
          }
        }
      })();
    });

    // Microphone selection change (stored for next recording)
    micSelect.addEventListener('change', () => {
      log.info(() => `[UI] Microphone changed: ${micSelect.value || 'default'}`);
    });
  }

  // Speed slider change
  if (speedSlider) {
    speedSlider.addEventListener('input', () => {
      selectedSpeed = parseFloat(speedSlider.value);
      const speedValue = document.getElementById('voice-speed-value');
      if (speedValue) {
        speedValue.textContent = `${selectedSpeed}x`;
      }
    });
  }

  // Set up error callback
  if (window.setOnError) {
    window.setOnError((error: string) => {
      log.error(() => `[AUDIO] Error callback: ${error}`);
      setVoiceStatus('Error');
    });
  }

  // Set up recording state callback
  if (window.setOnRecordingState) {
    window.setOnRecordingState((isRecording: boolean) => {
      log.info(() => `[AUDIO] Recording state changed: ${isRecording}`);
    });
  }
}

/**
 * Initialize voice controls on page load.
 * Passively checks permission status and pre-populates mic dropdown if granted.
 */
export async function initVoiceControls(): Promise<void> {
  const status = await checkMicrophonePermissionStatus();
  log.info(() => `[INIT] Microphone permission status: ${status}`);

  if (status === 'granted') {
    await populateMicDropdownPassive();
    setVoiceStatus('Ready');
  } else if (status === 'denied') {
    setVoiceStatus('Mic blocked');
  } else {
    setVoiceStatus('Click Play to start');
  }
}
