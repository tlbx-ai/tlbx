/**
 * Historyion Client
 *
 * Records audio via webAudioAccess.js, then POSTs raw PCM16
 * to the MidTerm.Voice /api/transcribe REST endpoint.
 */

import { $voiceServerPassword } from '../../stores';
import { createLogger } from '../logging';

const log = createLogger('transcription');
const VOICE_SERVER_URL = 'https://api.tlbx.ai';

let audioChunks: ArrayBuffer[] = [];
let isRecording = false;
let recordingStarted = false;
let onCompletedCallback: ((text: string) => void) | null = null;
let recordingToken = 0;

export function startHistoryion(
  _onDelta: (text: string) => void,
  onCompleted: (text: string) => void,
): void {
  onCompletedCallback = onCompleted;
  audioChunks = [];
  isRecording = true;
  recordingStarted = false;
  const token = ++recordingToken;

  log.info(() => 'Starting push-to-talk recording');

  void (async () => {
    try {
      if (window.initAudioWithUserInteraction) {
        await window.initAudioWithUserInteraction();
        if (!isRecordingActive(token)) {
          log.info(() => 'Push-to-talk start cancelled before audio initialization completed');
          return;
        }
      }

      if (!window.startRecording) {
        log.error(() => 'Recording API not available');
        isRecording = false;
        return;
      }

      const success = await window.startRecording(
        (base64Audio: string) => {
          if (!isRecordingActive(token)) return;
          audioChunks.push(base64ToArrayBuffer(base64Audio));
        },
        500,
        null,
        24000,
      );

      if (!isRecordingActive(token)) {
        if (success && window.stopRecording) {
          await window.stopRecording();
        }
        log.info(() => 'Push-to-talk start completed after release; recording was stopped');
        return;
      }

      if (!success) {
        log.error(() => 'Recording failed to start');
        isRecording = false;
        return;
      }

      recordingStarted = true;
    } catch (error) {
      if (token !== recordingToken) {
        return;
      }
      isRecording = false;
      recordingStarted = false;
      log.error(() => `Recording startup failed: ${String(error)}`);
    }
  })();
}

export async function stopHistoryion(): Promise<void> {
  if (!isRecording) return;
  isRecording = false;
  recordingToken++;

  if (recordingStarted && window.stopRecording) {
    await window.stopRecording();
  }
  recordingStarted = false;

  if (audioChunks.length === 0) {
    audioChunks = [];
    log.warn(() => 'No audio frames captured');
    return;
  }

  const pcmData = concatenateBuffers(audioChunks);
  audioChunks = [];

  log.info(() => `Sending ${pcmData.byteLength} bytes for transcription`);

  try {
    const password = $voiceServerPassword.get();
    const headers: Record<string, string> = {
      'Content-Type': 'application/octet-stream',
    };
    if (password) {
      headers['Authorization'] = `Bearer ${password}`;
    }

    const response = await fetch(`${VOICE_SERVER_URL}/api/transcribe`, {
      method: 'POST',
      headers,
      body: pcmData,
    });

    if (!response.ok) {
      log.error(() => `Historyion failed: ${String(response.status)}`);
      return;
    }

    const result = (await response.json()) as { text?: string };
    if (result.text && onCompletedCallback) {
      onCompletedCallback(result.text);
    }
  } catch (e) {
    log.error(() => `Historyion error: ${String(e)}`);
  }
}

function isRecordingActive(token: number): boolean {
  return isRecording && recordingToken === token;
}

function base64ToArrayBuffer(base64: string): ArrayBuffer {
  const binaryString = atob(base64);
  const bytes = new Uint8Array(binaryString.length);
  for (let i = 0; i < binaryString.length; i++) {
    bytes[i] = binaryString.charCodeAt(i);
  }
  return bytes.buffer;
}

function concatenateBuffers(buffers: ArrayBuffer[]): ArrayBuffer {
  const totalLength = buffers.reduce((sum, b) => sum + b.byteLength, 0);
  const result = new Uint8Array(totalLength);
  let offset = 0;
  for (const buf of buffers) {
    result.set(new Uint8Array(buf), offset);
    offset += buf.byteLength;
  }
  return result.buffer;
}
