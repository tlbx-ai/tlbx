# Localization

`src/Ai.Tlbx.MidTerm/src/static/locales/en.json` is the canonical locale file.

## Goals

- Keep all shipped locales structurally aligned with English.
- Prefer native product tone over literal translation.
- Preserve product names and common technical loanwords when they sound natural in the target language.
- Avoid mixed-language UI in high-visibility surfaces such as sidebar, settings, share flows, update flows, and Agent Controller Session.

## Tone Rules

- Keep `tlbx`, `Codex`, `Claude`, `Git`, `tmux`, `API key`, and platform names unchanged unless a locale already uses an established native equivalent.
- Keep borrowed product/UI words such as `Session`, `Frontend`, `Agent`, or `Host` when that is the more natural product voice for the locale's audience.
- Prefer concise in-product wording over formal or textbook phrasing.
- Avoid translating strings mechanically key by key; preserve meaning, action, and expected UX tone.
- Reuse a locale's existing vocabulary before introducing new wording.

## Process

1. Add or update the English source string first.
2. Update every shipped locale file in the same change for any user-visible string when feasible.
3. If full parity is not yet practical, at minimum update the staged high-visibility scope:
   - `sidebar.`
   - `share.`
   - `update.`
   - `settings.hub.`
   - `settings.general.showUpdateNotification`
4. Remove stale locale-only keys once the canonical English key no longer exists.
5. Run the locale parity checks before merging.

## Checks

From `src/Ai.Tlbx.MidTerm`:

```powershell
npm run locales:report
npm run locales:check
```

`locales:check` currently enforces parity for the staged high-visibility scope above and rejects stale extra keys in localized files. Remaining missing keys outside that staged scope are reported as warnings until they are fully backfilled.
