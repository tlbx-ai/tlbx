import { describe, expect, it, vi } from 'vitest';
import type {
  AppServerControlHistoryRequestSummary,
  AppServerControlHistorySnapshot,
  MidTermSettingsPublic,
} from '../../api/types';
import { $currentSettings } from '../../stores';
import { resolveHistoryBodyPresentation } from './historyContent';
import { buildAppServerControlHistoryEntries } from './historyProcessing';
import {
  areAgentTechnicalDetailsEnabled,
  filterReadingHistory,
  showAgentTechnicalDetails,
} from './historyVisibility';
import type { AppServerControlHistoryEntry } from './types';

vi.mock('../../stores', () => ({ $currentSettings: { get: vi.fn() } }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../../api/client', () => ({ AppServerControlHttpError: class extends Error {} }));

function entry(
  id: string,
  kind: AppServerControlHistoryEntry['kind'],
  patch: Partial<AppServerControlHistoryEntry> = {},
): AppServerControlHistoryEntry {
  return { id, kind, order: 1, tone: 'info', label: '', title: '', body: id, meta: '', ...patch };
}

function request(requestId: string, state: string): AppServerControlHistoryRequestSummary {
  return {
    requestId,
    state,
    kind: 'approval',
    kindLabel: 'Approval',
    questions: [],
    answers: [],
    updatedAt: '2026-11-08T00:00:00Z',
  };
}

describe('quiet reading defaults', () => {
  it('requires an explicit new opt-in and never infers it from the former true default', () => {
    expect(areAgentTechnicalDetailsEnabled(null)).toBe(false);
    expect(areAgentTechnicalDetailsEnabled(undefined)).toBe(false);
    expect(areAgentTechnicalDetailsEnabled({ showAgentTechnicalDetails: false })).toBe(false);
    expect(areAgentTechnicalDetailsEnabled({ showAgentTechnicalDetails: true })).toBe(true);
    const legacy = { showUnknownAgentMessages: true } as unknown as MidTermSettingsPublic;
    expect(areAgentTechnicalDetailsEnabled(legacy)).toBe(false);
    vi.mocked($currentSettings.get).mockReturnValue({
      showAgentTechnicalDetails: true,
    } as MidTermSettingsPublic);
    expect(showAgentTechnicalDetails()).toBe(true);
    vi.mocked($currentSettings.get).mockReturnValue({
      showAgentTechnicalDetails: false,
    } as MidTermSettingsPublic);
    expect(showAgentTechnicalDetails()).toBe(false);
  });

  it('removes protocol/tool/system noise while retaining conversation and current work feedback', () => {
    const entries = [
      entry('user', 'user'),
      entry('answer', 'assistant'),
      entry('working', 'assistant', { busyIndicator: true, body: 'Reading the requested file' }),
      entry('command', 'tool', { sourceStatus: 'running', live: true }),
      entry('unknown', 'tool', { sourceItemType: 'unknown_agent_message' }),
      entry('thinking', 'reasoning'),
      entry('startup', 'system'),
      entry('usage', 'notice'),
      entry('duration', 'system', { turnDurationNote: true }),
    ];
    expect(filterReadingHistory(entries, [], false).map((value) => value.id)).toEqual([
      'user',
      'answer',
      'working',
    ]);
    const copy = filterReadingHistory(entries, [], true);
    expect(copy).toEqual(entries);
    expect(copy).not.toBe(entries);
    expect(entries).toHaveLength(9);
  });

  it('retains pending approvals and questions but removes resolved request details', () => {
    const entries = [
      entry('approval', 'request', { requestId: 'approval' }),
      entry('question', 'request', { requestId: 'question', sourceItemType: 'interview' }),
      entry('resolved', 'request', { requestId: 'resolved', sourceStatus: 'open' }),
      entry('declined', 'request', { requestId: 'declined', tone: 'attention' }),
      entry('embedded', 'request', { sourceStatus: 'open', sourceItemType: 'interview' }),
    ];
    const requests = [
      request('approval', 'open'),
      { ...request('question', 'open'), kind: 'interview' },
      request('resolved', 'resolved'),
      request('declined', 'cancelled'),
    ];
    expect(filterReadingHistory(entries, requests, false).map((value) => value.id)).toEqual([
      'approval',
      'question',
      'embedded',
    ]);
    expect(filterReadingHistory(entries, requests, true)).toEqual(entries);
  });

  it('keeps real errors and actionable connection failures visible without exposing healthy notices', () => {
    const failedTool = entry('failed-tool', 'tool', {
      sourceStatus: 'failed',
      tone: 'attention',
    });
    const structuredError = entry('structured-error', 'tool', {
      toolPresentation: {
        category: 'command',
        label: 'Run',
        evidenceKind: 'error',
        totalLineCount: 1,
        omittedLineCount: 0,
        paths: [],
      },
    });
    const entries = [
      failedTool,
      structuredError,
      entry('provider-error', 'notice', { sourceItemType: 'agent_error' }),
      entry('midterm-status', 'notice', { tone: 'attention' }),
      entry('appServerControl-issue:auth', 'system', { tone: 'warning' }),
      entry('healthy', 'system', { sourceStatus: 'ready' }),
    ];
    expect(filterReadingHistory(entries, [], false).map((value) => value.id)).toEqual([
      'failed-tool',
      'structured-error',
      'provider-error',
      'midterm-status',
      'appServerControl-issue:auth',
    ]);
    const error = entry('error', 'notice', { tone: 'attention', body: 'Error\n'.repeat(250) });
    expect(resolveHistoryBodyPresentation(error).collapsedByDefault).toBe(false);
  });

  it('keeps file review available but folds even large live diffs without a raw preview', () => {
    const diff = entry('diff', 'diff', { body: '+line\n'.repeat(250), live: true });
    const edit = entry('edit', 'tool', {
      toolPresentation: {
        category: 'edit',
        label: 'Edit',
        totalLineCount: 250,
        omittedLineCount: 0,
        paths: ['app.ts'],
        evidence: '+line\n'.repeat(250),
      },
    });
    expect(filterReadingHistory([diff, edit], [], false)).toEqual([diff, edit]);
    expect(resolveHistoryBodyPresentation(diff)).toMatchObject({
      mode: 'diff',
      collapsedByDefault: true,
      preview: '',
    });
    expect(resolveHistoryBodyPresentation(edit)).toMatchObject({
      mode: 'tool',
      collapsedByDefault: true,
      preview: '',
    });
    const failedEdit = { ...edit, sourceStatus: 'failed', tone: 'attention' as const };
    expect(resolveHistoryBodyPresentation(failedEdit).collapsedByDefault).toBe(false);
  });

  it('does not suppress a real canonical error merely because its body contains telemetry fields', () => {
    const snapshot = {
      history: [
        {
          entryId: 'rate-failure',
          order: 1,
          kind: 'notice',
          status: 'failed',
          title: 'Provider update failed',
          body: 'Cannot read "usedpercent": permission denied',
          attachments: [],
          streaming: false,
          createdAt: '2026-11-08T00:00:00Z',
          updatedAt: '2026-11-08T00:00:00Z',
        },
      ],
    } as AppServerControlHistorySnapshot;
    const mapped = buildAppServerControlHistoryEntries(snapshot);
    expect(filterReadingHistory(mapped, [], false).map((value) => value.id)).toEqual(['rate-failure']);
  });
});
