import { updateSettings } from '../../api/client';
import { $currentSettings } from '../../stores';
import { showAlert } from '../../utils/dialog';
import { t } from '../i18n';
import { createLogger } from '../logging';

const sliders = {
  agentChatFontScale: { label: 'chatFontScale', min: 80, max: 160, initial: 100, unit: '%' },
  agentChatPanelTransparency: {
    label: 'chatPanelTransparency',
    min: -1,
    max: 100,
    initial: -1,
    unit: '%',
  },
  agentChatColumnWidth: { label: 'chatColumnWidth', min: 24, max: 100, initial: 54, unit: ' rem' },
} as const;

type AppearanceKey = keyof typeof sliders;

const log = createLogger('settings');
const drafts = new Map<AppearanceKey, { value: number }>();
let nextControlId = 0;
let saveQueue = Promise.resolve();
let listening = false;

function readValue(key: AppearanceKey): number {
  return drafts.get(key)?.value ?? $currentSettings.get()?.[key] ?? sliders[key].initial;
}

function renderSlider(input: HTMLInputElement, key: AppearanceKey): void {
  const value = readValue(key);
  input.disabled = !$currentSettings.get();
  if (input.valueAsNumber !== value) {
    input.valueAsNumber = value;
  }
  const label =
    value === -1 ? t('settings.agentUi.chatTransparencyAuto') : String(value) + sliders[key].unit;
  input.setAttribute('aria-valuetext', label);
  const output = input.nextElementSibling;
  if (output && output.textContent !== label) {
    output.textContent = label;
  }
}

function renderAppearance(): void {
  const style = document.documentElement.style;
  const scale = readValue('agentChatFontScale');
  const width = readValue('agentChatColumnWidth');
  const transparency = readValue('agentChatPanelTransparency');
  style.setProperty('--agent-chat-font-scale', String(scale / 100));
  if (width === sliders.agentChatColumnWidth.initial) {
    style.removeProperty('--agent-chat-column-width');
  } else {
    style.setProperty('--agent-chat-column-width', String(width) + 'rem');
  }
  if (transparency === -1) {
    style.removeProperty('--agent-chat-background');
  } else {
    style.setProperty(
      '--agent-chat-background',
      'color-mix(in srgb, var(--bg-terminal) ' + String(100 - transparency) + '%, transparent)',
    );
  }
  document.querySelectorAll<HTMLInputElement>('[data-agent-chat-appearance]').forEach((input) => {
    renderSlider(input, input.dataset.agentChatAppearance as AppearanceKey);
  });
}

function saveAppearance(key: AppearanceKey, value: number): void {
  const draft = { value };
  drafts.set(key, draft);
  const patch = { [key]: value === -1 ? null : value };
  renderAppearance();
  saveQueue = saveQueue.then(async () => {
    try {
      const { response } = await updateSettings(patch);
      if (!response.ok) {
        throw new Error(String(response.status));
      }
      const current = $currentSettings.get();
      if (current) {
        $currentSettings.set({ ...current, ...patch });
      }
    } catch (error) {
      log.error(() => 'Could not save chat appearance: ' + String(error));
      void showAlert(t('settings.agentUi.chatAppearanceSaveFailed'));
    } finally {
      if (drafts.get(key) === draft) {
        drafts.delete(key);
        renderAppearance();
      }
    }
  });
}

export function mountAgentChatAppearance(container: HTMLElement): void {
  if (!listening) {
    listening = true;
    $currentSettings.subscribe(renderAppearance);
    document.addEventListener('tlbx-locale-changed', renderAppearance);
  }

  for (const key of Object.keys(sliders) as AppearanceKey[]) {
    const slider = sliders[key];
    const row = document.createElement('div');
    row.className = 'setting-row';
    const label = document.createElement('label');
    label.dataset.i18n = 'settings.agentUi.' + slider.label;
    label.textContent = t(label.dataset.i18n);
    const sliderRow = document.createElement('div');
    sliderRow.className = 'transparency-slider-row';
    const input = document.createElement('input');
    input.type = 'range';
    input.id = 'agent-chat-appearance-' + String(++nextControlId);
    input.dataset.agentChatAppearance = key;
    input.min = String(slider.min);
    input.max = String(slider.max);
    label.htmlFor = input.id;
    const value = document.createElement('span');
    value.className = 'slider-value';
    value.setAttribute('aria-hidden', 'true');
    sliderRow.append(input, value);
    row.append(label, sliderRow);
    container.append(row);

    let preview: { value: number } | undefined;
    input.addEventListener('input', () => {
      preview = { value: input.valueAsNumber };
      drafts.set(key, preview);
      renderAppearance();
    });
    input.addEventListener('change', () => {
      preview = undefined;
      saveAppearance(key, input.valueAsNumber);
    });
    input.addEventListener('pointercancel', () => {
      if (preview && drafts.get(key) === preview) {
        drafts.delete(key);
        renderAppearance();
      }
      preview = undefined;
    });
    renderSlider(input, key);
  }
}
