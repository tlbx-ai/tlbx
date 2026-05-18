/**
 * Type extensions for xterm.js internal properties and non-standard CSS.
 *
 * These declarations extend the official xterm.js types to include internal
 * properties we access (like Terminal.modes) and non-standard CSS properties
 * (like CSSStyleDeclaration.zoom).
 */

import type { TerminalState, MidTermSettingsPublic } from '../types';

// Extend Terminal with internal properties we access
declare module '@xterm/xterm' {
  interface Terminal {
    modes?: {
      bracketedPasteMode?: boolean;
    };
  }
}

// Extend CSSStyleDeclaration with non-standard zoom property
declare global {
  interface CSSStyleDeclaration {
    zoom: string;
  }

  interface Window {
    __MIDTERM_XTERM_WEBGL_CELL_BG_ALPHA__?: number;
    __MIDTERM_XTERM_BOX_DRAWING_STROKE_SCALE__?: number;
    __MIDTERM_XTERM_BOX_DRAWING_STYLE__?: string;
    mmDebug?: {
      readonly terminals: Map<string, TerminalState>;
      readonly activeId: string | null;
      readonly settings: MidTermSettingsPublic | null;
      readonly layout: {
        dock: (
          targetSessionId: string,
          draggedSessionId: string,
          position: 'left' | 'right' | 'top' | 'bottom',
        ) => void;
        focus: (sessionId: string) => void;
        readonly sessions: string[];
        isSessionInLayout: (sessionId: string) => boolean;
        readonly rootVisible: boolean;
      };
      readonly appServerControl: {
        readonly scenarios: readonly ('mixed' | 'tables' | 'long' | 'massive' | 'workflow')[];
        showScenario: (
          sessionId: string,
          scenario?: 'mixed' | 'tables' | 'long' | 'massive' | 'workflow',
        ) => Promise<boolean>;
      };
    };

    // Voice audio functions from webAudioAccess.js
    initAudioWithUserInteraction?: () => Promise<boolean>;
    requestMicrophonePermissionAndGetDevices?: () => Promise<unknown[]>;
    getAvailableMicrophones?: () => Promise<unknown[]>;
    startRecording?: (
      callback: (base64Audio: string) => void,
      intervalMs?: number,
      deviceId?: string | null,
      targetSampleRate?: number,
    ) => Promise<boolean>;
    stopRecording?: () => Promise<void>;
    playAudio?: (base64Audio: string, sampleRate?: number) => Promise<boolean>;
    stopAudioPlayback?: () => Promise<void>;
    cleanupAudio?: () => void;
    setOnError?: (callback: (error: string) => void) => void;
    setOnRecordingState?: (callback: (isRecording: boolean) => void) => void;
  }
}

export {};
