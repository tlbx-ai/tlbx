const fs = require('node:fs');
const path = require('node:path');

const STAGED_REQUIRED_PREFIXES = [
  'auth.',
  'trust.',
  'sidebar.',
  'share.',
  'update.',
  'settings.hub.',
];
const STAGED_REQUIRED_KEYS = ['settings.general.showUpdateNotification'];

function getLocaleDir() {
  return path.resolve(__dirname, '../src/static/locales');
}

function readLocaleFile(localeDir, locale) {
  return JSON.parse(fs.readFileSync(path.join(localeDir, `${locale}.json`), 'utf8'));
}

function hasRequiredPrefix(key, prefixes) {
  return prefixes.some((prefix) => key.startsWith(prefix));
}

function isRequiredKey(key, prefixes, exactKeys) {
  return exactKeys.includes(key) || hasRequiredPrefix(key, prefixes);
}

function createLocaleParityReport(
  canonicalLocale = 'en',
  requiredPrefixes = STAGED_REQUIRED_PREFIXES,
  requiredKeys = STAGED_REQUIRED_KEYS,
) {
  const localeDir = getLocaleDir();
  const locales = fs
    .readdirSync(localeDir)
    .filter((file) => file.endsWith('.json'))
    .map((file) => file.slice(0, -'.json'.length))
    .sort();
  const canonical = readLocaleFile(localeDir, canonicalLocale);
  const canonicalKeys = Object.keys(canonical);
  const canonicalKeySet = new Set(canonicalKeys);
  const issues = [];

  for (const locale of locales) {
    if (locale === canonicalLocale) {
      continue;
    }

    const translations = readLocaleFile(localeDir, locale);
    const keys = Object.keys(translations);
    const keySet = new Set(keys);

    for (const key of canonicalKeys) {
      if (!keySet.has(key)) {
        issues.push({
          locale,
          type: 'missing',
          key,
          severity: isRequiredKey(key, requiredPrefixes, requiredKeys) ? 'error' : 'warn',
        });
      }
    }

    for (const key of keys) {
      if (!canonicalKeySet.has(key)) {
        issues.push({
          locale,
          type: 'extra',
          key,
          severity: 'error',
        });
      }
    }
  }

  return {
    canonicalLocale,
    localeDir,
    locales,
    issues,
  };
}

module.exports = {
  STAGED_REQUIRED_PREFIXES,
  createLocaleParityReport,
};
