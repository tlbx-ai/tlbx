import { getProviderResumeCandidates } from '../../api/client';
import type { ProviderResumeCatalogEntryDto } from '../../api/types';
import { escapeHtml } from '../../utils/dom';
import { formatRelativeAge } from '../history/inputHistoryPanel';
import { getCurrentLocale, t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';

export type ResumeProvider = 'codex' | 'claude';
export type ProviderResumeScope = 'current' | 'all';

export interface OpenProviderResumePickerOptions {
  provider: ResumeProvider;
  workingDirectory?: string | null;
  initialScope?: ProviderResumeScope;
}

const LIST_KEY_STEPS: Readonly<Record<string, number>> = {
  ArrowDown: 1,
  ArrowUp: -1,
  PageDown: 8,
  PageUp: -8,
};

let activeResumePickerPromise: Promise<ProviderResumeCatalogEntryDto | null> | null = null;

export async function openProviderResumePicker(
  options: OpenProviderResumePickerOptions,
): Promise<ProviderResumeCatalogEntryDto | null> {
  if (activeResumePickerPromise) {
    return activeResumePickerPromise;
  }

  activeResumePickerPromise = openProviderResumePickerInternal(options);
  try {
    return await activeResumePickerPromise;
  } finally {
    activeResumePickerPromise = null;
  }
}

function openProviderResumePickerInternal(
  options: OpenProviderResumePickerOptions,
): Promise<ProviderResumeCatalogEntryDto | null> {
  return new Promise((resolve) => {
    const providerLabel = getProviderLabel(options.provider);
    const hasFolder = Boolean(options.workingDirectory?.trim());
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay provider-resume-picker-overlay';
    overlay.innerHTML = `
      <div class="modal provider-resume-picker-modal" role="dialog" aria-modal="true" aria-labelledby="provider-resume-picker-title">
        <div class="modal-content provider-resume-picker-content">
          <div class="modal-header">
            <div>
              <div class="provider-resume-picker-kicker">${escapeHtml(providerLabel)}</div>
              <h3 id="provider-resume-picker-title">${escapeHtml(t('providerResume.title'))}</h3>
            </div>
            <button class="modal-close" type="button" data-role="cancel" aria-label="${escapeHtml(t('dialog.cancel'))}">&times;</button>
          </div>
          <div class="modal-body provider-resume-picker-body">
            <div class="provider-resume-picker-toolbar">
              <input
                type="search"
                class="provider-resume-picker-search"
                data-role="search"
                role="combobox"
                aria-expanded="true"
                aria-controls="provider-resume-picker-list"
                aria-autocomplete="list"
                autocomplete="off"
                spellcheck="false"
                aria-label="${escapeHtml(t('providerResume.search'))}"
                placeholder="${escapeHtml(t('providerResume.search'))}"
              >
              <div class="provider-resume-picker-scope" data-role="scope" role="group">
                <button type="button" class="provider-resume-picker-scope-btn" data-scope="current"${hasFolder ? '' : ' disabled'}>${escapeHtml(t('providerResume.scopeFolder'))}</button>
                <button type="button" class="provider-resume-picker-scope-btn" data-scope="all">${escapeHtml(t('providerResume.scopeAll'))}</button>
              </div>
              <button type="button" class="btn-secondary provider-resume-picker-refresh" data-role="refresh">${escapeHtml(t('providerResume.refresh'))}</button>
            </div>
            <div class="provider-resume-picker-status" data-role="status" role="status" aria-live="polite"></div>
            <div class="provider-resume-picker-list" id="provider-resume-picker-list" data-role="list" role="listbox" aria-label="${escapeHtml(t('providerResume.title'))}"></div>
          </div>
          <div class="modal-footer provider-resume-picker-footer">
            <span class="provider-resume-picker-hint">${escapeHtml(t('providerResume.keyboardHint'))}</span>
            <button type="button" class="btn-secondary" data-role="cancel">${escapeHtml(t('dialog.cancel'))}</button>
          </div>
        </div>
      </div>
    `;

    const searchEl = overlay.querySelector<HTMLInputElement>('[data-role="search"]');
    const statusEl = overlay.querySelector<HTMLElement>('[data-role="status"]');
    const listEl = overlay.querySelector<HTMLElement>('[data-role="list"]');
    const scopeEl = overlay.querySelector<HTMLElement>('[data-role="scope"]');
    const refreshButton = overlay.querySelector<HTMLButtonElement>('[data-role="refresh"]');
    if (!searchEl || !statusEl || !listEl || !scopeEl || !refreshButton) {
      resolve(null);
      return;
    }

    const search = searchEl;
    const status = statusEl;
    const list = listEl;
    const scope = scopeEl;
    let loading = false;
    let errorMessage: string | null = null;
    let entries: ProviderResumeCatalogEntryDto[] = [];
    let visible: ProviderResumeCatalogEntryDto[] = [];
    let activeIndex = 0;
    let activeScope: ProviderResumeScope = hasFolder ? (options.initialScope ?? 'current') : 'all';
    let requestToken = 0;
    const releaseBackButtonLayer = registerBackButtonLayer(() => close(null));

    function close(result: ProviderResumeCatalogEntryDto | null): void {
      requestToken += 1;
      document.removeEventListener('keydown', onKeyDown, true);
      releaseBackButtonLayer();
      overlay.remove();
      resolve(result);
    }

    function applyFilter(): void {
      const terms = search.value.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
      visible = entries.filter((entry) => {
        const haystack = [entry.title, entry.previewText ?? '', entry.workingDirectory]
          .join('\n')
          .toLocaleLowerCase();
        return terms.every((term) => haystack.includes(term));
      });
      activeIndex = Math.min(activeIndex, Math.max(0, visible.length - 1));
    }

    function renderStatus(): void {
      let text = '';
      if (loading) {
        text = t('providerResume.loading');
      } else if (errorMessage) {
        text = t('providerResume.loadFailed').replace('{error}', errorMessage);
      } else if (entries.length === 0) {
        text = t(activeScope === 'current' ? 'providerResume.emptyFolder' : 'providerResume.empty');
      } else if (visible.length === 0) {
        text = t('providerResume.noMatches');
      } else {
        text = t('providerResume.count').replace('{count}', String(visible.length));
      }

      status.textContent = text;
      status.classList.toggle('error', Boolean(errorMessage));
      list.setAttribute('aria-busy', loading ? 'true' : 'false');
      for (const button of scope.querySelectorAll<HTMLElement>('[data-scope]')) {
        const isActive = button.dataset.scope === activeScope;
        button.classList.toggle('active', isActive);
        button.setAttribute('aria-pressed', isActive ? 'true' : 'false');
      }
    }

    function render(): void {
      renderStatus();
      const now = Date.now();
      list.innerHTML = visible
        .map(
          (entry, index) => `
            <div
              class="provider-resume-picker-row${index === activeIndex ? ' is-active' : ''}"
              id="provider-resume-option-${index}"
              role="option"
              aria-selected="${index === activeIndex ? 'true' : 'false'}"
              data-index="${index}"
            >
              <span class="provider-resume-picker-row-head">
                <span class="provider-resume-picker-row-title">${escapeHtml(entry.title)}</span>
                <time class="provider-resume-picker-row-time" datetime="${escapeHtml(entry.updatedAtUtc)}" title="${escapeHtml(formatAbsolute(entry.updatedAtUtc))}">${escapeHtml(formatRelativeAge(entry.updatedAtUtc, now))}</time>
              </span>
              ${
                entry.previewText
                  ? `<span class="provider-resume-picker-row-preview">${escapeHtml(entry.previewText)}</span>`
                  : ''
              }
              <span class="provider-resume-picker-row-meta">
                <span>${escapeHtml(providerLabel)}</span>
                <span class="provider-resume-picker-row-path" title="${escapeHtml(entry.workingDirectory)}">${escapeHtml(entry.workingDirectory)}</span>
                <span>${escapeHtml(formatAbsolute(entry.updatedAtUtc))}</span>
              </span>
            </div>
          `,
        )
        .join('');
      syncActiveOption();
    }

    function syncActiveOption(): void {
      for (const row of list.querySelectorAll<HTMLElement>('[role="option"]')) {
        const isActive = Number(row.dataset.index) === activeIndex;
        row.classList.toggle('is-active', isActive);
        row.setAttribute('aria-selected', isActive ? 'true' : 'false');
        if (isActive) {
          row.scrollIntoView({ block: 'nearest' });
        }
      }

      if (visible.length > 0) {
        search.setAttribute('aria-activedescendant', `provider-resume-option-${activeIndex}`);
      } else {
        search.removeAttribute('aria-activedescendant');
      }
    }

    async function loadEntries(): Promise<void> {
      const currentToken = ++requestToken;
      loading = true;
      errorMessage = null;
      renderStatus();
      try {
        const nextEntries = await getProviderResumeCandidates(options.provider, {
          workingDirectory: options.workingDirectory ?? null,
          scope: activeScope,
        });
        if (currentToken !== requestToken) {
          return;
        }

        entries = nextEntries;
      } catch (error) {
        if (currentToken !== requestToken) {
          return;
        }

        entries = [];
        errorMessage = error instanceof Error ? error.message : String(error);
      }

      loading = false;
      activeIndex = 0;
      applyFilter();
      render();
    }

    function onKeyDown(event: KeyboardEvent): void {
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        close(null);
        return;
      }

      const target = event.target as Node | null;
      if (target !== search && target !== document.body && !list.contains(target)) {
        return;
      }

      const step = LIST_KEY_STEPS[event.key];
      if (step !== undefined && visible.length > 0) {
        event.preventDefault();
        event.stopPropagation();
        activeIndex = Math.min(Math.max(activeIndex + step, 0), visible.length - 1);
        syncActiveOption();
        return;
      }

      if (event.key === 'Enter' && visible[activeIndex]) {
        event.preventDefault();
        event.stopPropagation();
        close(visible[activeIndex] ?? null);
      }
    }

    search.addEventListener('input', () => {
      activeIndex = 0;
      applyFilter();
      render();
    });

    scope.addEventListener('click', (event) => {
      const nextScope = (event.target as HTMLElement | null)?.closest<HTMLElement>('[data-scope]')
        ?.dataset.scope as ProviderResumeScope | undefined;
      if (!nextScope || nextScope === activeScope) {
        return;
      }

      activeScope = nextScope;
      void loadEntries();
    });

    refreshButton.addEventListener('click', () => {
      void loadEntries();
    });

    list.addEventListener('click', (event) => {
      const row = (event.target as HTMLElement | null)?.closest<HTMLElement>('[role="option"]');
      const entry = row ? visible[Number(row.dataset.index)] : undefined;
      if (entry) {
        close(entry);
      }
    });

    overlay.addEventListener('click', (event) => {
      const target = event.target as HTMLElement | null;
      if (target === overlay || target?.closest('[data-role="cancel"]')) {
        close(null);
      }
    });

    document.addEventListener('keydown', onKeyDown, true);
    document.body.appendChild(overlay);
    search.focus();
    void loadEntries();
  });
}

function getProviderLabel(provider: ResumeProvider): string {
  return t(provider === 'claude' ? 'sessionLauncher.claudeTitle' : 'sessionLauncher.codexTitle');
}

function formatAbsolute(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? ''
    : new Intl.DateTimeFormat(getCurrentLocale(), {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(date);
}
