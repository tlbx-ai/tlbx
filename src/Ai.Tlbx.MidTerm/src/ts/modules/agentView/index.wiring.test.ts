import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const css = readFileSync(path.join(__dirname, '../../../static/css/app.css'), 'utf8');
const appServerControlDesign = readFileSync(
  path.join(__dirname, '../../../../../../docs/AgentControllerSessionDesign.md'),
  'utf8',
);
const indexSource = readFileSync(path.join(__dirname, 'index.ts'), 'utf8');
const historyContentSource = readFileSync(path.join(__dirname, 'historyContent.ts'), 'utf8');
const historyDomSource = readFileSync(path.join(__dirname, 'historyDom.ts'), 'utf8');
const historyRenderSource = readFileSync(path.join(__dirname, 'historyRender.ts'), 'utf8');
const historyProcessingSource = readFileSync(path.join(__dirname, 'historyProcessing.ts'), 'utf8');
const focusReclaimSource = readFileSync(
  path.join(__dirname, '../terminal/focusReclaim.ts'),
  'utf8',
);
const viewShellSource = readFileSync(path.join(__dirname, 'viewShell.ts'), 'utf8');

describe('agent view AppServerControl wiring', () => {
  it('keeps Codex AppServerControl user and assistant metadata above the message body', () => {
    expect(css).toMatch(
      /\.agent-view-panel\[data-appServerControl-layout='full-width-left'\]\s+\.agent-history-user\s+\.agent-history-header,\s*\.agent-view-panel\[data-appServerControl-layout='full-width-left'\]\s+\.agent-history-assistant\s+\.agent-history-header\s*\{[^}]*justify-content:\s*flex-start;[^}]*\}/s,
    );
  });

  it('keeps AppServerControl history labels and timestamps left-bound instead of right-bound', () => {
    expect(css).toMatch(
      /\.agent-history-user \.agent-history-header,\s*\.agent-history-assistant \.agent-history-header\s*\{[^}]*justify-content:\s*flex-start;/s,
    );
    expect(css).toMatch(/\.agent-history-meta\s*\{[^}]*margin-left:\s*0;/s);
    expect(appServerControlDesign).toContain(
      'No Agent Controller Session history row should right-align its header labels or timestamps.',
    );
    expect(appServerControlDesign).toContain(
      'Agent Controller Session history headers no longer right-bind labels or timestamps;',
    );
  });

  it('keeps full-width AppServerControl metadata compact while allowing the first assistant row in a turn to show Agent', () => {
    expect(css).toMatch(
      /\.agent-view-panel\[data-appServerControl-layout='full-width-left'\]\s+\.agent-history-badge-user,\s*\.agent-view-panel\[data-appServerControl-layout='full-width-left'\]\s+\.agent-history-badge-assistant\s*\{/s,
    );
    expect(css).toMatch(
      /\.agent-view-panel\[data-appServerControl-layout='full-width-left'\]\s+\.agent-history-user\s+\.agent-history-meta,\s*\.agent-view-panel\[data-appServerControl-layout='full-width-left'\]\s+\.agent-history-assistant\s+\.agent-history-meta\s*\{/s,
    );
    expect(css).toContain('font-size: 9px;');
    expect(css).toContain(
      ".agent-view-panel[data-appServerControl-layout='full-width-left'] .agent-history-badge-assistant {",
    );
    expect(css).toContain('display: none;');
    expect(css).toContain(".agent-history-badge-assistant[data-visible='true'] {");
    expect(css).toContain('color: var(--agent-appServerControl-assistant-label);');
    expect(css).toMatch(
      /:root:not\(\[data-agent-show-message-timestamps='true'\]\)\s+\.agent-history-assistant\s+\.agent-history-meta\s*\{/,
    );
  });

  it('uses the terminal font stack and terminal size for user and assistant prompts while machine rows stay on terminal monospace', () => {
    expect(css).toMatch(/--agent-history-message-font-family:\s*var\(\s*--terminal-font-family,/);
    expect(css).toContain(
      '--agent-history-mono-font-family: var(--terminal-font-family, var(--font-mono));',
    );
    expect(css).toContain('.agent-history-user .agent-history-body {');
    expect(css).toContain('.agent-history-assistant .agent-history-body {');
    expect(css).toContain('font-size: var(--terminal-font-size, 16px);');
    expect(css).toContain('font-weight: var(--terminal-font-weight, normal);');
    expect(css).toContain('letter-spacing: var(--terminal-letter-spacing, 0px);');
    expect(css).toContain('line-height: var(--terminal-line-height, 1);');
    expect(viewShellSource).not.toContain('--agent-font-family');
  });

  it('documents the above-body metadata rule in the AppServerControl design contract', () => {
    expect(appServerControlDesign).toContain(
      'In Codex Agent Controller Session, user rows should place their quiet role label and timestamp above the message body, not below it.',
    );
    expect(appServerControlDesign).toContain(
      'the quiet role label should remain on user rows, while assistant rows should omit a repeated `Agent` label',
    );
    expect(appServerControlDesign).toContain(
      'assistant rows should place any optional timestamp above the message body when that preference is enabled',
    );
    expect(appServerControlDesign).toContain(
      'the first assistant message row of a new turn should show a quiet `Agent` badge',
    );
  });

  it('keeps system runtime rows quieter and documents sanitized runtime notices', () => {
    expect(css).toContain('.agent-history-system {');
    expect(css).toContain('.agent-history-system .agent-history-badge-system {');
    expect(css).toContain('.agent-history-system .agent-history-meta {');
    expect(css).toContain('.agent-history-system .agent-history-body {');
    expect(css).toContain('font-size: 0.81rem;');
    expect(appServerControlDesign).toContain(
      'Runtime/system notices should strip raw ANSI/control bytes and de-duplicate repeated message/detail fragments before they render in Agent Controller Session history.',
    );
    expect(appServerControlDesign).toContain(
      'runtime/system notice text is sanitized for ANSI/control-byte noise, de-duplicates repeated message/detail payloads, and system rows render with quieter metadata/body emphasis than the main conversation lane',
    );
  });

  it('documents quiet agent-state rows and stronger red agent-error rows', () => {
    expect(css).toContain('.agent-history-notice.agent-history-attention {');
    expect(css).toContain(
      '.agent-history-notice.agent-history-attention .agent-history-badge-notice {',
    );
    expect(css).toContain('.agent-history-notice.agent-history-attention .agent-history-body {');
    expect(appServerControlDesign).toContain('quiet canonical `Agent State` system rows');
    expect(appServerControlDesign).toContain(
      'canonical `Agent Error` notice rows with stronger red emphasis',
    );
    expect(historyDomSource).toContain(
      "if (entry.kind !== 'user' && entry.kind !== 'assistant' && entry.label.trim()) {",
    );
  });

  it('binds the AppServerControl pane background to terminal transparency tokens', () => {
    expect(css).toMatch(/\.agent-view-panel\s*\{[\s\S]*?background:\s*transparent;/);
    expect(css).toMatch(
      /\.agent-chat-shell\s*\{[\s\S]*?background:\s*[\s\S]*?linear-gradient\(\s*var\(--terminal-canvas-background,\s*var\(--terminal-bg\)\),\s*var\(--terminal-canvas-background,\s*var\(--terminal-bg\)\)\s*\),\s*[\s\S]*?linear-gradient\(\s*var\(--terminal-canvas-background,\s*var\(--terminal-bg\)\),\s*var\(--terminal-canvas-background,\s*var\(--terminal-bg\)\)\s*\),\s*[\s\S]*?var\(--terminal-canvas-background,\s*var\(--terminal-bg\)\);/,
    );
    expect(appServerControlDesign).toContain(
      'Agent Controller Session pane background/transparency should follow the terminal transparency model, not the surrounding generic UI shell transparency model.',
    );
    expect(appServerControlDesign).toContain(
      'Agent Controller Session must match the effective stacked xterm background users see behind terminal text, not a single token layer.',
    );
  });

  it('keeps the history shell flat instead of reintroducing wrapper cards', () => {
    expect(indexSource).not.toContain('agent-history-card');
    expect(appServerControlDesign).toContain(
      'Codex/Claude history rows now render with a flatter console-like surface and remove the remaining card/bubble chrome while the renderer is being hardened',
    );
  });

  it('replaces changed history rows instead of mutating past DOM nodes in place', () => {
    expect(indexSource).not.toContain('updateHistoryEntryNode(');
    expect(appServerControlDesign).toContain(
      'Future updates must not mutate an already-rendered older row into a different row identity.',
    );
  });

  it('does not force AppServerControl back to the live edge on tab deactivate or foreground recovery', () => {
    const deactivateBlock = indexSource.match(
      /onTabDeactivated\('agent', \(sessionId\) => \{[\s\S]*?\n  \}\);/,
    );
    expect(deactivateBlock?.[0]).toBeTruthy();
    expect(deactivateBlock?.[0]).not.toContain("setHistoryScrollMode(state, 'follow');");
    expect(indexSource).toContain('void refreshAppServerControlSnapshot(sessionId, {');
    expect(indexSource).toContain('latestWindow: state.historyAutoScrollPinned,');
  });

  it('keeps the progress navigator stateful in layout and expands its touch target on mobile', () => {
    expect(historyRenderSource).not.toContain('host.hidden = historyCount <= 0;');
    expect(viewShellSource).not.toContain('repairAgentViewSkeleton(');
    expect(viewShellSource).not.toContain('history-index-scroll');
    expect(css).toMatch(/\.agent-history-entry\s*\{[\s\S]*?flex:\s*0 0 auto;/);
    expect(css).toMatch(/\.agent-history-placeholder\s*\{[\s\S]*?flex:\s*0 0 auto;/);
    expect(css).toContain('flex: 0 0 12px;');
    expect(css).toContain('width: 2px;');
    expect(css).toContain(
      '--agent-history-progress-surface: var(--surface-0, var(--btn-secondary));',
    );
    expect(css).toContain('var(--agent-history-progress-surface) 86%');
    expect(css).toContain(".agent-history-progress-nav[data-ready='false'] {");
    expect(css).toContain(
      ".agent-history-progress-nav[data-ready='false'] .agent-history-progress-thumb {",
    );
    expect(css).toContain('opacity: 0;');
    expect(viewShellSource.indexOf('data-agent-field="scroll-to-bottom"')).toBeGreaterThan(
      viewShellSource.indexOf('class="agent-history-shell"'),
    );
    expect(viewShellSource.indexOf('data-agent-field="scroll-to-bottom"')).toBeLessThan(
      viewShellSource.indexOf('class="agent-composer-shell"'),
    );
    expect(css).toMatch(/\.agent-history-shell\s*\{[\s\S]*?position:\s*relative;/);
    expect(css).toMatch(
      /\.agent-scroll-to-bottom\s*\{[\s\S]*?min-height:\s*var\(--command-bay-automation-chip-height,\s*30\.6px\);[\s\S]*?border-radius:\s*var\(--command-bay-pill-radius,\s*8px\);[\s\S]*?background:\s*var\(--command-bay-ui-reactive-surface,/s,
    );
    expect(css).toMatch(/\.agent-scroll-to-bottom\s*\{[\s\S]*?bottom:\s*12px;/);
    expect(css).not.toContain('bottom: calc(20px + var(--adaptive-footer-reserved-height, 0px));');
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.agent-history-progress-nav \{[\s\S]*?flex: 0 0 44px;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.agent-history-progress-nav \{[\s\S]*?background: transparent;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.agent-history-progress-thumb \{[\s\S]*?min-height: 48px;/s,
    );
    expect(appServerControlDesign).toContain(
      'The progress navigator should remain a persistent Agent Controller Session-owned rail in layout.',
    );
    expect(appServerControlDesign).toContain(
      'On touch-sized viewports, the progress navigator should expose at least a 44px touch target',
    );
    expect(appServerControlDesign).toContain(
      'On desktop, the progress navigator should stay visually recessive: a thin low-chrome rail with a darker thumb',
    );
    expect(appServerControlDesign).toContain(
      'The visible progress thumb should top-clamp when the pane itself is top-clamped on the first canonical history item',
    );
    expect(appServerControlDesign).toContain(
      'Any browser-side visible-range math that feeds navigator position, fetch policy, or tracing must resolve the actual on-screen slice',
    );
    expect(appServerControlDesign).toContain(
      'the progress navigator now stays in layout as a stateful Agent Controller Session rail instead of relying on `hidden` attribute toggles for visibility',
    );
    expect(historyRenderSource).toContain(
      'if (metrics.scrollTop <= HISTORY_PROGRESS_TOP_ALIGN_THRESHOLD_PX)',
    );
    expect(historyRenderSource).toContain('resolveHistoryNavigatorConcreteAnchorIndex');
    expect(historyRenderSource).toContain('refreshAnchorFromViewport');
    expect(historyRenderSource).toContain('state.historyNavigatorAnchorIndex');
    expect(appServerControlDesign).toContain(
      'Passive row-height measurement, image/content reflow, window hydration, and virtualization rerenders must not recompute the dedicated history scrollbar thumb',
    );
    expect(appServerControlDesign).toContain(
      'passive Agent Controller Session row-height remeasurement no longer backpressures the dedicated progress navigator',
    );
  });

  it('lets normal history scrolling retake the progress thumb after a completed navigator drag', () => {
    expect(indexSource).toContain('let activeHistoryNavigatorPointerId: number | null = null;');
    expect(indexSource).toContain('activeHistoryNavigatorPointerId === null');
    expect(indexSource).toMatch(
      /current\.historyNavigatorMode !== 'drag-preview' \|\|[\s\S]*?activeHistoryNavigatorPointerId === null[\s\S]*?\{[\s\S]*?current\.historyNavigatorMode = 'browse';[\s\S]*?current\.historyNavigatorDragTargetIndex = null;/,
    );
    expect(indexSource).toMatch(
      /updateNavigatorTarget\(event\.clientY, true\);[\s\S]*?activeHistoryNavigatorPointerId = null;[\s\S]*?activeHistoryNavigatorThumbOffsetPx = null;/,
    );
  });

  it('styles command-execution rows as console-like Ran blocks with terminal monospace', () => {
    expect(css).toContain('.agent-history-command-body {');
    expect(css).toContain('.agent-history-command-entry {');
    expect(css).toContain('.agent-history-command-entry .agent-history-header,');
    expect(css).toContain('background: transparent;');
    expect(css).toContain('border: 0;');
    expect(css).toContain('font-family: var(--agent-history-mono-font-family, var(--font-mono));');
    expect(css).toContain('.agent-history-command-prefix {');
    expect(css).toContain('color: var(--text-primary);');
    expect(css).toContain('.agent-history-command-token-command {');
    expect(css).toContain('.agent-history-command-output-tail {');
    expect(appServerControlDesign).toContain(
      'Command-execution rows should render in a console-like `Ran …` form',
    );
    expect(appServerControlDesign).toContain('Command-execution rows should remain fully flat.');
    expect(appServerControlDesign).toContain('fold up to 12 tail lines');
    expect(appServerControlDesign).toContain(
      'Folded command-output tails should remain raw terminal text.',
    );
    expect(appServerControlDesign).toContain('must not downgrade it back into a generic tool row');
    expect(historyDomSource).toContain(
      'Keep folded command tails as raw terminal text instead of applying',
    );
    expect(historyDomSource).not.toContain(
      "enrichInteractiveTextContent(output, getEntryFileMentions(entry, 'body'));",
    );
    expect(historyDomSource).not.toContain('wireAssistantInteractiveContent(output, sessionId);');
  });

  it('keeps machine-oriented history rows flat and dense', () => {
    expect(css).toContain('.agent-history-tool .agent-history-body,');
    expect(css).toContain('.agent-history-diff .agent-history-body {');
    expect(css).toContain('border: 0;');
    expect(css).toContain('border-radius: 0;');
    expect(css).toContain('box-shadow: none;');
    expect(css).toContain('.agent-history-markdown p,');
    expect(css).toContain('.agent-history-markdown .agent-markdown-gap {');
    expect(css).toContain('height: calc(0.25em * var(--agent-markdown-gap-lines, 1));');
    expect(css).toContain('.agent-history-markdown li + li {');
    expect(css).toContain('.agent-history-markdown li {');
    expect(css).toContain('padding-inline-start: 1.65rem;');
    expect(css).toContain('.agent-history-markdown ul > li::before {');
    expect(css).toContain(".agent-history-markdown ul > li::before {\n  content: '•';");
    expect(css).toContain('.agent-history-markdown ol {');
    expect(css).toContain('counter-reset: agent-history-ordered-list;');
    expect(css).toContain('.agent-history-markdown ol > li::before {');
    expect(css).toContain('counter(agent-history-ordered-list)');
    expect(appServerControlDesign).toContain(
      'Agent Controller Session should not draw decorative card outlines, rounded shells, or inset border treatments around machine-oriented history rows.',
    );
    expect(appServerControlDesign).toContain(
      'Markdown paragraph and list spacing should be dense and terminal-like.',
    );
    expect(appServerControlDesign).toContain(
      'Blank-line paragraph breaks in assistant markdown should stay much tighter than prose defaults',
    );
    expect(appServerControlDesign).toContain(
      'Assistant markdown should model those blank-line pauses explicitly as compact gap markers',
    );
    expect(appServerControlDesign).toContain('Bullet and numbered lists should stack compactly');
    expect(css).toContain('.agent-history-markdown .agent-markdown-table-wrap {');
    expect(css).toContain('inline-size: fit-content;');
    expect(css).toContain('.agent-history-markdown .agent-markdown-table-sort {');
    expect(css).toContain('.agent-history-markdown .agent-markdown-table-filter {');
    expect(historyDomSource).toContain('wireMarkdownTables(content, {');
    expect(appServerControlDesign).toContain(
      'Markdown tables should stay left-anchored and use intrinsic width when their content is narrow',
    );
    expect(appServerControlDesign).toContain(
      'Assistant markdown tables should expose compact per-column sort and filter controls in the header row',
    );
    expect(appServerControlDesign).toContain(
      'Fenced CSV blocks in assistant markdown should render through that same interactive table treatment',
    );
  });

  it('documents live assistant markdown rendering without degrading into raw text', () => {
    expect(css).toContain('.agent-history-inline-link {');
    expect(css).toMatch(/text-decoration:\s*underline\s+dotted/);
    expect(css).toContain('.agent-history-inline-previews {');
    expect(css).toContain('.agent-history-inline-preview-frame {');
    expect(css).toContain('.agent-history-attachment-image-frame {');
    expect(css).toContain('object-fit: contain;');
    expect(historyDomSource).toContain("case 'streaming':");
    expect(historyDomSource).toContain("case 'markdown':");
    expect(appServerControlDesign).toContain(
      'Streaming assistant text should render through the same markdown surface as settled assistant output',
    );
    expect(appServerControlDesign).toContain(
      'must preserve the same markdown-rendered body instead of downgrading the row to raw plain text',
    );
    expect(appServerControlDesign).toContain(
      'Assistant-only semantic tinting should remain subtle.',
    );
    expect(appServerControlDesign).toContain(
      'Image previews should preserve the full image bounds inside a bounded frame instead of center-cropping portrait screenshots or photos.',
    );
  });

  it('styles diff rows as Edited path headers with tight colored hunk blocks', () => {
    expect(css).toContain('.agent-history-diff-line-file {');
    expect(css).toContain('.agent-history-diff-line-add {');
    expect(css).toContain('.agent-history-diff-line-delete {');
    expect(css).toContain(".agent-history-diff-line[data-has-line-numbers='true'] {");
    expect(css).toContain('.agent-history-diff-line-gutter {');
    expect(css).toContain('.agent-history-diff-line-number-old {');
    expect(css).toContain('.agent-history-diff-line-number-new {');
    expect(historyContentSource).toContain('Edited ${displayPath}');
    expect(appServerControlDesign).toContain(
      'Diff file headers should read like console work artifacts',
    );
    expect(appServerControlDesign).toContain(
      'That diff line-number gutter should stay structurally consistent across context, removed, and added lines;',
    );
    expect(appServerControlDesign).toContain(
      'Command-execution rows and diff rows should not repeat timestamp meta.',
    );
  });

  it('keeps runtime token stats in a compact hovering overlay instead of history rows', () => {
    expect(viewShellSource).toContain('data-agent-field="runtime-stats"');
    expect(indexSource).toContain('buildAppServerControlRuntimeStats(snapshot)');
    expect(historyDomSource).toContain('formatTokenWindowCompact(stats)');
    expect(css).toContain('.agent-runtime-stats {');
    expect(css).toContain('.agent-runtime-stats-detail {');
    expect(appServerControlDesign).toContain(
      'Codex runtime bookkeeping notices such as context-window updates and rate-limit updates should not render as history rows.',
    );
    expect(appServerControlDesign).toContain(
      'Agent Controller Session should expose that telemetry in a compact hovering stats display',
    );
    expect(appServerControlDesign).toContain(
      'Virtualizer diagnostics should stay in traces, tests, and developer tooling rather than as a persistent Agent Controller Session overlay.',
    );
  });

  it('documents the selection-preservation rule for passive AppServerControl rerenders', () => {
    expect(indexSource).toContain('hasActiveAppServerControlSelectionInPanel');
    expect(focusReclaimSource).toContain("element.closest?.('.agent-view-panel') != null");
    expect(appServerControlDesign).toContain(
      'Passive rerenders must not clear an active text selection inside Agent Controller Session.',
    );
  });

  it('documents the current AppServerControl usability floor and canonical-only retention stance', () => {
    expect(appServerControlDesign).toContain(
      'the visible result after those changes must not regress below the current Agent Controller Session floor',
    );
    expect(appServerControlDesign).toContain(
      'persistent `Ran …` command rows with folded output tails',
    );
    expect(appServerControlDesign).toContain(
      'deterministic older-history paging through a bounded virtualized window',
    );
    expect(appServerControlDesign).toContain(
      'Raw provider inputs are transient reducer inputs, not retained Agent Controller Session history.',
    );
    expect(appServerControlDesign).toContain(
      'it should be dropped instead of preserved in a hidden Agent Controller Session data layer.',
    );
  });

  it('renders the busy indicator as Working with a slower KITT mask sweep animation', () => {
    expect(historyProcessingSource).toContain(
      "appServerControlText('appServerControl.status.working', 'Working')",
    );
    expect(historyProcessingSource).toContain(
      'resolveBusyIndicatorLabelFromSnapshotItems(snapshot)',
    );
    expect(historyProcessingSource).toContain('BUSY_INDICATOR_EXCLUDED_ITEM_TYPES');
    expect(historyDomSource).toContain("labelBase.className = 'agent-history-busy-label-base'");
    expect(historyDomSource).toContain("labelGlow.className = 'agent-history-busy-label-glow'");
    expect(historyDomSource).toContain('BUSY_SWEEP_WALLCLOCK_CYCLE_MS');
    expect(historyDomSource).toContain(
      'const BUSY_SWEEP_WALLCLOCK_LEG_MS = BUSY_SWEEP_WALLCLOCK_CYCLE_MS / 2;',
    );
    expect(historyDomSource).toContain('--agent-busy-animation-delay-ms');
    expect(historyDomSource).toContain('--agent-busy-animation-duration-ms');
    expect(historyDomSource).toContain(
      'resolveWallclockAnimationDelayMs(BUSY_SWEEP_WALLCLOCK_CYCLE_MS)',
    );
    expect(historyDomSource).toContain('const BUSY_SWEEP_WALLCLOCK_CYCLE_MS = 4901;');
    expect(historyDomSource).toContain('glow.dataset.busyPhaseLocked');
    expect(historyDomSource).not.toContain('agent-history-busy-triangle');
    expect(historyDomSource).not.toContain('agent-history-busy-spinner');
    expect(historyDomSource).toContain('performance.timeOrigin + performance.now()');
    expect(historyDomSource).toContain('agent-history-busy-elapsed');
    expect(historyDomSource).toContain('(Press Esc to cancel)');
    expect(css).toContain('.agent-history-busy-bubble {');
    expect(css).toContain('justify-content: flex-start;');
    expect(css).toContain('.agent-history-busy-label {');
    expect(css).toContain('flex: 0 0 auto;');
    expect(css).toContain('white-space: pre;');
    expect(css).toContain('.agent-history-busy-label-base {');
    expect(css).toContain('.agent-history-busy-label-glow {');
    expect(css).toContain('.agent-history-busy-status {');
    expect(css).toContain('mask-size: 300% 100%;');
    expect(css).toContain('-webkit-mask-size: 300% 100%;');
    expect(css).not.toContain('agent-history-busy-spin');
    expect(css).not.toContain('.agent-history-busy-triangle');
    expect(css).toContain(
      'animation: agent-history-busy-sweep var(--agent-busy-animation-duration-ms, 2450.5ms) ease-in-out infinite alternate;',
    );
    expect(css).toContain('animation-delay: var(--agent-busy-animation-delay-ms, 0ms);');
    expect(css).toMatch(/mask-position:\s*66%\s*0;/);
    expect(css).toMatch(/mask-position:\s*34%\s*0;/);
    expect(css).toContain('.agent-history-busy-cancel {');
    expect(css).toContain('.agent-history-turn-duration-body {');
    expect(css).toContain('.agent-history-turn-duration-marker {');
    expect(css).toContain('.agent-history-turn-duration-segment {');
    expect(css).toContain('.agent-history-turn-duration-label {');
    expect(css).toContain('justify-content: center;');
    expect(css).toContain('width: 90%;');
    expect(historyDomSource).toContain('createTurnDurationNoteBody(entry)');
    expect(css).toContain('@keyframes agent-history-busy-sweep {');
    expect(appServerControlDesign).toContain(
      'When the provider exposes a live in-progress task/tool/reasoning detail label',
    );
    expect(appServerControlDesign).toContain(
      'User-prompt text and assistant-message text must not populate the busy bubble.',
    );
    expect(appServerControlDesign).toContain(
      'busy bubble should also show a muted wall-clock duration counter',
    );
    expect(appServerControlDesign).toContain('hint immediately after the animated label');
    expect(appServerControlDesign).toContain('mirror at the right edge and travel back left');
    expect(appServerControlDesign).toContain('append one muted inline duration note');
    expect(appServerControlDesign).toContain('near-full-width end-of-turn marker');
  });

  it('routes plain Escape from the AppServerControl surface through AppServerControl interruption', () => {
    expect(viewShellSource).toContain("panel.addEventListener('keydown', (event) => {");
    expect(viewShellSource).toContain("event.key !== 'Escape' ||");
    expect(viewShellSource).toContain('event.shiftKey ||');
    expect(viewShellSource).toContain('event.ctrlKey ||');
    expect(viewShellSource).toContain('event.altKey ||');
    expect(viewShellSource).toContain('event.metaKey');
    expect(indexSource).toContain('void handleAppServerControlEscape(targetSessionId);');
  });

  it('normalizes command-output transcript rows into persistent command presentations', () => {
    expect(historyProcessingSource).toContain('applyDirectCommandPresentation(mapped);');
    expect(historyContentSource).toContain('export function hasInlineCommandPresentation(');
    expect(historyContentSource).toContain('export function parseCommandOutputBody(');
    expect(historyProcessingSource).toContain('preservePersistentCommandEntries(');
    expect(historyProcessingSource).toContain("normalizedType !== 'commandoutput'");
  });
});
