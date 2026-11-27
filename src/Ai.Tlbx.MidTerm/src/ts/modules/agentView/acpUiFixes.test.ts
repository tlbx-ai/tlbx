import { describe, expect, it } from 'vitest';
import { renderMarkdown } from '../../utils/markdown';
import { scanAssistantTextEnrichment } from './assistantEnrichment';
import { buildAppServerControlHistoryEntries } from './historyProcessing';
import { resolveToolCommandScript } from './toolActivityView';
import { selectProjectedToolEvidenceLines } from './toolOutput';
import {
  normalizeDisplayPath,
  parseCompactToolRow,
  summarizeToolActivityGroup,
} from './toolRowPresentation';
import { $sessions } from '../../stores';
import type { AppServerControlHistorySnapshot } from '../../api/types';
import type { AppServerControlHistoryEntry } from './types';

describe('ACP UI and markdown regressions', () => {
  it('renders nested unordered and ordered markdown lists', () => {
    const html = renderMarkdown(
      ['- Parent item', '  - Child item A', '  - Child item B', '- Second parent'].join('\n'),
    );
    expect(html).toContain('<ul><li>Parent item');
    expect(html).toContain('<ul><li>Child item A');
    expect(html).toContain('</li><li>Child item B');
    expect(html).not.toContain('  - Child item A');
  });

  it('does not highlight ordinary prose numbers, code methods, or live session IDs', () => {
    $sessions.set({
      afa4f253: { id: 'afa4f253' } as never,
    });
    const text =
      'At 22:35, 138 tools dropped by -52 % to 18.950 tokens. Fixed JsonArray.Add<T> and JsonArray.Add(JsonNode?) in Session afa4f253 (commit 1b2c3d4).';
    const result = scanAssistantTextEnrichment(text, [
      {
        field: 'body',
        displayText: 'JsonArray.Add',
        path: 'JsonArray.Add',
        pathKind: 'relative',
        exists: false,
        isDirectory: false,
      },
    ]);

    expect(result.tokens.filter((token) => token.kind === 'number')).toEqual([]);
    expect(result.tokens.filter((token) => token.kind === 'file')).toEqual([]);
    expect(result.tokens.filter((token) => token.kind === 'git').map((t) => t.hash)).toEqual([
      '1b2c3d4',
    ]);
  });

  it('retains ACP tool entries that carry toolPresentation even with empty body and null commandText', () => {
    const snapshot = {
      history: [
        {
          id: 'tool-1',
          order: 1,
          kind: 'tool',
          status: 'completed',
          title: 'Read file',
          body: '',
          updatedAt: '2026-11-27T21:40:00Z',
          toolPresentation: {
            category: 'read',
            label: 'Read file',
            toolName: 'File /Q:/repos/Jpa/AGENTS.md',
            paths: ['Q:\\repos\\Jpa\\AGENTS.md'],
          },
        },
        {
          id: 'tool-2',
          order: 2,
          kind: 'tool',
          status: 'completed',
          title: 'Ran command',
          body: '',
          updatedAt: '2026-11-27T21:40:01Z',
          toolPresentation: {
            category: 'command',
            label: 'Ran command',
            toolName: 'git status --short',
            paths: [],
          },
        },
      ],
    } as unknown as AppServerControlHistorySnapshot;

    const entries = buildAppServerControlHistoryEntries(snapshot);
    expect(entries).toHaveLength(2);
    expect(resolveToolCommandScript(entries[1] as AppServerControlHistoryEntry)).toBe(
      'git status --short',
    );
  });

  it('normalizes POSIX-prefixed Windows paths and formats compact MCP/read tool rows cleanly', () => {
    expect(normalizeDisplayPath('/Q:/repos/Jpa/.memory/dev-projects.md')).toBe(
      'Q:\\repos\\Jpa\\.memory\\dev-projects.md',
    );

    const readEntry = {
      id: 'r1',
      kind: 'tool',
      title: 'Read file',
      sourceStatus: 'completed',
      toolPresentation: {
        category: 'read',
        label: 'Read file',
        toolName: 'File /Q:/repos/Jpa/.memory/dev-projects.md',
        paths: [],
      },
    } as unknown as AppServerControlHistoryEntry;
    const readRow = parseCompactToolRow(readEntry);
    expect(readRow.badgeTone).toBe('read');
    expect(readRow.primaryText).toBe('dev-projects.md');
    expect(readRow.secondaryPath).toBe('Q:\\repos\\Jpa\\.memory');
    expect(readRow.secondaryPathRaw).toBe('Q:\\repos\\Jpa\\.memory\\dev-projects.md');
    expect(readRow.statusKind).toBe('done');

    const mcpEntry = {
      id: 'm1',
      kind: 'tool',
      title: 'Used tool',
      sourceStatus: 'completed',
      toolPresentation: {
        category: 'other',
        label: 'Used tool',
        toolName: 'Run mcp_tlbx_repo_bind',
        paths: ['Q:\\repos\\tlbx-1'],
      },
    } as unknown as AppServerControlHistoryEntry;
    const mcpRow = parseCompactToolRow(mcpEntry);
    expect(mcpRow.badgeTone).toBe('mcp');
    expect(mcpRow.serverName).toBe('tlbx');
    expect(mcpRow.primaryText).toBe('repo_bind');
    expect(mcpRow.secondaryPath).toBe('Q:\\repos\\tlbx-1');

    const cmdEntry = {
      id: 'c1',
      kind: 'tool',
      title: 'Ran command',
      sourceStatus: 'completed',
      toolPresentation: {
        category: 'command',
        label: 'Ran command',
        toolName: 'foreach ($i in 1..4) { Write-Output $i }',
        evidence: 'l1\nl2\nl3\nl4\nl5\nl6\nl7',
        totalLineCount: 15,
        omittedLineCount: 8,
        paths: [],
      },
    } as unknown as AppServerControlHistoryEntry;
    expect(selectProjectedToolEvidenceLines(cmdEntry, { expanded: true })).toHaveLength(7);
    expect(summarizeToolActivityGroup([mcpEntry, readEntry, cmdEntry])).toContain('MCP (tlbx)');
  });
});
