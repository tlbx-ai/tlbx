import { t } from '../i18n';
import { normalizeAppServerControlProvider } from './activationHelpers';
import { mountAgentChatAppearance } from '../settings/agentChatAppearance';
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
            <details class="agent-chat-quick-settings">
              <summary class="agent-chat-quick-trigger" data-i18n-title="settings.agentUi.quickSettings">
                <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round" aria-hidden="true">
                  <path d="M10 3h4l.65 2.55 1.55.9 2.55-.72 2 3.46-1.9 1.83v1.96l1.9 1.83-2 3.46-2.55-.72-1.55.9L14 21h-4l-.65-2.55-1.55-.9-2.55.72-2-3.46 1.9-1.83v-1.96l-1.9-1.83 2-3.46 2.55.72 1.55-.9L10 3Z"/>
                  <circle cx="12" cy="12" r="3"/>
                </svg>
                <span data-i18n="settings.agentUi.quickSettings"></span>
              </summary>
              <div class="agent-chat-quick-controls"></div>
            </details>
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
          <button type="button" class="agent-scroll-to-bottom" data-agent-field="scroll-to-bottom" hidden><svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M12 4v16m-6-6 6 6 6-6"/></svg></button>
          </div>
          <section class="agent-composer-shell" data-agent-field="composer-shell" hidden>
            <div class="agent-composer-interruption" data-agent-field="composer-interruption" hidden></div>
            <div class="agent-composer-host" data-agent-field="composer-host"></div>
          </section>
        </div>
      </section>
    `;
    bindAgentChatQuickSettings(panel);
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
}

function bindAgentChatQuickSettings(panel: HTMLDivElement): void {
  const details = panel.querySelector<HTMLDetailsElement>('.agent-chat-quick-settings');
  const trigger = details?.querySelector('summary');
  const controls = details?.querySelector<HTMLDivElement>('.agent-chat-quick-controls');
  if (!details || !trigger || !controls) {
    return;
  }
  const label = t('settings.agentUi.quickSettings');
  trigger.title = label;
  const accessibleName = trigger.querySelector('span');
  if (accessibleName) {
    accessibleName.textContent = label;
  }
  mountAgentChatAppearance(controls);

  details.addEventListener('focusout', (event) => {
    if (event.relatedTarget instanceof Node && !details.contains(event.relatedTarget)) {
      details.open = false;
    }
  });
  details.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && details.open) {
      event.preventDefault();
      event.stopPropagation();
      details.open = false;
      trigger.focus();
    }
  });
  panel.querySelector('.agent-chat-shell')?.addEventListener('pointerdown', (event) => {
    if (event.target instanceof Node && !details.contains(event.target)) {
      details.open = false;
    }
  });
}
