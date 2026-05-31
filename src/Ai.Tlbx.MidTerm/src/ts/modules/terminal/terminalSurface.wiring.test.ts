import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const appCss = readFileSync(path.join(__dirname, '../../../static/css/app.css'), 'utf8');
const constants = readFileSync(path.join(__dirname, '../../constants.ts'), 'utf8');
const managerSource = readFileSync(path.join(__dirname, 'manager.ts'), 'utf8');
const scalingSource = readFileSync(path.join(__dirname, 'scaling.ts'), 'utf8');
const interactionBindingsSource = readFileSync(
  path.join(__dirname, 'interactionBindings.ts'),
  'utf8',
);
const enterOverrideSuppressSource = readFileSync(
  path.join(__dirname, 'enterOverrideSuppress.ts'),
  'utf8',
);
const terminalGapFillersSource = readFileSync(
  path.join(__dirname, 'terminalGapFillers.ts'),
  'utf8',
);
const mobileVerticalStabilitySource = readFileSync(
  path.join(__dirname, 'mobileVerticalStability.ts'),
  'utf8',
);
const terminalOptionsSource = readFileSync(path.join(__dirname, 'terminalOptions.ts'), 'utf8');

describe('terminal surface wiring', () => {
  it('removes terminal panel inset padding from sizing and chrome', () => {
    expect(constants).toContain('export const TERMINAL_PADDING = 0;');
    expect(appCss).toContain('.terminal-container {');
    expect(appCss).toContain('padding: 0;');
    expect(appCss).toContain('background-color: transparent;');
  });

  it('keeps the xterm host from becoming a transparency-affecting backing pane', () => {
    expect(appCss).toContain('.terminal-container .xterm {');
    expect(appCss).toContain('background-color: transparent;');
    expect(appCss).toContain('z-index: 2;');
  });

  it('colors scaled terminal gaps with one non-overlapping terminal background plane', () => {
    expect(appCss).toContain('.terminal-container.scaled {');
    expect(appCss).toContain('background: var(\n    --terminal-gap-background,');
    expect(appCss).toContain('.terminal-gap-fill {');
    expect(appCss).toContain('background: var(--terminal-gap-background');
    expect(appCss).toContain('.terminal-container.scaled .terminal-gap-fill {');
    expect(appCss).toContain('background-color: transparent;');
    expect(appCss).toContain('background-image: none;');
    expect(appCss).toContain('.terminal-gap-fill-right {');
    expect(appCss).toContain('.terminal-gap-fill-bottom {');
    expect(appCss).toContain('.terminal-gap-fill-corner {');
    expect(appCss).not.toContain('calc(var(--terminal-gap-content-width, 0px) - 1px)');
    expect(appCss).not.toContain('calc(var(--terminal-gap-content-height, 0px) - 1px)');
    expect(scalingSource).toContain(
      "import { clearTerminalGapFillers, updateTerminalGapFillers } from './terminalGapFillers';",
    );
    expect(scalingSource).toContain('updateTerminalGapFillers(container, xterm, 1);');
    expect(terminalGapFillersSource).toContain("'xterm-screen'");
    expect(terminalGapFillersSource).toContain("'xterm-scrollable-element'");
    expect(terminalGapFillersSource).toContain("'--terminal-gap-background'");
    expect(terminalGapFillersSource).toContain('getBoundingClientRect');
    expect(terminalGapFillersSource).toContain('formatCssPixelValue');
    expect(terminalGapFillersSource).toContain("document.createElement('div')");
    expect(terminalGapFillersSource).toContain("'--terminal-gap-right-width'");
    expect(terminalGapFillersSource).toContain("'--terminal-gap-bottom-height'");
  });

  it('keeps terminal content above the Command Bay and refreshes when footer reserve changes', () => {
    expect(appCss).toContain('.adaptive-footer-reserve {');
    expect(appCss).toContain('height: var(--adaptive-footer-reserved-height);');
    expect(scalingSource).toContain('ADAPTIVE_FOOTER_RESERVED_HEIGHT_CHANGED_EVENT');
    expect(scalingSource).toContain('scheduleFooterReserveResize();');
    expect(mobileVerticalStabilitySource).toContain('mobileVerticalStabilityActive');
    expect(scalingSource).toContain('shouldPreserveMobileTerminalRows');
    expect(appCss).toContain(
      'body.mobile-terminal-vertical-stable .terminal-container.mobile-terminal-vertical-stable',
    );
    expect(appCss).toContain('overflow: hidden auto;');
  });

  it('does not run a periodic main-browser resize interval', () => {
    expect(scalingSource).not.toContain('setInterval(periodicResizeCheck');
  });

  it('wires custom box-drawing glyph rendering to persisted terminal settings', () => {
    expect(terminalOptionsSource).toContain("| 'customGlyphs'");
    expect(terminalOptionsSource).toContain('customGlyphs: currentSettings?.customGlyphs ?? true,');
  });

  it('does not reclaim terminal focus from AppServerControl, Files, or interactive Command Bay mouseup flows', () => {
    expect(managerSource).toContain('const FOCUS_RECLAIM_EXEMPT_SELECTOR = [');
    expect(managerSource).toContain("'.adaptive-footer-dock'");
    expect(managerSource).toContain('\'[data-tab-panel="agent"]\'');
    expect(managerSource).toContain('function hasActiveDocumentSelection(): boolean {');
    expect(managerSource).toContain("return getActiveTab(activeSessionId) !== 'terminal';");
    expect(managerSource).toContain('if (!target || shouldSkipGlobalFocusReclaim(target)) {');
  });

  it('suppresses embedded preview terminal auto-focus so nested MidTerm does not steal outer Command Bay focus', () => {
    expect(managerSource).toContain(
      "import { isEmbeddedWebPreviewContext } from '../web/webContext';",
    );
    expect(managerSource).toContain(
      'if (isEmbeddedWebPreviewContext() || isSearchVisible() || hasNonTerminalFocus()) return;',
    );
  });

  it('routes browser textarea line-break input through the terminal Enter override path', () => {
    expect(interactionBindingsSource).toContain("inputEvent.inputType === 'insertLineBreak'");
    expect(interactionBindingsSource).toContain("inputEvent.inputType === 'insertParagraph'");
    expect(interactionBindingsSource).toContain("'audit-input-enter'");
    expect(interactionBindingsSource).toContain('wasEnterOverrideHandledRecently');
    expect(interactionBindingsSource).toContain('const buildSyntheticEnterKeydown = ()');
    expect(interactionBindingsSource).toContain("key: { value: 'Enter' }");
    expect(interactionBindingsSource).toContain("type: { value: 'keydown' }");
    expect(managerSource).toContain('function tryHandleTerminalEnterOverride(');
    expect(managerSource).toContain('markTerminalEnterOverrideHandled(sessionId);');
    expect(enterOverrideSuppressSource).toContain(
      'const ENTER_OVERRIDE_INPUT_EVENT_SUPPRESS_MS = 250;',
    );
  });

  it('keeps Codex Enter overrides on the direct session-input path', () => {
    expect(managerSource).toContain('shouldRouteTerminalEnterOverrideThroughXtermInput(');
    expect(managerSource).toContain('sendInput(sessionId, bytes);');
    expect(managerSource).toContain('describeTerminalEnterOverrideDelivery(');
  });

  it('keeps terminal paste on the MidTerm paste path instead of native browser newline insertion', () => {
    expect(interactionBindingsSource).toContain('if (event.clipboardData) {');
    expect(interactionBindingsSource).toContain(
      'void pasteToTerminal(sessionId, sanitizePasteContent(text));',
    );
    expect(managerSource).toContain("from '../../api/client';");
    expect(managerSource).toContain('sendSessionPasteInput(sessionId, {');
    expect(managerSource).toContain('bracketedPaste: bpmEnabled,');
  });
});
