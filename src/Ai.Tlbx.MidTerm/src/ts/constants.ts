/**
 * Constants
 *
 * Protocol constants, theme definitions, and configuration values.
 */

import type { TerminalTheme } from './types';

// =============================================================================
// Build Version (injected at compile time via esbuild --define)
// =============================================================================

/** Version injected at build time - DO NOT MODIFY, replaced by esbuild */
declare const BUILD_VERSION: string;
declare const BUILD_ASSET_VERSION: string;

/** The version this JavaScript was compiled for */
export const JS_BUILD_VERSION: string =
  typeof BUILD_VERSION !== 'undefined' ? BUILD_VERSION : 'dev';

/** Fingerprint for cache-busting static assets without forcing app version bumps */
export const ASSET_VERSION: string =
  typeof BUILD_ASSET_VERSION !== 'undefined' ? BUILD_ASSET_VERSION : JS_BUILD_VERSION;

// =============================================================================
// Mux Protocol Constants
// =============================================================================

/** Mux protocol header size (1 byte type + 8 byte session ID) */
export const MUX_HEADER_SIZE = 9;

/** Mux protocol version - increment when making breaking protocol changes */
export const MUX_PROTOCOL_VERSION = 2;

/** Minimum compatible protocol version */
export const MUX_MIN_COMPATIBLE_VERSION = 1;

/** Mux protocol message types */
export const MUX_TYPE_OUTPUT = 0x01; // Server -> Client: Terminal output (includes dimensions)
export const MUX_TYPE_INPUT = 0x02; // Client -> Server: Terminal input
export const MUX_TYPE_RESIZE = 0x03; // Client -> Server: Terminal resize
export const MUX_TYPE_RESYNC = 0x05; // Server -> Client: Clear terminals, buffer refresh follows
export const MUX_TYPE_BUFFER_REQUEST = 0x06; // Client -> Server: Request buffer refresh
export const MUX_TYPE_COMPRESSED_OUTPUT = 0x07; // Server -> Client: GZip compressed output
export const MUX_TYPE_ACTIVE_HINT = 0x08; // Client -> Server: Hint which session is active
export const MUX_TYPE_PING = 0x09; // Client -> Server: Latency measurement ping
export const MUX_TYPE_FOREGROUND_CHANGE = 0x0a; // Server -> Client: Foreground process changed
export const MUX_TYPE_DATA_LOSS = 0x0b; // Server -> Client: Background session lost data
export const MUX_TYPE_PONG = 0x0c; // Server -> Client: Latency measurement pong
export const MUX_TYPE_SYNC_COMPLETE = 0x0d; // Server -> Client: Initial buffer replay finished
export const MUX_TYPE_VISIBLE_SESSIONS_HINT = 0x0e; // Client -> Server: Visible terminal sessions
export const MUX_TYPE_INPUT_TRACE_MARKER = 0x0f; // Client -> Server: Sample next input for latency trace
export const MUX_TYPE_INPUT_TRACE_RESULT = 0x10; // Server -> Client: Sampled input latency trace result
export const MUX_TYPE_RECOVERY_BEGIN = 0x11; // Server -> Client: Per-session recovery transaction starts
export const MUX_TYPE_RECOVERY_END = 0x12; // Server -> Client: Per-session recovery transaction commits

// Custom WebSocket close codes (4000-4999 range)
export const WS_CLOSE_AUTH_FAILED = 4401;
export const WS_CLOSE_SERVER_SHUTDOWN = 4503;
export const WS_CLOSE_PROTOCOL_ERROR = 4400;

// =============================================================================
// Terminal Themes
// =============================================================================

/** Terminal color themes (keyed by ThemeName or xterm-only scheme names like 'matrix') */
export const THEMES: Record<string, TerminalTheme> = {
  dark: {
    background: '#0C0C0C',
    foreground: '#F2F2F2',
    cursor: '#F2F2F2',
    cursorAccent: '#0C0C0C',
    selectionBackground: '#2D3044',
    scrollbarSliderBackground: 'rgba(58, 62, 82, 0.5)',
    scrollbarSliderHoverBackground: 'rgba(123, 162, 247, 0.5)',
    scrollbarSliderActiveBackground: 'rgba(123, 162, 247, 0.7)',
    black: '#0C0C0C',
    red: '#FF4055',
    green: '#32E03B',
    yellow: '#FFCC00',
    blue: '#2B65FF',
    magenta: '#C73DFF',
    cyan: '#35CFFF',
    white: '#5ABEFF',
    brightBlack: '#767676',
    brightRed: '#FF6B7D',
    brightGreen: '#68FF68',
    brightYellow: '#FFF59A',
    brightBlue: '#7DA6FF',
    brightMagenta: '#E667FF',
    brightCyan: '#7AF7FF',
    brightWhite: '#F2F2F2',
  },
  dark2: {
    background: '#000000',
    foreground: '#FFFFFF',
    cursor: '#FFFFFF',
    cursorAccent: '#000000',
    selectionBackground: '#333333',
    scrollbarSliderBackground: 'rgba(255, 255, 255, 0.22)',
    scrollbarSliderHoverBackground: 'rgba(255, 255, 255, 0.38)',
    scrollbarSliderActiveBackground: 'rgba(255, 255, 255, 0.5)',
    black: '#000000',
    red: '#FF0000',
    green: '#00FF00',
    yellow: '#FFFF00',
    blue: '#0000FF',
    magenta: '#FF00FF',
    cyan: '#00FFFF',
    white: '#FFFFFF',
    brightBlack: '#808080',
    brightRed: '#FF0000',
    brightGreen: '#00FF00',
    brightYellow: '#FFFF00',
    brightBlue: '#0000FF',
    brightMagenta: '#FF00FF',
    brightCyan: '#00FFFF',
    brightWhite: '#FFFFFF',
  },
  light: {
    background: '#F5F0E8',
    foreground: '#2E2720',
    cursor: '#2E2720',
    cursorAccent: '#F5F0E8',
    selectionBackground: '#D9CBBA',
    scrollbarSliderBackground: 'rgba(46, 39, 32, 0.15)',
    scrollbarSliderHoverBackground: 'rgba(46, 39, 32, 0.3)',
    scrollbarSliderActiveBackground: 'rgba(46, 39, 32, 0.45)',
    black: '#2C2620',
    red: '#B8433F',
    green: '#4E8A2E',
    yellow: '#8B6914',
    blue: '#3668A6',
    magenta: '#9244A8',
    cyan: '#1A7E7E',
    white: '#706862',
    brightBlack: '#6A6358',
    brightRed: '#D05040',
    brightGreen: '#5CA030',
    brightYellow: '#A07010',
    brightBlue: '#4878B8',
    brightMagenta: '#A050B0',
    brightCyan: '#287878',
    brightWhite: '#584F48',
  },
  campbell: {
    background: '#0C0C0C',
    foreground: '#CCCCCC',
    cursor: '#FFFFFF',
    cursorAccent: '#0C0C0C',
    selectionBackground: '#264F78',
    scrollbarSliderBackground: 'rgba(204, 204, 204, 0.22)',
    scrollbarSliderHoverBackground: 'rgba(204, 204, 204, 0.38)',
    scrollbarSliderActiveBackground: 'rgba(204, 204, 204, 0.5)',
    black: '#0C0C0C',
    red: '#C50F1F',
    green: '#13A10E',
    yellow: '#C19C00',
    blue: '#0037DA',
    magenta: '#881798',
    cyan: '#3A96DD',
    white: '#CCCCCC',
    brightBlack: '#767676',
    brightRed: '#E74856',
    brightGreen: '#16C60C',
    brightYellow: '#F9F1A5',
    brightBlue: '#3B78FF',
    brightMagenta: '#B4009E',
    brightCyan: '#61D6D6',
    brightWhite: '#F2F2F2',
  },
  macTerminalDark: {
    background: '#000000',
    foreground: '#FFFFFF',
    cursor: '#BFBFBF',
    cursorAccent: '#000000',
    selectionBackground: '#333333',
    scrollbarSliderBackground: 'rgba(191, 191, 191, 0.22)',
    scrollbarSliderHoverBackground: 'rgba(191, 191, 191, 0.38)',
    scrollbarSliderActiveBackground: 'rgba(191, 191, 191, 0.5)',
    black: '#000000',
    red: '#C65339',
    green: '#6AC44B',
    yellow: '#B8B74A',
    blue: '#6444ED',
    magenta: '#D357DB',
    cyan: '#69C1CF',
    white: '#FFFFFF',
    brightBlack: '#666666',
    brightRed: '#EB5A3A',
    brightGreen: '#77EA51',
    brightYellow: '#EFEF53',
    brightBlue: '#D09AF9',
    brightMagenta: '#EB5AF7',
    brightCyan: '#78F1F2',
    brightWhite: '#E5E5E5',
  },
  macTerminalLight: {
    background: '#FFFFFF',
    foreground: '#000000',
    cursor: '#7F7F7F',
    cursorAccent: '#FFFFFF',
    selectionBackground: '#D9D9D9',
    scrollbarSliderBackground: 'rgba(0, 0, 0, 0.18)',
    scrollbarSliderHoverBackground: 'rgba(0, 0, 0, 0.3)',
    scrollbarSliderActiveBackground: 'rgba(0, 0, 0, 0.42)',
    black: '#000000',
    red: '#990000',
    green: '#00A600',
    yellow: '#999900',
    blue: '#0000B2',
    magenta: '#B200B2',
    cyan: '#00A6B2',
    white: '#BFBFBF',
    brightBlack: '#666666',
    brightRed: '#E50000',
    brightGreen: '#00D900',
    brightYellow: '#BFBF00',
    brightBlue: '#0000FF',
    brightMagenta: '#E500E5',
    brightCyan: '#00D8D8',
    brightWhite: '#E5E5E5',
  },
  solarizedDark: {
    background: '#002B36',
    foreground: '#839496',
    cursor: '#839496',
    cursorAccent: '#002B36',
    selectionBackground: '#0D4A58',
    scrollbarSliderBackground: 'rgba(131, 148, 150, 0.3)',
    scrollbarSliderHoverBackground: 'rgba(131, 148, 150, 0.5)',
    scrollbarSliderActiveBackground: 'rgba(131, 148, 150, 0.7)',
    black: '#073642',
    red: '#DC322F',
    green: '#859900',
    yellow: '#B58900',
    blue: '#268BD2',
    magenta: '#D33682',
    cyan: '#2AA198',
    white: '#EEE8D5',
    brightBlack: '#586E75',
    brightRed: '#CB4B16',
    brightGreen: '#A4BD00',
    brightYellow: '#D4A017',
    brightBlue: '#54A3D8',
    brightMagenta: '#6C71C4',
    brightCyan: '#54BDB2',
    brightWhite: '#FDF6E3',
  },
  solarizedLight: {
    background: '#FDF6E3',
    foreground: '#657B83',
    cursor: '#657B83',
    cursorAccent: '#FDF6E3',
    selectionBackground: '#D3E5ED',
    scrollbarSliderBackground: 'rgba(101, 123, 131, 0.3)',
    scrollbarSliderHoverBackground: 'rgba(101, 123, 131, 0.5)',
    scrollbarSliderActiveBackground: 'rgba(101, 123, 131, 0.7)',
    black: '#073642',
    red: '#DC322F',
    green: '#859900',
    yellow: '#B58900',
    blue: '#268BD2',
    magenta: '#D33682',
    cyan: '#2AA198',
    white: '#EEE8D5',
    brightBlack: '#586E75',
    brightRed: '#CB4B16',
    brightGreen: '#6C8A00',
    brightYellow: '#946D00',
    brightBlue: '#1B7FC4',
    brightMagenta: '#6C71C4',
    brightCyan: '#1F8E85',
    brightWhite: '#FDF6E3',
  },
  matrix: {
    background: '#0A0A0A',
    foreground: '#00FF41',
    cursor: '#00FF41',
    cursorAccent: '#0A0A0A',
    selectionBackground: '#0A3A0A',
    scrollbarSliderBackground: 'rgba(0, 255, 65, 0.2)',
    scrollbarSliderHoverBackground: 'rgba(0, 255, 65, 0.4)',
    scrollbarSliderActiveBackground: 'rgba(0, 255, 65, 0.6)',
    black: '#0D1A0D',
    red: '#00AA33',
    green: '#00FF41',
    yellow: '#55FF55',
    blue: '#00CC55',
    magenta: '#33DD77',
    cyan: '#00EE66',
    white: '#88FFAA',
    brightBlack: '#339944',
    brightRed: '#33DD55',
    brightGreen: '#66FF88',
    brightYellow: '#99FF99',
    brightBlue: '#44EE77',
    brightMagenta: '#77FFAA',
    brightCyan: '#55FF88',
    brightWhite: '#BBFFCC',
  },
};

// =============================================================================
// Default Settings
// =============================================================================

/** Default terminal settings */
export const DEFAULT_SETTINGS = {
  fontSize: 14,
  scrollbackLines: 2000,
  scrollbackBytes: 2 * 1024 * 1024,
  cursorStyle: 'block' as const,
  cursorBlink: false,
  theme: 'dark' as const,
  bellStyle: 'notification' as const,
  copyOnSelect: false,
  rightClickPaste: true,
  clipboardShortcuts: 'auto' as const,
  terminalEnterMode: 'shiftEnterLineFeed' as const,
};

// =============================================================================
// WebSocket Configuration
// =============================================================================

/** Initial reconnect delay in milliseconds */
export const RECONNECT_INITIAL_DELAY = 1000;

/** Maximum reconnect delay in milliseconds */
export const RECONNECT_MAX_DELAY = 30000;

/** Backoff multiplier */
export const RECONNECT_BACKOFF_FACTOR = 2;

/** Jitter range (0.25 = ±25% randomization) */
export const RECONNECT_JITTER = 0.25;

// =============================================================================
// Terminal Rendering Constants
// =============================================================================

/** Terminal font stack for monospace rendering */
export const TERMINAL_FONT_STACK =
  "'Cascadia Code', 'Cascadia Mono', Consolas, 'Courier New', 'Segoe UI Emoji', 'Apple Color Emoji', 'Noto Color Emoji', 'Segoe UI Symbol', monospace";

/** Padding around terminal content in pixels */
export const TERMINAL_PADDING = 0;

/** Reserved space for xterm's overlay scrollbar (0 = scrollbar overlays text) */
export const SCROLLBAR_WIDTH = 0;

/** Minimum terminal columns */
export const MIN_TERMINAL_COLS = 10;

/** Minimum terminal rows */
export const MIN_TERMINAL_ROWS = 5;

/** Maximum terminal columns */
export const MAX_TERMINAL_COLS = 300;

/** Maximum terminal rows */
export const MAX_TERMINAL_ROWS = 100;

/** Maximum frame dimension for validation */
export const MAX_FRAME_DIMENSION = 500;

/** Mobile breakpoint in pixels */
export const MOBILE_BREAKPOINT = 768;

/** Max viewport width where coarse primary touch should still use mobile chrome */
export const MOBILE_TOUCH_BREAKPOINT = 1024;

// =============================================================================
// Icon Font (midFont) - Unicode characters
// =============================================================================

export const ICONS = {
  collapse: '\ue913', // keyboard_arrow_up
  expand: '\ue910', // keyboard_arrow_down
  settings: '\ue991', // wrench
  new: '\uea81', // terminal
  resize: '\ue989', // enlarge
  rename: '\ue91f', // drive_file_rename_outline
  close: '\ue909', // bomb
  menu: '\ue919', // menu (hamburger)
  update: '\ue91b', // arrow_right
  searchPrev: '\ue913', // keyboard_arrow_up
  searchNext: '\ue910', // keyboard_arrow_down
  save: '\ue90f', // save
  interrupt: '\ue9b5', // power
  terminal: '\uea81', // terminal
  warning: '\uea07', // warning
  tabGeneral: '\uea0c', // info
  tabAppearance: '\ue90d', // eyedropper
  tabBehavior: '\ue993', // equalizer2
  tabSecurity: '\ue908', // key
  tabDiagnostics: '\ue9ce', // eye
  more: '\ue918', // more_vert (vertical dots)
  history: '\ue967', // history (clock with arrow)
  undock: '\ue920', // close_fullscreen
  fullscreen: '\ue90c', // expand (open fullscreen)
  inject: '\ue997', // magic-wand (tell AI about MidTerm)
  notes: '\ue922', // pen-tool
} as const;

/** Creates an icon span element */
export function icon(name: keyof typeof ICONS): string {
  return `<span class="icon">${ICONS[name]}</span>`;
}
