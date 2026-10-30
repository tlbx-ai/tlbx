/**
 * Internationalization Module
 *
 * Lightweight i18n with JSON translation files, browser autodetection,
 * and cookie/settings override. Supports data-i18n attributes on DOM
 * elements and a t(key) lookup function for TypeScript usage.
 */

import { getCookie, setCookie } from '../../utils';
import { ASSET_VERSION } from '../../constants';

const SUPPORTED_LOCALES = ['en', 'zh', 'es', 'hi', 'fr', 'bn', 'pt', 'ru', 'ja', 'de'];
const LOCALE_COOKIE = 'mm-language';

let currentLocale = 'en';
let translations: Record<string, string> = {};
let fallbackTranslations: Record<string, string> = {};

/**
 * Translate a key to the current locale's string.
 * Falls back to English, then returns the key itself.
 */
export function t(key: string): string {
  return translations[key] ?? fallbackTranslations[key] ?? key;
}

/**
 * Get the current active locale code.
 */
export function getCurrentLocale(): string {
  return currentLocale;
}

declare global {
  interface Window {
    __tlbxI18n?: Readonly<{
      t: typeof t;
      getCurrentLocale: typeof getCurrentLocale;
    }>;
  }
}

// Connect's injected controls share this document and must use its native locale.
// Expose lookups only; settings remain the sole owner of language changes.
if (typeof window !== 'undefined') {
  window.__tlbxI18n = Object.freeze({ t, getCurrentLocale });
}

/**
 * Get the list of supported locale codes.
 */
export function getSupportedLocales(): string[] {
  return [...SUPPORTED_LOCALES];
}

/**
 * Initialize i18n: detect locale, load translations, apply to DOM.
 * Call this once at page startup.
 */
export async function initI18n(settingsLanguage?: string): Promise<void> {
  currentLocale = detectLocale(settingsLanguage);

  if (currentLocale !== 'en') {
    fallbackTranslations = await loadTranslations('en');
    translations = await loadTranslations(currentLocale);
  } else {
    translations = await loadTranslations('en');
    fallbackTranslations = translations;
  }

  applyTranslations();
  applyAttributeTranslations();
  document.documentElement.lang = currentLocale;
  document.dispatchEvent(new Event('tlbx-locale-changed'));
}

/**
 * Switch to a new locale at runtime. Fetches the new translation file
 * and re-applies all DOM translations.
 */
export async function setLocale(locale: string): Promise<void> {
  if (!SUPPORTED_LOCALES.includes(locale) && locale !== 'auto') {
    return;
  }

  if (locale === 'auto') {
    setCookie(LOCALE_COOKIE, 'auto');
    currentLocale = detectBrowserLocale();
  } else {
    setCookie(LOCALE_COOKIE, locale);
    currentLocale = locale;
  }

  if (currentLocale !== 'en') {
    if (Object.keys(fallbackTranslations).length === 0) {
      fallbackTranslations = await loadTranslations('en');
    }
    translations = await loadTranslations(currentLocale);
  } else {
    translations = fallbackTranslations;
  }

  applyTranslations();
  applyAttributeTranslations();
  document.documentElement.lang = currentLocale;
  document.dispatchEvent(new Event('tlbx-locale-changed'));
}

/**
 * Walk all elements with [data-i18n] and set textContent.
 */
export function applyTranslations(): void {
  document.querySelectorAll('[data-i18n]').forEach((el) => {
    const key = (el as HTMLElement).dataset.i18n;
    if (key) {
      el.textContent = t(key);
    }
  });
}

/**
 * Walk elements with data-i18n-title, data-i18n-placeholder, etc.
 */
export function applyAttributeTranslations(): void {
  document.querySelectorAll('[data-i18n-title]').forEach((el) => {
    const key = (el as HTMLElement).dataset.i18nTitle;
    if (key) {
      (el as HTMLElement).title = t(key);
    }
  });

  document.querySelectorAll('[data-i18n-placeholder]').forEach((el) => {
    const key = (el as HTMLElement).dataset.i18nPlaceholder;
    if (key) {
      (el as HTMLInputElement).placeholder = t(key);
    }
  });

  document.querySelectorAll('[data-i18n-text]').forEach((el) => {
    const key = (el as HTMLElement).dataset.i18nText;
    if (key) {
      (el as HTMLElement).setAttribute('data-tooltip', t(key));
    }
  });
}

/**
 * Detect the best locale to use.
 */
function detectLocale(settingsLanguage?: string): string {
  const cookie = getCookie(LOCALE_COOKIE);
  if (cookie && cookie !== 'auto' && SUPPORTED_LOCALES.includes(cookie)) {
    return cookie;
  }

  if (
    settingsLanguage &&
    settingsLanguage !== 'auto' &&
    SUPPORTED_LOCALES.includes(settingsLanguage)
  ) {
    return settingsLanguage;
  }

  return detectBrowserLocale();
}

function detectBrowserLocale(): string {
  const browserLang = navigator.language.split('-')[0] ?? 'en';
  if (SUPPORTED_LOCALES.includes(browserLang)) {
    return browserLang;
  }
  return 'en';
}

/**
 * Fetch a locale's JSON translation file.
 */
async function loadTranslations(locale: string): Promise<Record<string, string>> {
  try {
    const version = encodeURIComponent(ASSET_VERSION);
    const resp = await fetch(`/locales/${locale}.json?v=${version}`);
    if (resp.ok) {
      return (await resp.json()) as Record<string, string>;
    }
  } catch {
    // Fall through to empty
  }
  return {};
}
