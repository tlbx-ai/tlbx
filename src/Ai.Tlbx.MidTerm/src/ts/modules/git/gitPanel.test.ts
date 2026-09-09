import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  createGitPanel,
  destroyGitPanel,
  renderGitPanelInto,
  suspendGitPanel,
  updateGitStatus,
} from './gitPanel';
import { syncGitPanelDom } from './gitPanelDom';
import { fetchGitStatus } from './gitApi';
import type { GitStatusResponse } from './types';

vi.mock('./gitPanelDom', () => ({ syncGitPanelDom: vi.fn() }));
vi.mock('./gitApi', () => ({
  fetchGitStatus: vi.fn(async () => null),
  fetchGitLog: vi.fn(),
  fetchDiffView: vi.fn(),
  fetchCommitDetails: vi.fn(),
}));
vi.mock('../input/submit', () => ({ submitSessionText: vi.fn() }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));

function container(): HTMLElement {
  return { querySelector: vi.fn(() => null), addEventListener: vi.fn() } as unknown as HTMLElement;
}

function status(branch: string): GitStatusResponse {
  return {
    branch,
    repoRoot: 'repo',
    ahead: 0,
    behind: 0,
    staged: [],
    modified: [],
    untracked: [],
    conflicted: [],
    recentCommits: [],
    stashCount: 0,
    totalAdditions: 0,
    totalDeletions: 0,
  };
}

describe('Git panel update ownership', () => {
  afterEach(() => {
    destroyGitPanel('a');
    destroyGitPanel('b');
    vi.clearAllMocks();
  });

  it('skips identical status and hidden rendering, then paints latest state on reopen', async () => {
    const body = container();
    createGitPanel(body, 'a', 'repo');
    updateGitStatus('a', status('dev'));
    vi.mocked(syncGitPanelDom).mockClear();
    updateGitStatus('a', status('dev'));
    expect(syncGitPanelDom).not.toHaveBeenCalled();
    suspendGitPanel(body);
    updateGitStatus('a', status('latest'));
    expect(syncGitPanelDom).not.toHaveBeenCalled();
    await renderGitPanelInto(body, 'a', 'repo');
    expect(syncGitPanelDom).toHaveBeenCalledWith(body, expect.stringContaining('latest'));
  });

  it('does not let a delayed fetch or background session overwrite the shared dock', async () => {
    const body = container();
    let resolveFetch!: (value: GitStatusResponse) => void;
    vi.mocked(fetchGitStatus).mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveFetch = resolve;
        }),
    );
    const pending = renderGitPanelInto(body, 'a', 'repo');
    createGitPanel(body, 'b', 'repo');
    updateGitStatus('b', status('visible'));
    vi.mocked(syncGitPanelDom).mockClear();
    resolveFetch(status('late'));
    await pending;
    updateGitStatus('a', status('background'));
    expect(syncGitPanelDom).not.toHaveBeenCalled();
  });

  it('keeps pushed status when an older fetch finishes', async () => {
    const body = container();
    let resolveFetch!: (value: GitStatusResponse) => void;
    vi.mocked(fetchGitStatus).mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveFetch = resolve;
        }),
    );
    const pending = renderGitPanelInto(body, 'a', 'repo');
    updateGitStatus('a', status('newer-push'));
    resolveFetch(status('old-fetch'));
    await pending;
    expect(syncGitPanelDom).toHaveBeenLastCalledWith(body, expect.stringContaining('newer-push'));
  });
});
