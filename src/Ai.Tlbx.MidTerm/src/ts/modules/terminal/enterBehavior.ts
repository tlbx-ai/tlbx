/**
 * Terminal Enter Behavior
 *
 * Computes tlbx-specific Enter key overrides before xterm.js applies its
 * default keyboard translation.
 */

// Keep the legacy persisted values for compatibility with existing settings.json
// files, but treat them as a simple off/on remap toggle in the terminal UI.
export type TerminalEnterMode = 'default' | 'shiftEnterLineFeed';
export type TerminalEnterTarget = 'default' | 'powershell' | 'codex';

export interface EnterOverrideInput {
  key?: string;
  code?: string;
  keyCode?: number;
  which?: number;
  charCode?: number;
  ctrlKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
  metaKey: boolean;
}

const META_ENTER = '\x1b\r';

function containsCodexToken(value: string): boolean {
  return /(^|[\\/\s"'])codex(?:\.cmd|\.exe|\.js)?(?:$|[\s"'./\\-])/.test(value);
}

export function isPowerShellEnterTarget(
  foregroundName?: string | null,
  foregroundCommandLine?: string | null,
  shellType?: string | null,
): boolean {
  const haystack =
    `${foregroundName ?? ''} ${foregroundCommandLine ?? ''} ${shellType ?? ''}`.toLowerCase();
  return (
    haystack.includes('pwsh') || haystack.includes('powershell') || haystack.includes('psreadline')
  );
}

export function isCodexEnterTarget(
  foregroundName?: string | null,
  foregroundCommandLine?: string | null,
  shellType?: string | null,
): boolean {
  const haystack =
    `${foregroundName ?? ''} ${foregroundCommandLine ?? ''} ${shellType ?? ''}`.toLowerCase();

  return haystack.includes('@openai/codex') || containsCodexToken(haystack);
}

export function getTerminalEnterTarget(
  foregroundName?: string | null,
  foregroundCommandLine?: string | null,
  shellType?: string | null,
): TerminalEnterTarget {
  if (isCodexEnterTarget(foregroundName, foregroundCommandLine, shellType)) {
    return 'codex';
  }

  return isPowerShellEnterTarget(foregroundName, foregroundCommandLine, shellType)
    ? 'powershell'
    : 'default';
}

export function isTerminalEnterRemapEnabled(mode: TerminalEnterMode): boolean {
  return mode === 'shiftEnterLineFeed';
}

export function describeTerminalEnterOverrideBytes(value: string): string {
  if (value === META_ENTER) {
    return 'ESC+CR';
  }

  return `bytes=${JSON.stringify(value)}`;
}

export function shouldRouteTerminalEnterOverrideThroughXtermInput(
  _target: TerminalEnterTarget,
  _value: string,
): boolean {
  return false;
}

export function describeTerminalEnterOverrideDelivery(
  target: TerminalEnterTarget,
  value: string,
): string {
  const description = describeTerminalEnterOverrideBytes(value);
  return shouldRouteTerminalEnterOverrideThroughXtermInput(target, value)
    ? `xterm-input ${description}`
    : description;
}

function isEnterKey(input: EnterOverrideInput): boolean {
  return (
    input.key === 'Enter' ||
    input.code === 'Enter' ||
    input.code === 'NumpadEnter' ||
    input.keyCode === 13 ||
    input.which === 13 ||
    input.charCode === 13
  );
}

/**
 * Returns the raw terminal bytes to send when tlbx overrides Enter.
 *
 * Send Alt+Enter for a deliberate line break, including in Codex on Windows.
 * Do not synthesize a paste burst: Codex 0.154.0 turns the former
 * space/Enter/Backspace workaround into a space instead of a line break.
 */
export function getTerminalEnterOverride(
  input: EnterOverrideInput,
  mode: TerminalEnterMode,
  target: TerminalEnterTarget = 'default',
): string | null {
  if (!isEnterKey(input)) {
    return null;
  }

  if (
    isTerminalEnterRemapEnabled(mode) &&
    (target === 'codex' || !input.altKey) &&
    !input.metaKey &&
    (input.ctrlKey || input.shiftKey || (target === 'codex' && input.altKey))
  ) {
    return META_ENTER;
  }

  return null;
}
