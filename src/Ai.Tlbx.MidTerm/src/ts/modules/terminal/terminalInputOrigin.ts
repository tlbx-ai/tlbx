import type { Terminal, IDisposable } from '@xterm/xterm';

/** xterm marks keyboard/paste input before onData; parser replies have no such signal. */
export function onTerminalInput(
  terminal: Terminal,
  listener: (data: string, userInput: boolean) => void,
): IDisposable {
  const core = (
    terminal as unknown as {
      _core?: { coreService?: { onUserInput: (listener: () => void) => IDisposable } };
    }
  )._core?.coreService;
  let userInput = false;
  const origin = core?.onUserInput(() => {
    userInput = true;
  });
  const data = terminal.onData((value) => {
    const fromUser = userInput;
    userInput = false;
    listener(value, fromUser);
  });
  return {
    dispose: () => {
      origin?.dispose();
      data.dispose();
    },
  };
}
