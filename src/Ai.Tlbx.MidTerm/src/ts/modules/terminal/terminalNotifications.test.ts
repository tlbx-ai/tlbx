import { Terminal } from '@xterm/xterm';
import { describe, expect, it } from 'vitest';
import {
  buildTerminalNotificationBody,
  normalizeTerminalNotificationText,
  parseOsc777Notification,
  parseOsc9Notification,
  registerTerminalNotificationHandlers,
  shouldShowDesktopTerminalNotification,
  type TerminalNotificationSignal,
} from './terminalNotifications';

async function write(terminal: Terminal, data: string | Uint8Array): Promise<void> {
  await new Promise<void>((resolve) => terminal.write(data, resolve));
}

describe('terminal notification protocols', () => {
  it('recognizes BEL, Codex OSC 9, tmux-wrapped OSC 9, and OSC 777', async () => {
    const terminal = new Terminal({ allowProposedApi: true });
    const notifications: TerminalNotificationSignal[] = [];
    const disposables = registerTerminalNotificationHandlers(terminal, (signal) => {
      notifications.push(signal);
    });

    try {
      await write(terminal, Uint8Array.of(0x07));
      await write(terminal, '\x1b]9;Agent turn complete\x07');
      await write(terminal, '\x1bPtmux;\x1b\x1b]9;Approval requested\x07\x1b\\');
      await write(terminal, '\x1b]777;notify;Claude Code;Waiting for input\x1b\\');

      expect(notifications).toEqual([
        { protocol: 'bel' },
        { protocol: 'osc9', body: 'Agent turn complete' },
        { protocol: 'osc9', body: 'Approval requested' },
        { protocol: 'osc777', title: 'Claude Code', body: 'Waiting for input' },
      ]);
    } finally {
      for (const disposable of disposables) disposable.dispose();
      terminal.dispose();
    }
  });

  it('consumes progress/control OSC 9 commands without notifying', async () => {
    const terminal = new Terminal({ allowProposedApi: true });
    const notifications: TerminalNotificationSignal[] = [];
    const disposables = registerTerminalNotificationHandlers(terminal, (signal) => {
      notifications.push(signal);
    });

    try {
      await write(terminal, '\x1b]9;4;1;50\x1b\\');
      await write(terminal, '\x1b]9;9;file:///workspace\x07');
      await write(terminal, '\x1b]777;progress;Build;50\x07');
      expect(notifications).toEqual([]);
    } finally {
      for (const disposable of disposables) disposable.dispose();
      terminal.dispose();
    }
  });

  it('sanitizes untrusted notification text and preserves semicolons in the body', () => {
    expect(parseOsc9Notification('done\n\x1b[31mnow\u202etext')).toEqual({
      protocol: 'osc9',
      body: 'done now text',
    });
    expect(parseOsc777Notification('notify;Build;finished; artifact.zip')).toEqual({
      protocol: 'osc777',
      title: 'Build',
      body: 'finished; artifact.zip',
    });
    expect(normalizeTerminalNotificationText('   ', 20)).toBeNull();
  });

  it('limits terminal-controlled notification text', () => {
    const parsed = parseOsc9Notification('x'.repeat(400));
    expect(parsed?.body?.length).toBe(240);
    expect(parsed?.body?.endsWith('…')).toBe(true);
  });
});

describe('desktop terminal notification policy', () => {
  it.each([
    [{ documentHidden: true, documentFocused: false, sourceSessionActive: true }, true],
    [{ documentHidden: false, documentFocused: true, sourceSessionActive: true }, false],
  ] as const)('evaluates visibility %o', (visibility, expected) => {
    expect(shouldShowDesktopTerminalNotification(visibility)).toBe(expected);
  });

  it('keeps the session identity outside terminal-controlled text', () => {
    expect(
      buildTerminalNotificationBody({
        protocol: 'osc777',
        title: 'Codex',
        body: 'Agent turn complete',
      }),
    ).toBe('Codex: Agent turn complete');
    expect(buildTerminalNotificationBody({ protocol: 'bel' })).toBe('Needs your attention');
  });
});
