import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { shouldShowManagerBar } from './visibility';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../../../..');
const managerBarSource = readFileSync(
  path.join(projectRoot, 'src/ts/modules/managerBar/managerBar.ts'),
  'utf8',
);

describe('manager bar visibility', () => {
  it('stays hidden when there is no active session', () => {
    expect(shouldShowManagerBar(true, null)).toBe(false);
  });

  it('shows only when enabled and a session is active', () => {
    expect(shouldShowManagerBar(true, 'session-1')).toBe(true);
    expect(shouldShowManagerBar(false, 'session-1')).toBe(false);
  });

  it('suppresses the Automation Bar and queued items while settings are open', () => {
    expect(managerBarSource).toContain('$settingsOpen');
    expect(managerBarSource).toContain('!$settingsOpen.get()');
    expect(managerBarSource).toContain('shouldShowManagerBar(settings?.managerBarEnabled');
    expect(managerBarSource).toContain('renderMobileButtons(visible ? renderedButtons : []);');
    expect(managerBarSource).toContain('!$settingsOpen.get() && activeSessionId');
    expect(managerBarSource).toContain('$settingsOpen.subscribe(() => {');
  });

  it('keeps the full button body as the primary click target', () => {
    expect(managerBarSource).toContain(
      "const button = target.closest<HTMLElement>('.manager-btn');",
    );
    expect(managerBarSource).not.toContain("const labelEl = target.closest('.manager-btn-label');");
  });

  it('uses a compact menu trigger before showing edit and remove actions', () => {
    expect(managerBarSource).toContain('class="manager-btn-menu"');
    expect(managerBarSource).toContain(
      'function toggleManagerActionMenu(anchor: HTMLButtonElement, actionId: string): void {',
    );
    expect(managerBarSource).toContain("popover.className = 'manager-bar-action-popover hidden';");
    expect(managerBarSource).toContain(
      "overflowBtn = document.getElementById('manager-bar-overflow') as HTMLButtonElement | null;",
    );
    expect(managerBarSource).toContain(
      'function toggleOverflowMenu(anchor: HTMLElement | null = null): void {',
    );
    expect(managerBarSource).toContain('function syncOverflowedButtons(): void {');
    expect(managerBarSource).toContain(
      "managerBar.classList.contains('hidden') && !isMobileSurface",
    );
    expect(managerBarSource).toContain(
      'function getAvailableManagerRailWidth(managerBar: HTMLElement, addButton: HTMLElement): number {',
    );
    expect(managerBarSource).toContain(
      'function shouldCollapseManagerButtonsToOverflow(managerBar: HTMLElement): boolean {',
    );
    expect(managerBarSource).toContain("footerDock?.dataset.device === 'mobile'");
    expect(managerBarSource).toContain("footerDock.dataset.surface === 'appServerControl'");
    expect(managerBarSource).toContain("buttonStrip.style.maxWidth = '';");
    expect(managerBarSource).toContain("buttonStrip.style.maxWidth = '0px';");
    expect(managerBarSource).toContain("overflowButton.setAttribute('hidden', '');");
    expect(managerBarSource).toContain(
      "const actionButton = target?.closest<HTMLButtonElement>('.manager-bar-overflow-item');",
    );
    expect(managerBarSource).toContain('runButton.dataset.actionId = action.id;');
    expect(managerBarSource).toContain(
      "const menuButton = target?.closest<HTMLButtonElement>('.manager-bar-overflow-item-menu');",
    );
    expect(managerBarSource).toContain("row.className = 'manager-bar-overflow-row';");
    expect(managerBarSource).toContain('menuButton.dataset.actionId = action.id;');
    expect(managerBarSource).toContain("'.manager-btn, .manager-bar-overflow-row'");
    expect(managerBarSource).toContain("element.classList.add('manager-btn-overflow-hidden');");
    expect(managerBarSource).toContain(
      "if (target?.closest('.manager-btn') || target?.closest('.manager-bar-action-popover')) {",
    );
    expect(managerBarSource).toContain("buttonsEl.addEventListener('pointerdown', (event) => {");
    expect(managerBarSource).toContain("if (!target?.closest('.manager-btn-menu')) {");
    expect(managerBarSource).toContain('event.stopPropagation();');
    expect(managerBarSource).toContain(
      'function resolveEventElement(target: EventTarget | null): Element | null {',
    );
    expect(managerBarSource).toContain('if (target instanceof Element) {');
    expect(managerBarSource).toContain('return target.parentElement;');
  });

  it('uses subpixel-aware overflow measurement before showing the overflow trigger', () => {
    expect(managerBarSource).toContain('const OVERFLOW_LAYOUT_EPSILON_PX = 0.75;');
    expect(managerBarSource).toContain('const measuredRailWidth = Math.max(');
    expect(managerBarSource).toContain(
      'const availableRailWidth = measuredRailWidth > 0 ? measuredRailWidth : railWidth;',
    );
    expect(managerBarSource).toContain('const addWidth = getMeasuredWidth(addButton);');
    expect(managerBarSource).toContain(
      'if (totalWidth <= fullAvailableWidth + OVERFLOW_LAYOUT_EPSILON_PX) {',
    );
  });

  it('guards burst enqueue clicks before sending duplicate queue requests', () => {
    expect(managerBarSource).toContain('const QUEUE_ENQUEUE_DEDUP_WINDOW_MS = 1500;');
    expect(managerBarSource).toContain('const pendingEnqueueGuards = new Map<string, number>();');
    expect(managerBarSource).toContain(
      'const enqueueGuardKey = buildEnqueueGuardKey(sessionId, action);',
    );
  });

  it('uses a direct queue cancel handler with optimistic removal state', () => {
    expect(managerBarSource).toContain('const pendingQueueRemovals = new Set<string>();');
    expect(managerBarSource).toContain("deleteBtn.addEventListener('click', (event) => {");
    expect(managerBarSource).toContain('pendingQueueRemovals.add(queueId);');
    expect(managerBarSource).toContain('removeCommandBayQueueEntry(queueId);');
  });

  it('collapses the automation bar to overflow and hides add button on mobile AppServerControl', () => {
    expect(managerBarSource).toContain(
      'function isMobileAppServerControlSurface(managerBar: HTMLElement): boolean {',
    );
    expect(managerBarSource).toContain(
      "addButton.classList.toggle('hidden', mobileAppServerControl);",
    );
    expect(managerBarSource).toContain("t('managerBar.addButton')");
    expect(managerBarSource).toContain('isMobileAppServerControlSurface(barEl)');
  });

  it('exports trigger hooks and proxy anchor for mobile status row proxy buttons', () => {
    expect(managerBarSource).toContain(
      'export function triggerAutomationOverflow(anchor: HTMLElement | null = null): void {',
    );
    expect(managerBarSource).toContain('export function triggerAddAutomation(): void {');
    expect(managerBarSource).toContain('export function setAutomationOverflowProxyAnchor(');
    expect(managerBarSource).toContain('let overflowProxyAnchorEl:');
    expect(managerBarSource).toContain('let activeOverflowAnchorEl:');
    expect(managerBarSource).toContain('resolveUsableOverflowAnchor(overflowProxyAnchorEl)');
    expect(managerBarSource).toContain('resolveUsableOverflowAnchor(overflowBtn)');
  });

  it('positions action popovers inside the visual viewport for mobile keyboard overlap', () => {
    expect(managerBarSource).toContain('function getVisualViewportBounds(): ViewportBounds {');
    expect(managerBarSource).toContain('const vv = window.visualViewport;');
    expect(managerBarSource).toContain('const viewport = getVisualViewportBounds();');
    expect(managerBarSource).toContain('viewport.bottom - triggerRect.bottom');
    expect(managerBarSource).toContain('triggerRect.top - viewport.top');
    expect(managerBarSource).toContain('viewport.right - viewportPadding - popoverRect.width');
  });

  it('renders prompt queue items beside automation items in the same queue surface', () => {
    expect(managerBarSource).toContain("if (entry.kind === 'prompt') {");
    expect(managerBarSource).toContain(
      'function describeQueuedPromptTitle(entry: ManagerBarQueueEntry): string {',
    );
    expect(managerBarSource).toContain("if (entry.kind === 'prompt' && entry.nextRunAt) {");
    expect(managerBarSource).toContain('function formatQueuedPromptRunAt(value: string): string {');
    expect(managerBarSource).toContain("return t('managerBar.modal.singlePrompt');");
    expect(managerBarSource).toContain(
      'function usesTurnQueueForSession(sessionId: string): boolean {',
    );
  });
});
