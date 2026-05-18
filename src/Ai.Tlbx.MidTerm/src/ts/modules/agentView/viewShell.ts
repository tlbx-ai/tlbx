import { t } from '../i18n';
import {
  normalizeAppServerControlProvider,
  resolveAppServerControlLayoutMode,
} from './activationHelpers';
import type { AppServerControlDebugScenarioName } from './debugScenario';

export const APP_SERVER_CONTROL_DEBUG_SCENARIO_NAMES: readonly AppServerControlDebugScenarioName[] =
  ['mixed', 'tables', 'long', 'massive', 'workflow'];

function appServerControlText(key: string, fallback: string): string {
  const translated = t(key);
  if (!translated || translated === key) {
    return fallback;
  }

  return translated;
}

export function normalizeAppServerControlDebugScenarioName(
  scenario: string,
): AppServerControlDebugScenarioName {
  return APP_SERVER_CONTROL_DEBUG_SCENARIO_NAMES.includes(
    scenario as AppServerControlDebugScenarioName,
  )
    ? (scenario as AppServerControlDebugScenarioName)
    : 'mixed';
}

export function ensureAgentViewSkeleton(
  sessionId: string,
  panel: HTMLDivElement,
  onEscape: (sessionId: string) => void,
): void {
  syncAgentViewPresentation(panel);
  if (panel.dataset.agentViewReady !== 'true') {
    panel.dataset.agentViewReady = 'true';
    panel.classList.add('agent-view-panel');
    panel.innerHTML = `
      <section class="agent-view">
        <div class="agent-chat-shell">
          <div class="agent-runtime-stats" data-agent-field="runtime-stats" hidden></div>
          <div class="agent-history-shell">
            <div class="agent-history" data-agent-field="history" tabindex="0"></div>
            <div
              class="agent-history-progress-nav"
              data-agent-field="history-progress-nav"
              role="scrollbar"
              data-ready="false"
              tabindex="-1"
              aria-label="${appServerControlText('appServerControl.history.indexScroll', 'History navigation scrollbar')}"
              aria-disabled="true"
              aria-valuemin="1"
              aria-valuemax="1"
              aria-valuenow="1"
            >
              <div class="agent-history-progress-track" data-agent-field="history-progress-track"></div>
              <div class="agent-history-progress-thumb" data-agent-field="history-progress-thumb"></div>
            </div>
            <button type="button" class="agent-scroll-to-bottom" data-agent-field="scroll-to-bottom" hidden>${appServerControlText('appServerControl.scrollToBottom', 'Back to bottom')}</button>
          </div>
          <section class="agent-composer-shell" data-agent-field="composer-shell" hidden>
            <div class="agent-composer-interruption" data-agent-field="composer-interruption" hidden></div>
            <div class="agent-composer-host" data-agent-field="composer-host"></div>
          </section>
        </div>
      </section>
    `;
  }

  if (panel.dataset.agentViewEscapeBound === 'true') {
    return;
  }

  panel.dataset.agentViewEscapeBound = 'true';
  panel.addEventListener('keydown', (event) => {
    if (
      event.key !== 'Escape' ||
      event.shiftKey ||
      event.ctrlKey ||
      event.altKey ||
      event.metaKey
    ) {
      return;
    }

    event.preventDefault();
    onEscape(sessionId);
  });
}

function syncAgentViewPresentation(
  panel: HTMLDivElement,
  provider: string | null | undefined = null,
): void {
  panel.dataset.appServerControlProvider = normalizeAppServerControlProvider(provider);
  panel.dataset.appServerControlLayout = resolveAppServerControlLayoutMode(provider);
}
