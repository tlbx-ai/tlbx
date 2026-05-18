import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const source = readFileSync(path.join(__dirname, 'smartInput.ts'), 'utf8');
const metricsSource = readFileSync(path.join(__dirname, 'smartInputMetrics.ts'), 'utf8');
const submissionSource = readFileSync(
  path.join(__dirname, 'appServerControlAttachmentSubmission.ts'),
  'utf8',
);
const layoutSource = readFileSync(path.join(__dirname, 'layout.ts'), 'utf8');
const keyBindingsSource = readFileSync(path.join(__dirname, 'smartInputKeyBindings.ts'), 'utf8');
const textareaShortcutsSource = readFileSync(
  path.join(__dirname, 'smartInputTextareaShortcuts.ts'),
  'utf8',
);
const viewSource = readFileSync(path.join(__dirname, 'smartInputView.ts'), 'utf8');
const footerSupportSource = readFileSync(path.join(__dirname, 'footerSupport.ts'), 'utf8');
const appServerControlResumeButtonSource = readFileSync(
  path.join(__dirname, 'appServerControlResumeButton.ts'),
  'utf8',
);
const css = readFileSync(path.join(__dirname, '../../../static/css/app.css'), 'utf8');
const html = readFileSync(path.join(__dirname, '../../../static/index.html'), 'utf8');

function compactWhitespace(value: string): string {
  return value.replace(/\s+/g, ' ').trim();
}

function getCssRule(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) {
    return '';
  }

  const end = css.indexOf('\n}', start);
  return end >= 0 ? css.slice(start, end + 2) : '';
}

describe('smart input tab wiring', () => {
  it('resyncs smart input visibility when non-AppServerControl tabs activate', () => {
    expect(source).toContain("onTabActivated('agent', (sessionId) => {");
    expect(source).toContain("onTabActivated('terminal', (sessionId) => {");
    expect(source).toContain("onTabActivated('files', (sessionId) => {");
  });

  it('suppresses embedded-preview autofocus so nested MidTerm cannot steal outer Command Bay focus', () => {
    expect(source).toContain("import { isEmbeddedWebPreviewContext } from '../web/webContext';");
    expect(source).toContain('function shouldAllowProgrammaticSmartInputFocus(): boolean {');
    expect(source).toContain('return !isEmbeddedWebPreviewContext();');
    expect(source).toContain(
      'isAppServerControlActiveSession(sessionId) && shouldAllowProgrammaticSmartInputFocus()',
    );
    expect(source).toContain('syncSmartInputVisibility(shouldAllowProgrammaticSmartInputFocus());');
  });

  it('does not rely on agent deactivation timing to hide AppServerControl-only controls', () => {
    expect(source).not.toContain("onTabDeactivated('agent'");
  });

  it('keys footer glass material off UI transparency, not terminal pane transparency', () => {
    expect(source).toContain('const transparency = settings?.uiTransparency ?? 0;');
    expect(source).not.toContain(
      'const transparency = settings?.terminalTransparency ?? settings?.uiTransparency ?? 0;',
    );
  });

  it('keeps the Command Bay material unfrosted', () => {
    const glassRule = getCssRule(".adaptive-footer-dock[data-material='glass']");

    expect(glassRule).toContain("adaptive-footer-dock[data-material='glass']");
    expect(glassRule).not.toContain('backdrop-filter');
    expect(glassRule).not.toContain('-webkit-backdrop-filter');
  });

  it('keeps AppServerControl quick settings hidden when the hidden attribute is set', () => {
    expect(css).toContain('.smart-input-appServerControl-settings[hidden] {');
    expect(css).toContain('.smart-input-appServerControl-actions[hidden] {');
    expect(css).toContain('display: none !important;');
    expect(css).toContain('.adaptive-footer-dock .smart-input-appServerControl-dropdown-menu {');
    expect(css).toMatch(
      /\.smart-input-appServerControl-dropdown\.smart-input-appServerControl-dropdown-open-up\s+\.smart-input-appServerControl-dropdown-menu\s*\{/,
    );
    expect(css).toContain('position: absolute;');
    expect(css).toContain('z-index: 5;');
    expect(viewSource).toContain('createAppServerControlQuickSettingsDropdown(');
    expect(viewSource).toContain('appServerControlModelSelect,');
    expect(viewSource).toContain(
      'createAppServerControlQuickSettingsDropdown(appServerControlEffortSelect)',
    );
    expect(viewSource).toContain(
      "appServerControlQuickSettingsActions.className = 'smart-input-appServerControl-actions';",
    );
    expect(viewSource).toContain(
      'manager-bar-action-popover smart-input-appServerControl-dropdown-menu hidden',
    );
    expect(viewSource).toContain(
      "wrapper.classList.toggle('smart-input-appServerControl-dropdown-open-up', openUp);",
    );
    expect(viewSource).toContain("document.addEventListener('scroll', updateMenuPlacement, true);");
    expect(viewSource).toContain('trigger.disabled = disabled;');
    expect(viewSource).toContain(
      "select.addEventListener('midterm:disabled', syncDisabledState as EventListener);",
    );
  });

  it('avoids no-op AppServerControl quick-setting dropdown churn during footer resync', () => {
    expect(viewSource).toContain('if (select.dataset.midtermOptionsSignature === nextSignature) {');
    expect(viewSource).toContain('select.dataset.midtermOptionsSignature = nextSignature;');
    expect(viewSource).toContain("select.dispatchEvent(new Event('midterm:options'));");
    expect(viewSource).toContain("select.dispatchEvent(new Event('midterm:disabled'));");
    expect(viewSource).toContain('syncSelection();');
    expect(footerSupportSource).toContain('if (select.value === nextValue) {');
    expect(footerSupportSource).toContain("select.dispatchEvent(new Event('midterm:sync'));");
  });

  it('locks AppServerControl quick settings while turns are running or queued', () => {
    expect(source).toContain('hasInterruptibleAppServerControlTurnWork');
    expect(footerSupportSource).toContain(
      'const quickSettingsLocked = hasInterruptibleAppServerControlTurnWork(sessionId);',
    );
    expect(footerSupportSource).toContain('appServerControlModelSelect,\n    quickSettingsLocked,');
    expect(footerSupportSource).toContain(
      'appServerControlEffortSelect,\n    quickSettingsLocked,',
    );
    expect(footerSupportSource).toContain(
      'setAppServerControlQuickSettingsDropdownDisabled(appServerControlPlanSelect, quickSettingsLocked);',
    );
    expect(footerSupportSource).toContain(
      'appServerControlPermissionSelect,\n    quickSettingsLocked,',
    );
  });

  it('mounts smart input, manager automation, and status rails inside one adaptive footer dock', () => {
    expect(html).toContain('id="adaptive-footer-dock"');
    expect(html).toContain('id="adaptive-footer-reserve"');
    expect(html).toContain('id="adaptive-footer-primary"');
    expect(html).toContain('id="adaptive-footer-context"');
    expect(html).toContain('id="adaptive-footer-status"');
    expect(html).toContain('id="manager-bar-overflow"');
    expect(css).toContain('.manager-bar-overflow[hidden] {');
    expect(css).toContain('display: none !important;');
    expect(source).toContain(
      'function getAdaptiveFooterLayoutState(): AdaptiveFooterLayoutState {',
    );
    expect(source).toContain('showAutomation');
    expect(source).toContain('showStatus');
    expect(source).toContain('syncFooterRailOrder(layoutState);');
    expect(layoutSource).toContain('if (state.isMobile && state.appServerControlActive) {');
    expect(layoutSource).toContain("return ['status', 'primary', 'context', 'automation'];");
    expect(layoutSource).toContain("return ['primary', 'status', 'context', 'automation'];");
    expect(layoutSource).toContain("return ['primary', 'context', 'automation', 'status'];");
    expect(layoutSource).not.toContain("return ['primary', 'automation', 'context', 'status'];");
  });

  it('collapses the adaptive footer immediately while settings are open', () => {
    expect(source).toContain('$settingsOpen');
    expect(source).toContain('const settingsOpen = $settingsOpen.get();');
    expect(source).toContain('$settingsOpen.subscribe(() => {');
    expect(source).toContain('const showFooter = settingsOpen');
    expect(source).toContain('hideAdaptiveFooter();');
    expect(source).toContain('updateFooterReservedHeight();');
  });

  it('reserves only collapsed footer height and uses send gestures for auto-send toggling', () => {
    expect(footerSupportSource).toContain('calculateAdaptiveFooterReservedHeight');
    expect(layoutSource).toContain('ADAPTIVE_FOOTER_RESERVED_HEIGHT_CHANGED_EVENT');
    expect(source).toContain('ResizeObserver');
    expect(footerSupportSource).toContain('setAdaptiveFooterReservedHeight(');
    expect(footerSupportSource).toContain('window.dispatchEvent(');
    expect(source).toContain('function scrollFooterDockForTextareaFocus(): void {');
    expect(source).toContain(
      'shouldKeepFocusedComposerVisibleOnMobileAppServerControl() ? footerDock.scrollHeight : 0',
    );
    expect(viewSource).toContain("sendBtn.addEventListener('dblclick', args.onSendDoubleClick);");
    expect(source).toContain('AUTO_SEND_LONG_PRESS_MS');
    expect(source).toContain(
      "footerStatusHost.toggleAttribute('hidden', !renderedTerminalStatus);",
    );
    expect(source).toContain('createTerminalTouchToggleButton({');
    expect(source).toContain('function setTouchKeysExpanded(expanded: boolean): void {');
    expect(source).toContain('closeTouchControllerPopup();');
    expect(source).toContain('footerContextHost.appendChild(touchControllerEl);');
    expect(viewSource).toContain(
      "keysToggle.className = 'adaptive-footer-context-toggle adaptive-footer-status-toggle';",
    );
    expect(css).toContain('.adaptive-footer-dock {');
    expect(css).toContain('.adaptive-footer-reserve {');
    expect(css).toContain('height: var(--adaptive-footer-reserved-height);');
    expect(css).toContain('.smart-input-tools-surface {');
    expect(css).toContain(
      ".adaptive-footer-dock[data-device='mobile'] .smart-input-tools-surface {",
    );
    expect(css).toContain('bottom: calc(100% + var(--command-bay-gap));');
    expect(css).toContain('.adaptive-footer-status.adaptive-footer-status-sheet-open {');
    expect(css).toContain('.adaptive-footer-context-toggle {');
    expect(css).toContain(".adaptive-footer-status[data-appServerControl-compact='true'] {");
    expect(css).toContain('position: relative;');
    expect(css).toContain('z-index: 3;');
    expect(css).toContain('--smart-input-control-height: 42px;');
    expect(css).toContain('--command-bay-control-height: 36px;');
    expect(css).toContain('--command-bay-surface: color-mix(');
    expect(css).toContain(
      '--smart-input-mobile-text-size: max(15px, var(--terminal-font-size, 16px));',
    );
    expect(css).toContain('font-size: var(--smart-input-mobile-text-size);');
    expect(compactWhitespace(css)).toContain(
      compactWhitespace(
        'padding: var(--smart-input-textarea-padding-top) 33px var(--smart-input-textarea-padding-bottom) 7px;',
      ),
    );
    expect(css).toContain('align-items: center;');
    expect(css).toContain('.smart-input-tools-toggle::before,');
    expect(viewSource).toContain(
      "toolsPanel.className = 'manager-bar-action-popover smart-input-tools-surface';",
    );
    expect(css).toContain('font-size: var(--terminal-font-size, 16px);');
    expect(metricsSource).toContain('const MAX_TEXTAREA_OVERLAY_LINES = 7;');
    expect(metricsSource).toContain(
      'const MAX_VISIBLE_TEXTAREA_LINES = COLLAPSED_TEXTAREA_LINES + MAX_TEXTAREA_OVERLAY_LINES;',
    );
  });

  it('keeps staged attachment drafts available in both AppServerControl and terminal composers', () => {
    expect(source).toContain('appServerControlAttachmentDrafts');
    expect(source).toContain('handleSmartInputSelectedFiles');
    expect(source).toContain('const uploadedPath = await uploadFile(sessionId, file);');
    expect(source).toContain('shouldConvertPastedTextToSmartInputReference');
    expect(source).toContain('addAppServerControlComposerTextReference');
    expect(source).toContain('prepareSmartInputTerminalTurn');
    expect(source).toContain(
      'void openAppServerControlDraftAttachment(currentSessionId, attachment);',
    );
    expect(source).toContain('enqueueCommandBayTurn');
    expect(source).not.toContain('await handleFileDrop(files);');
    expect(source).not.toContain(
      'isAppServerControlActiveSession(sessionId) &&\n        clipboardDataMayContainAppServerControlComposerImage',
    );
    expect(submissionSource).toContain('prepareSmartInputOutboundPrompt');
    expect(submissionSource).toContain(
      'queuedTurn: args.submitQueuedTurn(args.sessionId, request)',
    );
    expect(css).toContain('.smart-input-attachments {');
    expect(css).toContain('.smart-input-attachment-chip {');
    expect(css).toContain('.smart-input-attachment-open {');
    expect(viewSource).not.toContain("textarea.addEventListener('pointerdown', () => {");
    expect(viewSource).not.toContain('document.activeElement !== textarea');
  });

  it('keeps command-bay panels in reserved flow while only textarea growth may overlay the pane', () => {
    expect(source).toContain(
      "footerStatusHost.classList.add('adaptive-footer-status-sheet-open');",
    );
    expect(source).toContain('return args.touchControlsAvailable;');
    expect(source).toContain('shouldUseCompactAppServerControlStatusRail(layoutState)');
    expect(source).toContain('dockedBar.appendChild(dom.inputRow);');
    expect(source).toContain('let toolsPanelOpen = false;');
    expect(source).toContain('let suppressNextToolsToggleClick = false;');
    expect(source).toContain('setToolsPanelOpen(!toolsPanelOpen);');
    expect(source).toContain(
      "toolsPanel.parentElement?.classList.toggle('smart-input-row-tools-open', shouldOpen);",
    );
    expect(source).toContain(
      'const preserveTextareaFocus = document.activeElement === activeTextarea;',
    );
    expect(source).toContain('layoutState.showInput &&');
    expect(source).toContain(
      'preserveTextareaFocus || (focusTextarea && shouldAllowProgrammaticSmartInputFocus())',
    );
    expect(source).toContain('const needsReorder = desiredOrder.some(');
    expect(source).toContain('event.stopPropagation();');
    expect(source).not.toContain("nextToolsToggleBtn.addEventListener('pointerdown'");
    expect(viewSource).toContain('inputRow.appendChild(toolsPanel);');
    expect(css).toContain('bottom: calc(100% + var(--command-bay-gap));');
    expect(css).toContain('.smart-input-row.smart-input-row-tools-open {');
    expect(css).toContain(
      '.smart-input-row.smart-input-row-tools-open .smart-input-tools-surface:not([hidden]) {',
    );
    expect(css).toContain('position: static;');
    expect(css).toContain('flex: 1 0 100%;');
    expect(css).toContain('.smart-input-appServerControl-settings-sheet {');
    expect(css).toContain('overflow: visible;');
    expect(css).toContain('.adaptive-footer-primary {');
    expect(css).toContain('.smart-input-editor {');
    expect(css).toContain('.smart-input-textarea {');
    expect(css).toContain('.adaptive-footer-dock .smart-input-textarea {');
    expect(css).toContain(":root:not([data-command-bay-ligatures='false']) .smart-input-textarea");
    expect(css).toContain(
      'font-family: var(--terminal-font-family, var(--agent-history-mono-font-family, var(--font-mono)));',
    );
    expect(css).toContain('font-size: var(--terminal-font-size, 16px);');
    expect(css).toContain('font-weight: var(--terminal-font-weight, normal);');
    expect(css).toContain('letter-spacing: var(--terminal-letter-spacing, 0px);');
    expect(css).toContain(
      '--smart-input-textarea-rendered-height: var(--smart-input-textarea-min-height);',
    );
    expect(compactWhitespace(css)).toContain(
      compactWhitespace(
        '--smart-input-textarea-collapsed-height: var( --command-bay-control-height, var(--smart-input-control-height) );',
      ),
    );
    expect(css).toContain(
      '--smart-input-textarea-collapsed-height: var(--command-bay-control-height);',
    );
    expect(css).toContain(
      '--smart-input-textarea-padding-y: var(--smart-input-textarea-multiline-padding-y);',
    );
    expect(css).toContain(
      '--smart-input-textarea-padding-top: var(--smart-input-textarea-padding-y);',
    );
    expect(css).toContain(
      '--smart-input-textarea-padding-bottom: var(--smart-input-textarea-padding-y);',
    );
    expect(compactWhitespace(css)).toContain(
      compactWhitespace(
        '.adaptive-footer-dock .smart-input-textarea { flex: 1; resize: none; box-sizing: border-box;',
      ),
    );
    expect(compactWhitespace(css)).toContain(
      compactWhitespace(
        'padding: var(--smart-input-textarea-padding-top) 9px var(--smart-input-textarea-padding-bottom) 9px;',
      ),
    );
    expect(compactWhitespace(css)).toContain(
      compactWhitespace(
        'padding: var(--smart-input-textarea-padding-top) calc(9px + var(--smart-input-expand-hit-size) - 10px) var(--smart-input-textarea-padding-bottom) 9px;',
      ),
    );
    expect(compactWhitespace(css)).toContain(
      compactWhitespace(
        'var(--smart-input-textarea-collapsed-height) - var(--smart-input-textarea-line-height) - 8px',
      ),
    );
    expect(css).toContain('--smart-input-textarea-line-height: calc(');
    expect(css).toContain('var(--terminal-line-height, 1)');
    expect(css).toContain('--smart-input-textarea-line-gap-extra: 2px;');
    expect(css).toContain('var(--smart-input-textarea-line-gap-extra)');
    expect(css).toContain('line-height: var(--smart-input-textarea-line-height);');
    expect(css).toContain('font-kerning: none;');
    expect(css).toContain('@supports (leading-trim: both) and (text-edge: cap alphabetic) {');
    expect(css).toContain('overflow: visible;');
    expect(css).toContain('.manager-btn-overflow-hidden {');
    expect(metricsSource).toContain('const SINGLE_LINE_VERTICAL_OPTICAL_OFFSET_PX = 3;');
    expect(metricsSource).toContain('--smart-input-textarea-padding-top');
    expect(metricsSource).toContain('--smart-input-textarea-padding-bottom');
  });

  it('matches the Command Bay dock background to the sidebar background', () => {
    const solidRule = getCssRule(".adaptive-footer-dock[data-material='solid']");
    const glassRule = getCssRule(".adaptive-footer-dock[data-material='glass']");

    expect(solidRule).toContain('background: var(--bg-sidebar);');
    expect(glassRule).toContain('background: var(--bg-sidebar);');
    expect(solidRule).not.toContain('linear-gradient');
    expect(glassRule).not.toContain('linear-gradient');
    expect(compactWhitespace(css)).toContain(
      compactWhitespace(
        '@media (max-width: 768px) { .adaptive-footer-dock { --command-bay-control-height: var(--command-bay-control-height-mobile); padding: 4px;',
      ),
    );
    expect(compactWhitespace(css)).toContain(
      compactWhitespace('background: var(--bg-sidebar); -webkit-backdrop-filter: none;'),
    );
  });

  it('keeps attachment and token rerenders from snapping the composer viewport back to the top', () => {
    expect(source).toContain('const preserveScrollTop = textarea.scrollTop;');
    expect(source).toContain('resizeSmartInputTextarea(textarea, { preserveScrollTop });');
    expect(source).toContain('let draftRenderedIntoTextarea = false;');
    expect(source).toContain('if (!draftRenderedIntoTextarea) {');
    expect(css).toContain('--smart-input-textarea-max-visible-lines: 8;');
    expect(css).toContain(
      'var(--smart-input-textarea-line-height) * var(--smart-input-textarea-max-visible-lines)',
    );
  });

  it('supports an inset composer expand toggle without duplicating the live textarea', () => {
    expect(viewSource).toContain("textareaShell.className = 'smart-input-textarea-shell';");
    expect(viewSource).toContain("composerExpandBtn.className = 'smart-input-expand-toggle';");
    expect(viewSource).toContain(
      'syncSmartInputComposerExpandToggleState(composerExpandBtn, false);',
    );
    expect(viewSource).toContain('textareaShell.appendChild(textarea);');
    expect(viewSource).toContain('textareaShell.appendChild(composerExpandBtn);');
    expect(source).toContain('const sessionComposerExpanded = new Map<string, boolean>();');
    expect(source).toContain(
      "footerDock?.setAttribute('data-composer-expanded', composerExpanded ? 'true' : 'false');",
    );
    expect(source).toContain(
      'setActiveSessionComposerExpanded(!isComposerExpanded($activeSessionId.get()));',
    );
    expect(source).toContain(
      'releaseComposerExpandedBackButtonLayer = registerBackButtonLayer(() => {',
    );
    expect(footerSupportSource).toContain('composerExpanded: boolean;');
    expect(footerSupportSource).toContain('args.composerExpanded');
    expect(css).toContain('.smart-input-textarea-shell {');
    expect(css).toContain('.smart-input-expand-toggle {');
    expect(css).toContain(".adaptive-footer-dock[data-composer-expanded='true'] {");
    expect(css).toContain(".adaptive-footer-dock[data-composer-expanded='true'] {\n  top: 0;");
    expect(css).toContain('justify-content: flex-end;');
    expect(css).toContain(
      ".adaptive-footer-dock[data-composer-expanded='true'] .adaptive-footer-primary {\n  flex: 1 1 auto;",
    );
    expect(css).toContain(
      "body.keyboard-visible .adaptive-footer-dock[data-composer-expanded='true'][data-device='mobile']",
    );
  });

  it('keeps per-session composer drafts and expanded state when switching the active session', () => {
    expect(source).toContain('persistDraftForSession(lastSessionId);');
    expect(source).toContain('syncDraftForActiveSession();');
    expect(source).toContain('const sessionComposerExpanded = new Map<string, boolean>();');
    expect(source).toContain(
      'function setComposerExpandedForSession(sessionId: string, expanded: boolean): void {',
    );
    expect(source).toContain(
      'return sessionId ? sessionComposerExpanded.get(sessionId) === true : false;',
    );
  });

  it('auto-collapses the expanded composer only after a prompt send succeeds', () => {
    expect(source).toContain(
      'function collapseComposerAfterSuccessfulSend(sessionId: string): void {',
    );
    expect(source).toContain('if ($activeSessionId.get() === sessionId) {');
    expect(source).toContain('collapseComposerAfterSuccessfulSend(sessionId);');
  });

  it('renders the plus-menu tools as popover actions with icon and text labels', () => {
    expect(viewSource).toContain("toolsToggleBtn.setAttribute('aria-haspopup', 'menu');");
    expect(viewSource).toContain(
      "toolsToggleBtn.addEventListener('pointerdown', args.onToolsTogglePointerDown);",
    );
    expect(viewSource).toContain("button.classList.add('smart-input-tool-button');");
    expect(viewSource).toContain('smart-input-tool-label');
    expect(viewSource).not.toContain('describeTerminalStatus(');
    expect(css).toContain('.smart-input-tools-surface .smart-input-tool-button {');
    expect(css).toContain('.smart-input-tools-surface .smart-input-tool-label {');
  });

  it('uses an explicit picker helper for attach and photo tools instead of relying on raw hidden-input clicks', () => {
    expect(source).toContain('function openFileInputPicker(input: HTMLInputElement): void {');
    expect(viewSource).toContain("if (typeof input.showPicker === 'function')");
    expect(source).toContain('openFileInputPicker(sharedAttachInput);');
    expect(source).toContain('openFileInputPicker(sharedPhotoInput);');
  });

  it('routes Escape through the AppServerControl interrupt handler instead of treating it like a text key', () => {
    expect(source).toContain('bindSmartInputGlobalKeyBindings({');
    expect(source).toContain('hasInterruptibleAppServerControlTurnWork(sessionId)');
    expect(keyBindingsSource).toContain('document.addEventListener(');
    expect(keyBindingsSource).toContain("'keydown'");
    expect(keyBindingsSource).toContain('event.stopImmediatePropagation();');
    expect(keyBindingsSource).toContain('true,');
    expect(source).toContain("event.key === 'Escape'");
    expect(source).toContain('void handleAppServerControlEscape(sessionId);');
  });

  it('submits from the command bay only on bare Enter', () => {
    expect(source).toContain('import {');
    expect(source).toContain("} from './enterBehavior';");
    expect(source).toContain("event.key === 'ArrowUp'");
    expect(source).toContain("event.key === 'ArrowDown'");
    expect(source).toContain("navigatePromptHistory(sessionId, 'older', textarea)");
    expect(source).toContain("navigatePromptHistory(sessionId, 'newer', textarea)");
    expect(source).toContain('shouldInsertLineBreakOnEnter');
    expect(source).toContain('insertSmartInputLineBreak');
    expect(source).toContain('if (shouldInsertLineBreakOnEnter(event)) {');
    expect(source).toContain('insertSmartInputLineBreak(textarea);');
    expect(source).toContain('if (shouldSubmitSmartInputOnEnter(event)) {');
    expect(source).not.toContain("if (event.key === 'Enter' && !event.shiftKey) {");
  });

  it('captures Shift+Tab by active surface so AppServerControl toggles plan mode and Terminal receives backtab', () => {
    expect(source).toContain("import { getActiveTab, onTabActivated } from '../sessionTabs';");
    expect(source).toContain(
      "import { resolveSmartInputShiftTabAction } from './smartInputTextareaShortcuts';",
    );
    expect(source).toContain(
      'function handleSmartInputShiftTabShortcut(event: KeyboardEvent): boolean {',
    );
    expect(source).toContain('const shiftTabAction = resolveSmartInputShiftTabAction(');
    expect(source).toContain('if (handleSmartInputShiftTabShortcut(event)) {');
    expect(source).toContain('toggleAppServerControlPlanMode(sessionId);');
    expect(source).toContain("sendInput(sessionId, '\\x1b[Z');");
    expect(textareaShortcutsSource).toContain("return 'toggle-appServerControl-plan-mode';");
    expect(textareaShortcutsSource).toContain("return 'forward-to-terminal';");
  });

  it('advertises prompt history restoration from the empty Automation Bar composer', () => {
    expect(viewSource).toContain("textarea.placeholder = t('smartInput.placeholder');");
    expect(source).toContain(
      "layoutState.isMobile ? 'smartInput.placeholderMobile' : 'smartInput.placeholder'",
    );
    expect(source).toContain('pushCurrentPromptToHistory(sessionId);');
    expect(source).toContain('sessionPromptHistoryNavigation');
  });

  it('routes command-bay sends through the backend-owned queue instead of direct terminal submission', () => {
    expect(source).toContain('prepareSmartInputTerminalTurn');
    expect(source).toContain('await enqueueCommandBayTurn(sessionId, request);');
    expect(source).toContain('submitQueuedTurn: enqueueCommandBayTurn,');
  });

  it('adds a space-scoped provider resume action to the AppServerControl Command Bay status rail', () => {
    expect(source).toContain('setAppServerControlResumeConversationHandler');
    expect(source).toContain('createAppServerControlResumeButton');
    expect(source).toContain('syncAppServerControlQuickSettingsActions(sessionId);');
    expect(source).toContain('shouldIgnoreFooterTransientUiDocumentClickSupport(target)');
    expect(footerSupportSource).toContain("target.closest('.provider-resume-picker-overlay')");
    expect(appServerControlResumeButtonSource).toContain(
      "button.className = 'smart-input-appServerControl-action smart-input-appServerControl-resume';",
    );
    expect(appServerControlResumeButtonSource).toContain('session?.spaceId');
    expect(css).toContain('.smart-input-appServerControl-actions {');
    expect(css).toContain('.smart-input-appServerControl-action {');
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.adaptive-footer-dock \.smart-input-appServerControl-field:nth-of-type\(4\),[\s\S]*?\.adaptive-footer-dock \.smart-input-appServerControl-actions \{[\s\S]*?display: none;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.adaptive-footer-dock \.smart-input-appServerControl-settings \{[\s\S]*?background: transparent;[\s\S]*?box-shadow: none;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.adaptive-footer-dock \.smart-input-appServerControl-settings \{[\s\S]*?width: fit-content;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.adaptive-footer-dock[\s\S]*?\.smart-input-appServerControl-field[\s\S]*?\+ \.smart-input-appServerControl-field::before \{[\s\S]*?display: none;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.adaptive-footer-dock \.smart-input-appServerControl-dropdown-trigger \{[\s\S]*?width: clamp\(84px, 28vw, 118px\);[\s\S]*?min-height: 32px;[\s\S]*?border-radius: 8px;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.adaptive-footer-status\.adaptive-footer-status-sheet-open[\s\S]*?\.adaptive-footer-status-summary-appServerControl,[\s\S]*?\.adaptive-footer-status\.adaptive-footer-status-sheet-open[\s\S]*?\.adaptive-footer-status-automation-proxy \{[\s\S]*?display: none;/s,
    );
  });

  it('exposes quiet AppServerControl controls without raw slash-command labels', () => {
    expect(source).not.toContain("'/model'");
    expect(source).not.toContain("'/plan'");
    expect(source).not.toContain("'/goal'");
    expect(source).not.toContain('openAppServerControlModelPicker();');
    expect(source).toContain("createAppServerControlActionButton(\n      'Plan'");
    expect(source).toContain('toggleAppServerControlPlanMode(sessionId);');
    expect(source).toContain("createAppServerControlActionButton(\n        'Goal'");
    expect(source).toContain('void prepareAppServerControlGoal(sessionId);');
    expect(source).toContain('if (appServerControlGoalComposeSessionId === sessionId) {');
    expect(source).toContain("appServerControlGoalComposeSessionId = null;");
    expect(source).toContain('await setAppServerControlGoal(sessionId, { objective });');
    expect(viewSource).toContain("['xhigh', 'XHigh']");
  });

  it('hides inline tools on mobile AppServerControl sessions', () => {
    expect(source).toContain(
      'if (layoutState.appServerControlActive && layoutState.isMobile && inlineToolHost) {',
    );
    expect(source).toContain('inlineToolHost.hidden = true;');
  });

  it('merges context and automation controls into the mobile status row', () => {
    expect(source).toContain('function renderMobileTerminalStatusRow(');
    expect(source).toContain("leftCluster.className = 'adaptive-footer-status-left';");
    expect(source).toContain("rightCluster.className = 'adaptive-footer-status-right';");
    expect(source).toContain('event.stopPropagation();');
    expect(source).toContain(
      'setAppServerControlQuickSettingsSheetOpen(!appServerControlQuickSettingsSheetOpen);',
    );
    expect(source).toContain('appServerControlQuickSettingsRow.classList.contains(');
    expect(source).toContain("'smart-input-appServerControl-settings-sheet',");
    expect(source).toContain('footerStatusHost.appendChild(appServerControlQuickSettingsRow);');
    expect(source).toContain('expanded: layoutState.touchControlsExpanded,');
    expect(source).toContain('setTouchKeysExpanded(!layoutState.touchControlsExpanded);');
    expect(source).toContain('createAutomationOverflowProxy()');
    expect(source).toContain('createAutomationAddProxy()');
    expect(source).toContain('setAutomationOverflowProxyAnchor(overflowProxy)');
    expect(source).toContain('triggerAutomationOverflow(btn)');
    expect(source).toContain('setAutomationOverflowProxyAnchor(null)');
    expect(source).toContain('triggerAddAutomation()');
    expect(viewSource).toContain('adaptive-footer-status-toggle-icon');
    expect(viewSource).toContain('adaptive-footer-status-toggle-label');
    expect(source).toContain(
      "managerBar?.classList.toggle('hidden', !layoutState.showAutomation || layoutState.isMobile)",
    );
    expect(css).toContain('.adaptive-footer-status-left {');
    expect(css).toContain('.adaptive-footer-status-right {');
    expect(css).toContain(".adaptive-footer-dock[data-device='mobile'] .manager-bar {");
    expect(css).toContain(
      "body.keyboard-visible\n  .adaptive-footer-dock[data-device='mobile'][data-surface='terminal']\n  .adaptive-footer-status {",
    );
    expect(css).toContain(
      "body.keyboard-visible\n  .adaptive-footer-dock[data-device='mobile'][data-surface='terminal']\n  .adaptive-footer-status-toggle-label {",
    );
  });

  it('keeps mobile AppServerControl status awareness and bottom-jump chrome out of the keyboard overlap zone', () => {
    expect(css).toMatch(
      /body\.keyboard-visible\s+\.adaptive-footer-dock\[data-device='mobile'\]\[data-surface='appServerControl'\]\s+\.adaptive-footer-status\s*\{/,
    );
    expect(css).toMatch(
      /body\.keyboard-visible\s+\.adaptive-footer-dock\[data-device='mobile'\]\[data-surface='appServerControl'\]\s+\.adaptive-footer-primary\s*\{/,
    );
    expect(css).toContain('bottom: 12px;');
    expect(css).toContain('bottom: 10px;');
    expect(css).not.toContain('bottom: calc(20px + var(--adaptive-footer-reserved-height, 0px));');
    expect(css).not.toContain('bottom: calc(12px + var(--adaptive-footer-reserved-height, 0px));');
    expect(source).toContain(
      'function shouldKeepFocusedComposerVisibleOnMobileAppServerControl(): boolean {',
    );
    expect(source).toContain("document.body.classList.contains('keyboard-visible')");
  });
});
