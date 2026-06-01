import type { TerminalState } from '../../types';

export interface ResumeCursorSnapshot {
  receivedSeq: bigint;
  renderedSeq: bigint;
}

export function getResumeSequence(snapshot: ResumeCursorSnapshot | undefined): bigint | null {
  if (!snapshot) {
    return null;
  }

  return snapshot.renderedSeq !== 0n ? snapshot.renderedSeq : snapshot.receivedSeq;
}

export function buildResumeCursorQueryValue(
  sessionTerminals: Map<string, TerminalState>,
  getSnapshot: (sessionId: string) => ResumeCursorSnapshot | undefined,
  isHubSessionId: (sessionId: string) => boolean,
): string | null {
  const tokens: string[] = [];
  sessionTerminals.forEach((_, sessionId) => {
    if (isHubSessionId(sessionId)) {
      return;
    }

    const cursor = getResumeSequence(getSnapshot(sessionId));
    if (cursor !== null && cursor > 0n) {
      tokens.push(`${sessionId}:${cursor.toString()}`);
    }
  });

  return tokens.length > 0 ? tokens.join(',') : null;
}

export function countLocalTerminals(
  sessionTerminals: Map<string, TerminalState>,
  isHubSessionId: (sessionId: string) => boolean,
): number {
  let count = 0;
  sessionTerminals.forEach((_, sessionId) => {
    if (!isHubSessionId(sessionId)) {
      count += 1;
    }
  });
  return count;
}

export function createBufferRequestFrame(
  muxHeaderSize: number,
  muxTypeBufferRequest: number,
  encodeSessionId: (buffer: Uint8Array, offset: number, sessionId: string) => void,
  sessionId: string,
  quickResume: boolean,
  replayRows: number | null,
  resumeSequence: bigint | null,
): Uint8Array {
  const includeResumeSequence = resumeSequence !== null && resumeSequence > 0n;
  const frame = new Uint8Array(
    muxHeaderSize + (includeResumeSequence ? 11 : replayRows === null ? 1 : 3),
  );
  frame[0] = muxTypeBufferRequest;
  encodeSessionId(frame, 1, sessionId);
  frame[muxHeaderSize] = quickResume ? 1 : 0;

  if (replayRows !== null || includeResumeSequence) {
    const rows = replayRows ?? 0;
    frame[muxHeaderSize + 1] = rows & 0xff;
    frame[muxHeaderSize + 2] = (rows >> 8) & 0xff;
  }

  if (includeResumeSequence) {
    new DataView(frame.buffer, frame.byteOffset, frame.byteLength).setBigUint64(
      muxHeaderSize + 3,
      resumeSequence,
      true,
    );
  }

  return frame;
}
