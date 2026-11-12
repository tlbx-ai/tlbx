import { sendCommandBayPrompt } from '../commandBay/queue';
import { sendInput } from '../comms';
import {
  createAppServerControlTurnRequest,
  isAppServerControlActiveSession,
} from '../appServerControl/input';
import { pasteToTerminal } from '../terminal';

const SESSION_TEXT_SUBMIT_DELAY_MS = 200;

export async function submitSessionText(sessionId: string, text: string): Promise<void> {
  if (isAppServerControlActiveSession(sessionId)) {
    await sendCommandBayPrompt(sessionId, createAppServerControlTurnRequest(text, [], sessionId));
    return;
  }

  // Match Smart Input's paste-and-submit behavior for terminal sessions so
  // JS-driven TUIs receive a settled paste before Enter is sent.
  await pasteToTerminal(sessionId, text);
  await new Promise((resolve) => globalThis.setTimeout(resolve, SESSION_TEXT_SUBMIT_DELAY_MS));
  sendInput(sessionId, '\r');
}
