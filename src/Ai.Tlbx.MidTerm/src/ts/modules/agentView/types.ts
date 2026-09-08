import type {
  AppServerControlAttachmentReference,
  AppServerControlHistorySnapshot,
  AppServerControlInlineFileReference,
  AppServerControlInlineImagePreview,
  AppServerControlHistoryRuntimeNotice,
  AppServerControlQuestion,
  AppServerControlAnsweredQuestion,
  AppServerControlToolPresentation,
} from '../../api/types';

export interface SessionAppServerControlViewState {
  panel: HTMLDivElement;
  snapshot: AppServerControlHistorySnapshot | null;
  debugScenarioActive: boolean;
  activationRunId: number;
  historyViewport: HTMLDivElement | null;
  historyProgressNav: HTMLDivElement | null;
  historyProgressThumb: HTMLDivElement | null;
  historyEntries: AppServerControlHistoryEntry[];
  historyWindowStart: number;
  historyWindowCount: number;
  historyWindowTargetCount: number;
  historyViewportSyncPending: boolean;
  historyViewportSyncForcePending: boolean;
  historyViewportSyncQueuedDuringRefresh: boolean;
  historyViewportSyncSuppressUntil: number;
  disconnectStream: (() => void) | null;
  streamConnected: boolean;
  refreshInFlight: boolean;
  requestBusyIds: Set<string>;
  requestDraftAnswersById: Record<string, Record<string, string[]>>;
  requestQuestionIndexById: Record<string, number>;
  historyScrollMode: HistoryScrollMode;
  historyAutoScrollPinned: boolean;
  historyLastScrollMetrics: HistoryScrollMetrics | null;
  historyLastUserScrollIntentAt: number;
  historyLastUserScrollDirection: -1 | 0 | 1;
  historyLastVoidSyncScrollTop: number | null;
  historyWindowRevision: string | null;
  historyWindowViewportWidth: number | null;
  historyNavigatorMode: HistoryNavigatorMode;
  historyNavigatorAnchorIndex: number | null;
  historyNavigatorDragTargetIndex: number | null;
  historyNavigatorQueuedTargetIndex: number | null;
  historyNavigatorQueuedRequestKind: HistoryNavigatorRequestKind | null;
  historyNavigatorPreviewHandle: number | null;
  historyNavigatorHydrateHandle: number | null;
  historyNavigatorLastPreviewRequestAt: number;
  historyPendingJumpTargetIndex: number | null;
  historyPendingJumpAlign: HistoryJumpAlign | null;
  historyRenderScheduled: number | null;
  historyRenderBatchHandle: number | null;
  activationState:
    | 'idle'
    | 'opening'
    | 'attaching'
    | 'waiting-history-window'
    | 'connecting-stream'
    | 'ready'
    | 'failed';
  activationDetail: string;
  activationTrace: AppServerControlActivationTraceEntry[];
  activationError: string | null;
  activationIssue: AppServerControlActivationIssue | null;
  activationActionBusy: boolean;
  optimisticTurns: PendingAppServerControlTurn[];
  renderDirty: boolean;
  assistantMarkdownCache: Map<string, AssistantMarkdownCacheEntry>;
  historyRenderedNodes: Map<string, HistoryRenderedNode>;
  historyMeasuredHeights: Map<string, number>;
  historyObservedHeights: Map<string, number>;
  historyMeasuredHeightsByBucket: Map<number, Map<string, number>>;
  historyObservedHeightsByBucket: Map<number, Map<string, number>>;
  historyObservedHeightSamplesByBucket: Map<number, Map<string, number[]>>;
  historyMeasuredWidthBucket: number;
  historyMeasurementObserver: ResizeObserver | null;
  historyViewportResizeObserver: ResizeObserver | null;
  historyViewportSize: HistoryViewportSize | null;
  historyLeadingPlaceholders: HTMLDivElement[];
  historyTrailingPlaceholders: HTMLDivElement[];
  historyEmptyState: HTMLDivElement | null;
  pendingHistoryPrependAnchor: HistoryViewportAnchor | null;
  pendingHistoryLayoutAnchor: HistoryViewportAnchor | null;
  historyLastVirtualWindowKey: string | null;
  historyExpandedEntries: Set<string>;
  runtimeStats: AppServerControlRuntimeStatsSummary | null;
  busyIndicatorTickHandle: number | null;
  completedTurnDurationEntries: Map<string, AppServerControlHistoryEntry>;
}

export interface AppServerControlRuntimeStatsSummary {
  windowUsedTokens: number | null;
  windowTokenLimit: number | null;
  accumulatedInputTokens: number;
  accumulatedOutputTokens: number;
  primaryRateLimitUsedPercent: number | null;
  secondaryRateLimitUsedPercent: number | null;
}

export type HistoryScrollMode = 'follow' | 'browse' | 'restore-anchor';
export type HistoryNavigatorMode = 'follow-live' | 'browse' | 'drag-preview';
export type HistoryNavigatorRequestKind = 'preview' | 'hydrate';
export type HistoryJumpAlign = 'top' | 'center' | 'bottom';

export interface PendingAppServerControlTurn {
  optimisticId: string;
  turnId: string | null;
  text: string;
  attachments: AppServerControlAttachmentReference[];
  submittedAt: string;
  status: 'submitted' | 'accepted';
}

export interface AppServerControlActivationTraceEntry {
  tone: HistoryTone;
  meta: string;
  summary: string;
  detail: string;
}

export interface AssistantMarkdownCacheEntry {
  body: string;
  html: string;
  fileMentionToken: string;
}

export type HistoryKind =
  'user' | 'assistant' | 'reasoning' | 'tool' | 'request' | 'plan' | 'diff' | 'system' | 'notice';

export type HistoryTone = 'info' | 'positive' | 'warning' | 'attention';
export type AppServerControlHistoryActionId = 'retry-appServerControl';
export type AppServerControlLayoutMode = 'default' | 'full-width-left';

export interface AppServerControlHistoryAction {
  id: AppServerControlHistoryActionId;
  label: string;
  style: 'primary' | 'secondary';
  busyLabel?: string;
}

export interface AppServerControlActivationIssue {
  kind:
    | 'busy-terminal-turn'
    | 'missing-resume-id'
    | 'shell-recovery-failed'
    | 'native-runtime-unavailable'
    | 'readonly-history'
    | 'startup-failed';
  tone: HistoryTone;
  meta: string;
  title: string;
  body: string;
  actions: AppServerControlHistoryAction[];
}

export interface AppServerControlHistoryEntry {
  id: string;
  order: number;
  estimatedHeightPx?: number;
  kind: HistoryKind;
  tone: HistoryTone;
  label: string;
  title: string;
  body: string;
  meta: string;
  requestId?: string;
  attachments?: AppServerControlAttachmentReference[];
  actions?: AppServerControlHistoryAction[];
  live?: boolean;
  pending?: boolean;
  sourceItemId?: string | null;
  sourceTurnId?: string | null;
  sourceItemType?: string | null;
  sourceStatus?: string;
  sourceUpdatedAt?: string;
  questions?: AppServerControlQuestion[];
  answers?: AppServerControlAnsweredQuestion[];
  busyIndicator?: boolean;
  busyElapsedText?: string | null;
  turnDurationNote?: boolean;
  commandText?: string | null;
  commandOutputTail?: string[];
  toolPresentation?: AppServerControlToolPresentation | null;
  fileMentions?: AppServerControlInlineFileReference[];
  imagePreviews?: AppServerControlInlineImagePreview[];
}

export interface HistoryVirtualWindow {
  start: number;
  end: number;
  topSpacerPx: number;
  bottomSpacerPx: number;
}

export interface HistoryIndexRange {
  start: number;
  end: number;
}

export interface HistoryViewportMetrics {
  scrollTop: number;
  clientHeight: number;
  clientWidth: number;
}

export interface HistoryViewportSize {
  width: number;
  height: number;
}

export interface HistoryScrollMetrics {
  scrollTop: number;
  clientHeight: number;
  scrollHeight: number;
}

export interface HistoryVisibleEntry {
  key: string;
  entry: AppServerControlHistoryEntry;
  cluster: ArtifactClusterInfo | null;
  showAssistantBadge: boolean;
  signature: string;
}

export interface HistoryPlaceholderBlock {
  key: string;
  heightPx: number;
  itemCount: number;
  direction: 'earlier' | 'later';
  label: string;
  rangeLabel: string;
}

export interface HistoryRenderPlan {
  emptyStateText: string | null;
  virtualWindowKey: string | null;
  leadingPlaceholders: HistoryPlaceholderBlock[];
  trailingPlaceholders: HistoryPlaceholderBlock[];
  visibleEntries: HistoryVisibleEntry[];
}

export interface HistoryRenderedNode {
  node: HTMLElement;
  signature: string;
  entry: AppServerControlHistoryEntry;
  cluster: ArtifactClusterInfo | null;
  lastMeasuredWidthBucket: number | null;
}

export interface HistoryViewportAnchor {
  entryId: string;
  topOffsetPx: number;
  absoluteIndex: number;
}

export interface HistoryBodyPresentation {
  mode: 'plain' | 'monospace' | 'markdown' | 'streaming' | 'diff' | 'command' | 'tool';
  collapsedByDefault: boolean;
  lineCount: number;
  preview: string;
}

export interface ArtifactClusterInfo {
  position: 'single' | 'start' | 'middle' | 'end';
  label: string | null;
  count: number;
  onlyTools: boolean;
}

export interface CommandToken {
  text: string;
  kind: 'command' | 'parameter' | 'string' | 'operator' | 'text' | 'whitespace';
}

export interface DiffRenderLine {
  text: string;
  className: string;
  oldLineNumber?: number;
  newLineNumber?: number;
}

export interface AppServerControlNoticeEntry {
  id: string;
  order: number;
  title: string;
  meta: string;
  notice: AppServerControlHistoryRuntimeNotice;
}
