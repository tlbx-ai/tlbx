import { describe, expect, it } from 'vitest';
import {
  getTerminalEnterOverride,
  isCodexEnterTarget,
  isPowerShellEnterTarget,
  type EnterOverrideInput,
} from './enterBehavior';

function key(
  value: string | undefined = 'Enter',
  mods: Partial<Pick<EnterOverrideInput, 'ctrlKey' | 'shiftKey' | 'altKey' | 'metaKey'>> = {},
  extra: Partial<Pick<EnterOverrideInput, 'code' | 'keyCode' | 'which' | 'charCode'>> = {},
): EnterOverrideInput {
  return {
    key: value,
    code: extra.code,
    keyCode: extra.keyCode,
    which: extra.which,
    charCode: extra.charCode,
    ctrlKey: mods.ctrlKey ?? false,
    shiftKey: mods.shiftKey ?? false,
    altKey: mods.altKey ?? false,
    metaKey: mods.metaKey ?? false,
  };
}

describe('getTerminalEnterOverride', () => {
  it('maps Ctrl+Enter to Alt+Enter-compatible bytes only when enabled', () => {
    expect(getTerminalEnterOverride(key('Enter', { ctrlKey: true }), 'default')).toBeNull();
    expect(getTerminalEnterOverride(key('Enter', { ctrlKey: true }), 'shiftEnterLineFeed')).toBe(
      '\x1b\r',
    );
  });

  it('maps Shift+Enter to Alt+Enter-compatible bytes only when enabled', () => {
    expect(getTerminalEnterOverride(key('Enter', { shiftKey: true }), 'default')).toBeNull();
    expect(getTerminalEnterOverride(key('Enter', { shiftKey: true }), 'shiftEnterLineFeed')).toBe(
      '\x1b\r',
    );
  });

  it('accepts legacy Enter key fields when key is missing or generic', () => {
    expect(
      getTerminalEnterOverride(
        key(undefined, { shiftKey: true }, { keyCode: 13 }),
        'shiftEnterLineFeed',
      ),
    ).toBe('\x1b\r');
    expect(
      getTerminalEnterOverride(
        key('Process', { shiftKey: true }, { code: 'Enter' }),
        'shiftEnterLineFeed',
      ),
    ).toBe('\x1b\r');
  });

  it('uses Alt+Enter-compatible bytes for modified Enter in PowerShell too', () => {
    expect(
      getTerminalEnterOverride(
        key('Enter', { shiftKey: true }),
        'shiftEnterLineFeed',
        'powershell',
      ),
    ).toBe('\x1b\r');
    expect(
      getTerminalEnterOverride(key('Enter', { ctrlKey: true }), 'shiftEnterLineFeed', 'powershell'),
    ).toBe('\x1b\r');
    expect(
      getTerminalEnterOverride(
        key('Enter', { ctrlKey: true, shiftKey: true }),
        'shiftEnterLineFeed',
        'powershell',
      ),
    ).toBe('\x1b\r');
  });

  it('maps modified Enter to explicit Alt+Enter in Codex without synthetic paste characters', () => {
    expect(
      getTerminalEnterOverride(key('Enter', { shiftKey: true }), 'shiftEnterLineFeed', 'codex'),
    ).toBe('\x1b\r');
    expect(
      getTerminalEnterOverride(key('Enter', { ctrlKey: true }), 'shiftEnterLineFeed', 'codex'),
    ).toBe('\x1b\r');
    expect(
      getTerminalEnterOverride(key('Enter', { altKey: true }), 'shiftEnterLineFeed', 'codex'),
    ).toBe('\x1b\r');
    expect(
      getTerminalEnterOverride(
        key('Enter', { ctrlKey: true, shiftKey: true }),
        'shiftEnterLineFeed',
        'codex',
      ),
    ).toBe('\x1b\r');
    expect(getTerminalEnterOverride(key('Enter'), 'shiftEnterLineFeed', 'codex')).toBeNull();
  });

  it('leaves Alt+Enter and plain Enter on the xterm default path', () => {
    expect(getTerminalEnterOverride(key('Enter'), 'shiftEnterLineFeed')).toBeNull();
    expect(
      getTerminalEnterOverride(key('Enter', { altKey: true }), 'shiftEnterLineFeed'),
    ).toBeNull();
  });
});

describe('isCodexEnterTarget', () => {
  it('detects Codex foreground processes', () => {
    expect(isCodexEnterTarget('codex', null)).toBe(true);
    expect(isCodexEnterTarget('codex.exe', null)).toBe(true);
    expect(
      isCodexEnterTarget(
        null,
        'node C:\\Users\\johan\\AppData\\Roaming\\npm\\node_modules\\@openai\\codex\\bin\\codex.js --yolo',
      ),
    ).toBe(true);
    expect(isCodexEnterTarget(null, 'C:\\Users\\johan\\AppData\\Roaming\\npm\\codex.cmd')).toBe(
      true,
    );
  });

  it('does not treat unrelated processes as Codex', () => {
    expect(isCodexEnterTarget('pwsh.exe', null)).toBe(false);
    expect(isCodexEnterTarget('bash', '/bin/bash')).toBe(false);
  });
});

describe('isPowerShellEnterTarget', () => {
  it('detects pwsh foreground processes', () => {
    expect(isPowerShellEnterTarget('pwsh.exe', null)).toBe(true);
    expect(isPowerShellEnterTarget('powershell', null)).toBe(true);
    expect(isPowerShellEnterTarget(null, 'C:\\Program Files\\PowerShell\\7\\pwsh.exe')).toBe(true);
    expect(isPowerShellEnterTarget(null, null, 'Pwsh')).toBe(true);
    expect(isPowerShellEnterTarget(null, null, 'PowerShell')).toBe(true);
  });

  it('does not treat other shells as PowerShell', () => {
    expect(isPowerShellEnterTarget('bash', '/bin/bash')).toBe(false);
  });
});
