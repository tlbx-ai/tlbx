// webAudioAccess.js - Adapted for tlbx Voice Assistant
// Simplified version using callbacks instead of DotNet interop

let mediaStream = null;
let mediaStreamSource = null;
let audioWorkletNode = null;
let playbackWorkletNode = null;
let isRecording = false;
let audioContext = null;
let audioInitialized = false;
let audioContextSampleRate = 48000;
let playbackNodeConnected = false;
let onAudioDataCallback = null;
let onRecordingStateCallback = null;
let onErrorCallback = null;

// Debug counters
let audioFrameCounter = 0;
let lastFrameTime = 0;

// Processing nodes
let deEsserNode = null;
let compressorNode = null;
let makeupGainNode = null;

async function loadAudioWorkletModules() {
  if (!audioContext) {
    console.error('Cannot load audio worklet: audioContext is null');
    return false;
  }

  try {
    await audioContext.audioWorklet.addModule('./js/audio-processor.js');
    console.log('AudioWorklet modules loaded successfully');
    return true;
  } catch (err) {
    if (err.message && (err.message.includes('already been added') || err.message.includes('has been already registered'))) {
      console.warn('AudioWorklet module loading warning (likely already loaded):', err.message);
      return true;
    }
    console.error('Failed to load AudioWorklet module:', err);
    return false;
  }
}

async function initAudioWithUserInteraction() {
  try {
    console.log('Initializing audio with user interaction');

    if (!audioContext || audioContext.sampleRate !== audioContextSampleRate) {
      if (audioContext) {
        await audioContext.close();
      }
      audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: audioContextSampleRate });
      console.log('AudioContext created', { sampleRate: audioContext.sampleRate, state: audioContext.state });
    }

    if (audioContext.state === 'suspended') {
      await audioContext.resume();
      console.log('AudioContext resumed', { state: audioContext.state });
    }

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      throw new Error("This browser doesn't support accessing the microphone.");
    }

    if (!await loadAudioWorkletModules()) {
      throw new Error('Failed to load AudioWorklet modules');
    }

    // Setup Playback Worklet Node
    if (playbackWorkletNode) {
      playbackWorkletNode.disconnect();
      playbackWorkletNode = null;
    }

    try {
      playbackWorkletNode = new AudioWorkletNode(audioContext, 'playback-processor');
      playbackWorkletNode.onprocessorerror = (event) => {
        console.error('PlaybackProcessor error:', event);
        onErrorCallback?.('Playback processor error occurred.');
      };
      playbackWorkletNode.connect(audioContext.destination);
      playbackNodeConnected = true;
      console.log('PlaybackProcessor node created and connected.');
    } catch (nodeError) {
      console.error('Failed to create or connect PlaybackProcessor node:', nodeError);
      throw new Error(`Failed to initialize playback processor: ${nodeError.message}`);
    }

    audioInitialized = true;
    console.log('Audio system fully initialized');
    return true;
  } catch (error) {
    console.error('Audio initialization error:', error);
    onErrorCallback?.(`Audio initialization failed: ${error.message}`);
    audioInitialized = false;

    if (playbackWorkletNode) {
      playbackWorkletNode.disconnect();
      playbackWorkletNode = null;
    }
    if (audioContext && audioContext.state !== 'closed') {
      await audioContext.close();
      audioContext = null;
    }
    return false;
  }
}

async function requestMicrophonePermissionAndGetDevices() {
  try {
    console.log('Requesting microphone permission');

    if (!await initAudioWithUserInteraction()) {
      throw new Error('Failed to initialize audio system');
    }

    const tempStream = await navigator.mediaDevices.getUserMedia({
      audio: {
        sampleRate: { ideal: 48000, min: 44100 },
        echoCancellation: false,
        noiseSuppression: false,
        autoGainControl: false
      }
    });

    tempStream.getTracks().forEach(track => track.stop());
    console.log('Microphone permission granted');

    return await getAvailableMicrophones();
  } catch (permErr) {
    if (permErr.name === 'NotAllowedError' || permErr.name === 'PermissionDeniedError') {
      throw new Error('Microphone permission denied.');
    } else if (permErr.name === 'NotFoundError') {
      throw new Error('No microphone detected.');
    } else {
      throw new Error(`Failed to request microphone permission: ${permErr.message}`);
    }
  }
}

async function getAvailableMicrophones() {
  try {
    const devices = await navigator.mediaDevices.enumerateDevices();
    const audioInputs = devices.filter(device => device.kind === 'audioinput');
    return audioInputs.map((device, index) => ({
      id: device.deviceId,
      name: device.label || `Microphone ${device.deviceId.substring(0, 8)}`,
      isDefault: index === 0 || device.deviceId === 'default'
    }));
  } catch (error) {
    console.error('Error getting available microphones:', error);
    return [];
  }
}

async function ensureAudioContextResumed() {
  if (!audioContext) {
    try {
      audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: audioContextSampleRate });
      await loadAudioWorkletModules();
      if (!playbackWorkletNode && audioContext.state !== 'closed') {
        playbackWorkletNode = new AudioWorkletNode(audioContext, 'playback-processor');
        playbackWorkletNode.connect(audioContext.destination);
      }
    } catch (initErr) {
      console.error('Failed to reinitialize AudioContext:', initErr);
      return false;
    }
  }

  if (audioContext.state === 'suspended') {
    try {
      await audioContext.resume();
    } catch (error) {
      console.error('Failed to resume AudioContext:', error);
      return false;
    }
  }

  return audioContext.state === 'running';
}

async function startRecording(callback, intervalMs = 500, deviceId = null, targetSampleRate = 24000) {
  console.log('Attempting to start recording', { intervalMs, deviceId, targetSampleRate });

  onAudioDataCallback = callback;

  if (!(await ensureAudioContextResumed())) {
    console.error('Cannot start recording: AudioContext not running');
    onErrorCallback?.('Cannot start recording: AudioContext is not active.');
    return false;
  }

  if (!audioInitialized) {
    const ok = await initAudioWithUserInteraction();
    if (!ok) {
      onErrorCallback?.('Audio system not initialized.');
      return false;
    }
  }

  if (isRecording) {
    return true;
  }

  try {
    const constraints = {
      audio: {
        channelCount: 1,
        sampleRate: { ideal: 48000, min: 44100 },
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: true,
        ...(deviceId && { deviceId: { exact: deviceId } })
      },
      video: false
    };

    mediaStream = await navigator.mediaDevices.getUserMedia(constraints);
    console.log('Media stream obtained', { active: mediaStream.active });

    // Clean up existing nodes
    if (audioWorkletNode) {
      audioWorkletNode.disconnect();
      audioWorkletNode = null;
    }
    if (deEsserNode) {
      deEsserNode.disconnect();
      deEsserNode = null;
    }
    if (compressorNode) {
      compressorNode.disconnect();
      compressorNode = null;
    }
    if (makeupGainNode) {
      makeupGainNode.disconnect();
      makeupGainNode = null;
    }

    // Create recorder worklet
    audioWorkletNode = new AudioWorkletNode(audioContext, 'audio-recorder-processor', {
      processorOptions: {
        targetSampleRate: targetSampleRate,
        captureSampleRate: audioContextSampleRate
      }
    });

    audioWorkletNode.onprocessorerror = (e) => {
      console.error('Recorder processor error:', e);
    };

    // Create processing chain
    deEsserNode = audioContext.createBiquadFilter();
    deEsserNode.type = 'highshelf';
    deEsserNode.frequency.value = 5500;
    deEsserNode.gain.value = -4;

    makeupGainNode = audioContext.createGain();
    makeupGainNode.gain.value = 1.5;

    compressorNode = audioContext.createDynamicsCompressor();
    compressorNode.threshold.value = -18;
    compressorNode.knee.value = 6;
    compressorNode.ratio.value = 8;
    compressorNode.attack.value = 0.002;
    compressorNode.release.value = 0.1;

    // Setup message handling
    audioFrameCounter = 0;
    lastFrameTime = performance.now();
    audioWorkletNode.port.onmessage = (event) => {
      if (event.data.audioData) {
        const pcm16Data = event.data.audioData;
        const buffer = pcm16Data.buffer;
        const bytes = new Uint8Array(buffer);

        audioFrameCounter++;
        const now = performance.now();
        const elapsed = now - lastFrameTime;
        lastFrameTime = now;

        // Only log every 10th frame to reduce console spam
        if (audioFrameCounter % 10 === 0) {
          console.log(`[WebAudio] Frame #${audioFrameCounter}: ${bytes.byteLength} bytes, ${elapsed.toFixed(0)}ms avg`);
        }

        let binary = '';
        for (let i = 0; i < bytes.byteLength; i++) {
          binary += String.fromCharCode(bytes[i]);
        }
        const base64Audio = btoa(binary);

        if (onAudioDataCallback) {
          onAudioDataCallback(base64Audio);
        } else {
          console.warn('[WebAudio] No callback registered for audio data!');
        }
      }
    };

    mediaStreamSource = audioContext.createMediaStreamSource(mediaStream);

    // Connect chain: source -> de-esser -> gain -> compressor -> recorder
    mediaStreamSource.connect(deEsserNode);
    deEsserNode.connect(makeupGainNode);
    makeupGainNode.connect(compressorNode);
    compressorNode.connect(audioWorkletNode);

    isRecording = true;
    console.log('Recording started successfully');
    onRecordingStateCallback?.(true);
    return true;
  } catch (error) {
    console.error('Error starting recording:', error);
    isRecording = false;
    onRecordingStateCallback?.(false);
    onErrorCallback?.(`Failed to start recording: ${error.message}`);

    if (mediaStream) {
      mediaStream.getTracks().forEach(track => track.stop());
      mediaStream = null;
    }
    return false;
  }
}

async function stopRecording() {
  console.log(`[WebAudio] Stopping recording (processed ${audioFrameCounter} frames)`);
  if (!isRecording) {
    console.warn('Recording not in progress');
    return;
  }

  isRecording = false;

  if (mediaStreamSource) {
    try {
      mediaStreamSource.disconnect();
    } catch (e) { }
  }

  if (deEsserNode) {
    try { deEsserNode.disconnect(); } catch (e) { }
    deEsserNode = null;
  }
  if (compressorNode) {
    try { compressorNode.disconnect(); } catch (e) { }
    compressorNode = null;
  }
  if (makeupGainNode) {
    try { makeupGainNode.disconnect(); } catch (e) { }
    makeupGainNode = null;
  }

  if (audioWorkletNode) {
    try {
      const stopPromise = new Promise((resolve) => {
        const originalHandler = audioWorkletNode.port.onmessage;
        audioWorkletNode.port.onmessage = (event) => {
          if (originalHandler && event.data.audioData) {
            originalHandler(event);
          }
          if (event.data.stopped) {
            resolve();
          }
        };
        audioWorkletNode.port.postMessage({ command: 'stop' });
        setTimeout(resolve, 200);
      });
      await stopPromise;
      audioWorkletNode.disconnect();
      audioWorkletNode = null;
    } catch (e) { }
  }

  await new Promise(resolve => setTimeout(resolve, 100));

  if (mediaStream) {
    const tracks = mediaStream.getTracks();
    tracks.forEach(track => { track.enabled = false; });
    await new Promise(resolve => setTimeout(resolve, 150));
    tracks.forEach(track => { try { track.stop(); } catch (e) { } });
    mediaStream = null;
    mediaStreamSource = null;
  }

  console.log('Recording stopped successfully');
  onRecordingStateCallback?.(false);
}

function pcm16Base64ToFloat32(base64Audio) {
  try {
    const binaryString = atob(base64Audio);
    const len = binaryString.length;
    const bytes = new Uint8Array(len);
    for (let i = 0; i < len; i++) {
      bytes[i] = binaryString.charCodeAt(i);
    }
    const pcm16 = new Int16Array(bytes.buffer);
    const float32 = new Float32Array(pcm16.length);
    for (let i = 0; i < pcm16.length; i++) {
      float32[i] = pcm16[i] / 32768.0;
    }
    return float32;
  } catch (error) {
    console.error('Error decoding/converting Base64 PCM16 audio:', error);
    return null;
  }
}

async function playAudio(base64Audio, sampleRate = 24000) {
  if (!(await ensureAudioContextResumed())) {
    console.error('Cannot play audio: AudioContext not running');
    return false;
  }

  if (!playbackWorkletNode) {
    console.error('Cannot play audio: Playback worklet node not initialized');
    return false;
  }

  const float32Audio = pcm16Base64ToFloat32(base64Audio);
  if (!float32Audio) {
    console.error('Failed to decode audio data');
    return false;
  }

  try {
    playbackWorkletNode.port.postMessage({ audioData: float32Audio.buffer }, [float32Audio.buffer]);
    return true;
  } catch (error) {
    console.error('Error sending audio data to PlaybackProcessor:', error);
    return false;
  }
}

async function stopAudioPlayback() {
  console.log('Stopping audio playback');

  if (!audioContext || !playbackWorkletNode) {
    return;
  }

  try {
    playbackWorkletNode.port.postMessage({ command: 'clear' });
    playbackWorkletNode.disconnect();
    playbackNodeConnected = false;

    setTimeout(() => {
      if (playbackWorkletNode && audioContext && audioContext.state === 'running') {
        try {
          playbackWorkletNode.connect(audioContext.destination);
          playbackNodeConnected = true;
        } catch (e) { }
      }
    }, 50);
  } catch (error) {
    console.error('Error stopping playback:', error);
  }
}

function setOnError(callback) {
  onErrorCallback = callback;
}

function setOnRecordingState(callback) {
  onRecordingStateCallback = callback;
}

function cleanupAudio() {
  console.log('Cleaning up audio resources');
  stopRecording();
  stopAudioPlayback();

  if (playbackWorkletNode) {
    playbackWorkletNode.disconnect();
    playbackWorkletNode = null;
  }
  if (audioWorkletNode) {
    audioWorkletNode.disconnect();
    audioWorkletNode = null;
  }
  if (audioContext && audioContext.state !== 'closed') {
    audioContext.close();
    audioContext = null;
  }
  audioInitialized = false;
  playbackNodeConnected = false;
}

// Attach to window for global access
window.initAudioWithUserInteraction = initAudioWithUserInteraction;
window.requestMicrophonePermissionAndGetDevices = requestMicrophonePermissionAndGetDevices;
window.getAvailableMicrophones = getAvailableMicrophones;
window.startRecording = startRecording;
window.stopRecording = stopRecording;
window.playAudio = playAudio;
window.stopAudioPlayback = stopAudioPlayback;
window.cleanupAudio = cleanupAudio;
window.setOnError = setOnError;
window.setOnRecordingState = setOnRecordingState;

// Cleanup on page unload
window.addEventListener('beforeunload', () => {
  if (isRecording) {
    stopRecording();
  }
  cleanupAudio();
});
