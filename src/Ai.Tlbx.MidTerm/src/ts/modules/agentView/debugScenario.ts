import type {
  AppServerControlHistoryItem,
  AppServerControlHistorySnapshot,
} from '../../api/client';
import type { AppServerControlAttachmentReference } from '../../api/types';

export type AppServerControlDebugScenarioName =
  | 'mixed'
  | 'tables'
  | 'long'
  | 'massive'
  | 'workflow';

type HistoryKind =
  | 'user'
  | 'assistant'
  | 'reasoning'
  | 'tool'
  | 'request'
  | 'plan'
  | 'diff'
  | 'system'
  | 'notice';

type DebugScenarioItemFactory = (
  itemId: string,
  itemType: string,
  detail: string,
  updatedAt: string,
) => AppServerControlHistorySnapshot['items'][number];

interface DebugScenarioContent {
  items: AppServerControlHistorySnapshot['items'];
  requests: AppServerControlHistorySnapshot['requests'];
  assistantText: string;
  currentTurnState: AppServerControlHistorySnapshot['currentTurn']['state'];
  currentTurnStateLabel: string;
}

function cloneHistoryAttachments(
  attachments: readonly AppServerControlAttachmentReference[] | undefined,
): AppServerControlAttachmentReference[] {
  return attachments?.map((attachment) => ({ ...attachment })) ?? [];
}

function normalizeHistoryItemType(value: string | null | undefined): string {
  return (value ?? '')
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '');
}

function historyKindFromItem(itemType: string): HistoryKind {
  const normalized = normalizeHistoryItemType(itemType);

  if (normalized.includes('user')) return 'user';
  if (normalized.includes('assistant')) return 'assistant';
  if (normalized.includes('reasoning')) return 'reasoning';
  if (normalized.includes('plan')) return 'plan';
  if (normalized.includes('diff')) return 'diff';
  if (
    normalized.includes('request') ||
    normalized.includes('approval') ||
    normalized.includes('input')
  ) {
    return 'request';
  }
  if (normalized.includes('tool') || normalized.includes('command')) return 'tool';
  return 'system';
}

function buildDebugScenarioHistory(args: {
  generatedAt: string;
  turnId: string;
  currentTurnState: string;
  items: AppServerControlHistorySnapshot['items'];
  requests: AppServerControlHistorySnapshot['requests'];
  assistantText: string;
  reasoningText: string;
  reasoningSummaryText: string;
  planText: string;
  commandOutput: string;
  fileChangeOutput: string;
  unifiedDiff: string;
}): AppServerControlHistoryItem[] {
  const historyEntries: AppServerControlHistoryItem[] = [];
  let order = 1;

  for (const item of args.items) {
    historyEntries.push({
      entryId: `${historyKindFromItem(item.itemType)}:${item.itemId}`,
      order: order++,
      kind: historyKindFromItem(item.itemType),
      turnId: item.turnId ?? null,
      itemId: item.itemId,
      requestId: null,
      status: item.status,
      itemType: item.itemType,
      title: item.title ?? null,
      body: item.detail || '',
      attachments: cloneHistoryAttachments(item.attachments),
      streaming: false,
      createdAt: item.updatedAt,
      updatedAt: item.updatedAt,
    });
  }

  const pushStream = (kind: string, title: string | null, body: string): void => {
    if (!body.trim()) return;
    const status =
      kind === 'assistant' && args.currentTurnState === 'running' ? 'streaming' : 'completed';
    historyEntries.push({
      entryId: `${kind}:${args.turnId}:${order}`,
      order: order++,
      kind,
      turnId: args.turnId,
      itemId: null,
      requestId: null,
      status,
      itemType: kind,
      title,
      body,
      attachments: [],
      streaming: kind === 'assistant' && args.currentTurnState === 'running',
      createdAt: args.generatedAt,
      updatedAt: args.generatedAt,
    });
  };

  pushStream('assistant', null, args.assistantText);
  pushStream('reasoning', 'Reasoning', args.reasoningText);
  pushStream('reasoning', 'Reasoning summary', args.reasoningSummaryText);
  pushStream('plan', 'Plan', args.planText);
  pushStream('tool', 'Command output', args.commandOutput);
  pushStream('tool', 'File change output', args.fileChangeOutput);
  pushStream('diff', 'Working diff', args.unifiedDiff);

  for (const request of args.requests) {
    historyEntries.push({
      entryId: `request:${request.requestId}`,
      order: order++,
      kind: 'request',
      turnId: request.turnId ?? null,
      itemId: null,
      requestId: request.requestId,
      status: request.state,
      itemType: request.kind,
      title: request.kindLabel,
      body: [request.detail, ...request.questions.map((question) => question.question)]
        .filter(Boolean)
        .join('\n\n'),
      attachments: [],
      streaming: false,
      createdAt: request.updatedAt,
      updatedAt: request.updatedAt,
    });
  }

  return historyEntries;
}

function buildTablesDebugScenario(
  createItem: DebugScenarioItemFactory,
  at: (offsetMs: number) => string,
): DebugScenarioContent {
  return {
    items: [
      createItem(
        'user-debug-table',
        'user_message',
        'Stress the AppServerControl history with wide markdown tables and dense comparisons.',
        at(-180000),
      ),
    ],
    requests: [],
    assistantText: [
      'Here is a dense status sheet for the current worker fleet.',
      '',
      '| Lane | Mode | State | Last token burst | Scrollback | CPU peak | First paint | Attach P95 | Model | Owner | Queue | Notes |',
      '| :--- | :--- | :--- | ---: | ---: | ---: | ---: | ---: | :--- | :--- | ---: | :--- |',
      '| Alpha | AppServerControl | Streaming | 1420 | 18233 | 68% | 118 ms | 880 ms | gpt-5.4 | Codex | 0 | Long answer with code and tables kept live while the operator watches scrollback |',
      '| Beta | Terminal | Idle | 0 | 932 | 12% | 74 ms | 140 ms | none | Human | 1 | Waiting for next prompt and preserving shell ownership |',
      '| Gamma | AppServerControl | Blocked | 17 | 4112 | 31% | 129 ms | 1420 ms | gpt-5.4-mini | Codex | 3 | Approval request open and should stay visible even when the assistant lane is busy |',
      '| Delta | AppServerControl | Replaying | 921 | 15540 | 54% | 105 ms | 650 ms | claude-opus | Claude | 0 | Canonical history restored from MidTerm and replayed into the history lane |',
      '',
      '| Metric | P50 | P95 | P99 | Target | Last good build | Regressed by | Notes |',
      '| --- | ---: | ---: | ---: | ---: | :--- | :--- | :--- |',
      '| First paint | 118 ms | 212 ms | 356 ms | 150 ms | v8.7.41-dev | +9 ms | Still acceptable in the local source loop |',
      '| AppServerControl attach | 420 ms | 880 ms | 1420 ms | 600 ms | v8.7.39-dev | +140 ms | Regression only visible on native-runtime-blocked sessions |',
      '| Snapshot rebuild | 34 ms | 68 ms | 110 ms | 50 ms | v8.7.50-dev | -6 ms | Fast enough once canonical history exists |',
      '',
      '| Render mode | Benefit | Risk |',
      '| :--- | :--- | :--- |',
      '| Virtual window | Keeps long histories fast | Needs stable bottom pinning |',
      '| Inline tables | Preserves structure for operators | Can overflow on mobile without scroll container |',
    ].join('\n'),
    currentTurnState: 'completed',
    currentTurnStateLabel: 'Completed',
  };
}

function buildLongDebugScenario(
  createItem: DebugScenarioItemFactory,
  at: (offsetMs: number) => string,
): DebugScenarioContent {
  return {
    items: Array.from({ length: 140 }, (_value, index) => {
      const isUser = index % 2 === 0;
      const ordinal = index + 1;
      const body = isUser
        ? `Prompt ${ordinal}: summarize lane ${Math.floor(index / 2) + 1} and keep the history compact.`
        : [
            `Reply ${ordinal}: lane ${Math.floor(index / 2) + 1} is stable.`,
            '',
            ordinal % 10 === 1
              ? '| Check | Value |\n| :--- | ---: |\n| backlog | 7 |\n| diff hunks | 3 |'
              : 'Streaming stays smooth when cards remain narrow, labels stay quiet, and long histories virtualize cleanly.',
          ].join('\n');

      return createItem(
        `${isUser ? 'user' : 'assistant'}-debug-${ordinal}`,
        isUser ? 'user_message' : 'assistant_message',
        body,
        at(-240000 + index * 1200),
      );
    }),
    requests: [],
    assistantText: '',
    currentTurnState: 'completed',
    currentTurnStateLabel: 'Completed',
  };
}

function buildMassiveDebugScenario(
  createItem: DebugScenarioItemFactory,
  at: (offsetMs: number) => string,
): DebugScenarioContent {
  return {
    items: Array.from({ length: 10000 }, (_value, index) => {
      const ordinal = index + 1;
      const isUser = index % 4 === 0;
      const isTable = ordinal % 31 === 0;
      const detail = isUser
        ? `Prompt ${ordinal}: audit long-running lane ${Math.floor(index / 4) + 1} without losing scroll position.`
        : isTable
          ? [
              `Reply ${ordinal}: compact table checkpoint for lane ${Math.floor(index / 4) + 1}.`,
              '',
              '| Metric | Value | Budget |',
              '| :--- | ---: | ---: |',
              `| retained rows | ${64 + (ordinal % 9)} | 120 |`,
              `| visible rows | ${8 + (ordinal % 5)} | 24 |`,
              `| canonical index | ${ordinal} | 10000 |`,
            ].join('\n')
          : [
              `Reply ${ordinal}: lane ${Math.floor(index / 4) + 1} remains readable in a 10k item history.`,
              '',
              'The visible DOM should stay bounded, the progress rail should stay index-based, and local scrolling should not snap back while nearby rows materialize.',
            ].join('\n');

      return createItem(
        `${isUser ? 'user' : 'assistant'}-massive-${ordinal}`,
        isUser ? 'user_message' : 'assistant_message',
        detail,
        at(-3600000 + index * 250),
      );
    }),
    requests: [],
    assistantText: '',
    currentTurnState: 'completed',
    currentTurnStateLabel: 'Completed',
  };
}

function buildWorkflowDebugScenario(
  createItem: DebugScenarioItemFactory,
  at: (offsetMs: number) => string,
): DebugScenarioContent {
  return {
    items: [
      createItem(
        'user-debug-workflow',
        'user_message',
        'Audit the workspace, ask for the release mode, then patch the report and summarize the diff.',
        at(-150000),
      ),
    ],
    requests: [
      {
        requestId: 'request-debug-workflow',
        turnId: 'turn-debug',
        kind: 'interview',
        kindLabel: 'Question',
        state: 'open',
        decision: null,
        detail: 'The agent is blocked until the operator chooses the release posture.',
        questions: [
          {
            id: 'mode',
            question: 'Choose SAFE or FAST before I continue.',
            header: 'Release mode',
            multiSelect: false,
            options: [
              {
                label: 'SAFE',
                description: 'Validate carefully and preserve the current shape.',
              },
              {
                label: 'FAST',
                description: 'Move quickly and accept a rougher pass.',
              },
            ],
          },
        ],
        answers: [],
        updatedAt: at(-12000),
      },
    ],
    assistantText: [
      'Plan:',
      '1. Inspect the workspace state.',
      '2. Wait for the release mode.',
      '3. Apply the requested patch and summarize the diff.',
      '',
      '| file | status | owner |',
      '| :--- | :--- | :--- |',
      '| report.md | pending | Codex |',
      '| inventory.csv | reviewed | Operator |',
    ].join('\n'),
    currentTurnState: 'running',
    currentTurnStateLabel: 'Running',
  };
}

function buildMixedDebugScenario(
  createItem: DebugScenarioItemFactory,
  at: (offsetMs: number) => string,
  heroImageUrl: string,
): DebugScenarioContent {
  return {
    items: [
      createItem(
        'user-debug-mixed',
        'user_message',
        'Give me a power-user quality pass: smooth streaming, compact labels, readable tables, and inline media.',
        at(-120000),
      ),
    ],
    requests: [
      {
        requestId: 'request-debug-choice',
        turnId: 'turn-debug',
        kind: 'interview',
        kindLabel: 'User input',
        state: 'open',
        decision: null,
        detail: 'Pick the shipping posture for this polish pass.',
        questions: [
          {
            id: 'posture',
            question: 'Which rollout posture fits this history best?',
            header: 'Posture',
            multiSelect: false,
            options: [
              {
                label: 'Local proof',
                description: 'Validate in the source loop first.',
              },
              {
                label: 'Pre-release',
                description: 'Cut a dev build after browser proof.',
              },
            ],
          },
        ],
        answers: [],
        updatedAt: at(-15000),
      },
    ],
    assistantText: [
      'The current AppServerControl pass is tuned for operators instead of messenger chrome.',
      '',
      `![Inline AppServerControl media preview](${heroImageUrl})`,
      '',
      '| Surface | Goal | Status |',
      '| :--- | :--- | :---: |',
      '| History chrome | Stay quiet and readable | Good |',
      '| Streaming feel | Keep the answer alive while it grows | Live |',
      '| Tables | Preserve structure without blowing out the lane | Better |',
      '',
      '```ts',
      'const historyMode = "power-user";',
      'const keepLabelsQuiet = true;',
      '```',
      '',
      'Next I would pressure-test this with many long turns, wide tables, and mixed media so the renderer fails in development instead of production.',
    ].join('\n'),
    currentTurnState: 'running',
    currentTurnStateLabel: 'Running',
  };
}

function buildAppServerControlDebugScenarioContent(
  scenario: AppServerControlDebugScenarioName,
  createItem: DebugScenarioItemFactory,
  at: (offsetMs: number) => string,
  heroImageUrl: string,
): DebugScenarioContent {
  const builders: Record<AppServerControlDebugScenarioName, () => DebugScenarioContent> = {
    mixed: () => buildMixedDebugScenario(createItem, at, heroImageUrl),
    tables: () => buildTablesDebugScenario(createItem, at),
    long: () => buildLongDebugScenario(createItem, at),
    massive: () => buildMassiveDebugScenario(createItem, at),
    workflow: () => buildWorkflowDebugScenario(createItem, at),
  };

  return builders[scenario]();
}

export function buildAppServerControlDebugScenario(
  sessionId: string,
  scenario: AppServerControlDebugScenarioName,
  origin: string,
): {
  snapshot: AppServerControlHistorySnapshot;
} {
  const now = Date.now();
  const at = (offsetMs: number) => new Date(now + offsetMs).toISOString();
  const heroImageUrl = new URL('/img/logo.png', origin).href;

  const createItem = (
    itemId: string,
    itemType: string,
    detail: string,
    updatedAt: string,
  ): AppServerControlHistorySnapshot['items'][number] => ({
    itemId,
    turnId: 'turn-debug',
    itemType,
    status: 'completed',
    title: itemType === 'user_message' ? 'User message' : 'Assistant message',
    detail,
    attachments: [],
    updatedAt,
  });

  const { items, requests, assistantText, currentTurnState, currentTurnStateLabel } =
    buildAppServerControlDebugScenarioContent(scenario, createItem, at, heroImageUrl);
  const reasoningText =
    scenario === 'workflow'
      ? 'Need the operator choice before touching the file so the patch posture is explicit.'
      : '';
  const reasoningSummaryText =
    scenario === 'workflow'
      ? 'Waiting on SAFE/FAST, then update report.md and show the working diff.'
      : '';
  const planText =
    scenario === 'workflow'
      ? '1. Read the workspace.\n2. Ask for SAFE or FAST.\n3. Patch and summarize the diff.'
      : '';
  const commandOutput = scenario === 'workflow' ? 'status: TODO\nowner: codex' : '';
  const fileChangeOutput =
    scenario === 'workflow' ? 'Success. Updated the following files:\nM report.md' : '';
  const unifiedDiff =
    scenario === 'workflow'
      ? 'diff --git a/report.md b/report.md\n@@\n-status: TODO\n+status: DONE'
      : '';

  return {
    snapshot: {
      sessionId,
      provider: 'codex',
      generatedAt: at(0),
      latestSequence: 500,
      historyCount: items.length,
      historyWindowStart: 0,
      historyWindowEnd: items.length,
      hasOlderHistory: false,
      hasNewerHistory: false,
      session: {
        state: currentTurnState === 'running' ? 'running' : 'ready',
        stateLabel: currentTurnState === 'running' ? 'Running' : 'Ready',
        reason:
          scenario === 'long'
            ? 'Long synthetic history loaded for history virtualization.'
            : 'AppServerControl debug scenario loaded from the browser console.',
        lastError: null,
        lastEventAt: at(0),
      },
      thread: {
        threadId: `thread-debug-${scenario}`,
        state: 'active',
        stateLabel: 'Active',
      },
      currentTurn: {
        turnId: 'turn-debug',
        state: currentTurnState,
        stateLabel: currentTurnStateLabel,
        model: 'gpt-5.4',
        effort: 'high',
        startedAt: at(-90000),
        completedAt: currentTurnState === 'running' ? null : at(-5000),
      },
      quickSettings: {
        model: 'gpt-5.4',
        effort: 'high',
        planMode: scenario === 'workflow' ? 'on' : 'off',
        permissionMode: 'manual',
      },
      streams: {
        assistantText,
        reasoningText,
        reasoningSummaryText,
        planText,
        commandOutput,
        fileChangeOutput,
        unifiedDiff,
      },
      history: buildDebugScenarioHistory({
        generatedAt: at(0),
        turnId: 'turn-debug',
        currentTurnState,
        items,
        requests,
        assistantText,
        reasoningText,
        reasoningSummaryText,
        planText,
        commandOutput,
        fileChangeOutput,
        unifiedDiff,
      }),
      items,
      requests,
      notices: [],
    },
  };
}
