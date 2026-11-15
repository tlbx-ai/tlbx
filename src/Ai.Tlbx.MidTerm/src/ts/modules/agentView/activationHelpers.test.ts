import { describe, expect, it, vi } from 'vitest';
import { AppServerControlHttpError } from '../../api/errors';
import { withActivationIssueNotice } from './historyProcessing';
import type { AppServerControlHistoryEntry } from './types';
import {
  resumeAppServerControlActivationAfterTransport,
  classifyAppServerControlActivationIssue,
  shouldShowAppServerControlDevErrorDialog,
} from './activationHelpers';

vi.mock('../i18n', () => ({ t: () => '' }));

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

describe('ACP lifecycle attach recovery barrier', () => {
  it('waits for the interrupted activation before starting one fresh attach/read', async () => {
    const old = deferred();
    const activate = vi.fn().mockResolvedValue(undefined);
    const recover = vi.fn().mockResolvedValue(undefined);
    const pending = resumeAppServerControlActivationAfterTransport(
      recover,
      old.promise,
      () => true,
      activate,
    );
    await Promise.resolve();
    expect(recover).toHaveBeenCalledOnce();
    expect(activate).not.toHaveBeenCalled();
    old.resolve();
    await pending;
    expect(activate).toHaveBeenCalledOnce();
  });

  it('drops pending attach/read when the selected-view or lifecycle guard is invalidated', async () => {
    const old = deferred();
    let current = true;
    const activate = vi.fn();
    const pending = resumeAppServerControlActivationAfterTransport(
      async () => {},
      old.promise,
      () => current,
      activate,
    );
    await Promise.resolve();
    current = false;
    old.resolve();
    await pending;
    expect(activate).not.toHaveBeenCalled();
  });

  it('does not activate if the captured view became stale while connecting', async () => {
    const connection = deferred();
    const activate = vi.fn();
    const pending = resumeAppServerControlActivationAfterTransport(
      () => connection.promise,
      undefined,
      () => false,
      activate,
    );
    connection.resolve();
    await pending;
    expect(activate).not.toHaveBeenCalled();
  });

  it('reports transport failure without scheduling retries or invoking attach', async () => {
    const activate = vi.fn();
    await expect(
      resumeAppServerControlActivationAfterTransport(
        async () => {
          throw new Error('offline');
        },
        undefined,
        () => true,
        activate,
      ),
    ).rejects.toThrow('offline');
    expect(activate).not.toHaveBeenCalled();
  });
});

describe('Agent Controller attach failures', () => {
  it.each([
    'App Server Controller native runtime is not available for this session.',
    'AppServerControl native runtime is not available for this session.',
  ])('keeps unavailable runtime failures recoverable: %s', (detail) => {
    const issue = classifyAppServerControlActivationIssue(
      new AppServerControlHttpError(400, detail),
      false,
    );
    expect(issue.kind).toBe('native-runtime-unavailable');
    expect(issue.actions.map((action) => action.id)).toContain('retry-appServerControl');
    expect(shouldShowAppServerControlDevErrorDialog(issue)).toBe(false);
  });
  it.each([false, true])(
    'explains a missing saved conversation (retained history: %s)',
    (retained) => {
      const issue = classifyAppServerControlActivationIssue(
        new AppServerControlHttpError(
          400,
          'no rollout found for thread id 00000000-0000-0000-0000-000000000001',
        ),
        retained,
      );
      expect(issue.kind).toBe('missing-resume-history');
      expect(issue.body).toContain('Choose another conversation');
      expect(issue.body).not.toContain('00000000');
      expect(shouldShowAppServerControlDevErrorDialog(issue)).toBe(false);
    },
  );
  it('shows a readable recovery notice while retaining existing conversation rows', () => {
    const issue = classifyAppServerControlActivationIssue(
      new Error('no rollout found for thread id missing'),
      true,
    );
    const retained = {
      id: 'retained',
      body: 'Existing assistant response',
    } as AppServerControlHistoryEntry;
    const history = [retained];
    const result = withActivationIssueNotice(history, issue);
    expect(result[0]).toMatchObject({ kind: 'notice', tone: 'warning', body: issue.body });
    expect(result[1]).toBe(retained);
    expect(history).toEqual([retained]);
  });
  it('retains diagnostic reporting for an unknown startup failure', () => {
    const issue = classifyAppServerControlActivationIssue(
      new Error('Unexpected protocol invariant'),
      false,
    );
    expect(issue.body).toBe('Unexpected protocol invariant');
    expect(shouldShowAppServerControlDevErrorDialog(issue)).toBe(true);
  });
});
