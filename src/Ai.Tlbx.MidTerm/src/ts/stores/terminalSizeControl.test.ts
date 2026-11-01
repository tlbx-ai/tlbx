import { afterEach, describe, expect, it } from 'vitest';
import type { TerminalSizeControlStatus } from '../types';
import {
  $terminalSizeControls,
  getTerminalSizeControl,
  getTerminalSizeDimensions,
  removeTerminalSizeControlSource,
  setTerminalSizeControl,
  setTerminalSizeControlsForSource,
} from './index';

const source = 'hub:race-test';
const sessionId = 'hub:race-test:session-1';

function status(epoch: number, isOwner: boolean): TerminalSizeControlStatus {
  return {
    sessionId,
    isOwner,
    hasOwner: true,
    ownerOnline: true,
    canTakeOverAutomatically: isOwner,
    epoch,
  };
}

describe('terminal size-control projection', () => {
  afterEach(() => {
    removeTerminalSizeControlSource(source);
  });

  it('keeps unchanged snapshots and unaffected sessions stable', () => {
    const second = { ...status(9, false), sessionId: 'hub:race-test:session-2' };
    setTerminalSizeControlsForSource(source, [status(9, false), second]);
    const before = $terminalSizeControls.get();
    setTerminalSizeControlsForSource(source, [{ ...status(9, false) }, { ...second }]);
    expect($terminalSizeControls.get()).toBe(before);
    setTerminalSizeControlsForSource(source, [status(10, true), { ...second }]);
    expect($terminalSizeControls.get()).not.toBe(before);
    expect($terminalSizeControls.get()[second.sessionId]).toBe(before[second.sessionId]);
    setTerminalSizeControlsForSource(source, [status(10, true)]);
    expect($terminalSizeControls.get()[second.sessionId]).toBeUndefined();
  });

  it('does not let a delayed command response regress an authoritative epoch', () => {
    setTerminalSizeControlsForSource(source, [status(9, false)]);

    setTerminalSizeControl(status(8, true));

    expect(getTerminalSizeControl(sessionId)).toEqual(status(9, false));
  });

  it('accepts same-epoch online-state updates and newer ownership epochs', () => {
    setTerminalSizeControlsForSource(source, [status(9, false)]);
    const offline = { ...status(9, false), ownerOnline: false };

    setTerminalSizeControl(offline);
    expect(getTerminalSizeControl(sessionId)?.ownerOnline).toBe(false);

    setTerminalSizeControl(status(10, true));
    expect(getTerminalSizeControl(sessionId)).toEqual(status(10, true));
  });

  it('keeps the confirmed grid against older responses and snapshots at the same epoch', () => {
    const current = { ...status(9, true), cols: 103, rows: 44, sizeRevision: 2 };
    const stale = { ...current, cols: 158, rows: 54, sizeRevision: 1 };
    setTerminalSizeControl(current);
    setTerminalSizeControl(stale);
    setTerminalSizeControlsForSource(source, [stale]);
    expect(getTerminalSizeControl(sessionId)).toEqual(current);
    expect(getTerminalSizeDimensions(sessionId, 158, 54)).toEqual({ cols: 103, rows: 44 });
    setTerminalSizeControl({ ...current, isOwner: false, epoch: 10 });
    expect(getTerminalSizeDimensions(sessionId, 158, 54)).toEqual({ cols: 103, rows: 44 });
  });

  it('uses output dimensions until the remote host provides a confirmed grid', () => {
    setTerminalSizeControl(status(9, false));
    expect(getTerminalSizeDimensions(sessionId, 158, 54)).toEqual({ cols: 158, rows: 54 });
  });
});
