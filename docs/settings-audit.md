# Settings Audit

## Why tlbx Settings Feel Complicated

The same setting is usually hand-wired in several places:

1. `Settings/MidTermSettings.cs` defines the internal value.
2. `Settings/MidTermSettingsPublic.cs` defines the API-facing value.
3. `Settings/MidTermSettingsPublic.Runtime.cs` manually copies the value in `FromSettings()` and `ApplyTo()`.
4. `Settings/SettingsService.cs` may also touch the value in default migration, `.old` migration, or installer merge logic.
5. `src/ts/modules/settings/persistence.ts` manually populates the form and manually rebuilds the PUT payload.
6. One or more frontend/runtime modules apply the setting after load.

That means a single setting can easily have 6-8 touch points before it actually works.

There are also three different application modes mixed together:

- Immediate UI settings: theme, font, cursor, transparency, language.
- Lazy runtime settings: bell style, clipboard behavior, input mode, file radar. These are read from `$currentSettings` when an event happens.
- Creation-time / server-side settings: default shell, working directory, run-as-user, update channel.

Finally, some settings bypass the main `saveAllSettings()` path entirely:

- `managerBarButtons` is saved from `modules/managerBar/managerBar.ts`.
- `showChangelogAfterUpdate` can also be changed from `modules/updating/changelog.ts`.
- `devMode` is toggled from the hidden version-click handler.
- Background image metadata is written by dedicated upload/delete endpoints, not by the normal settings PUT.

This audit now has a matching code registry in `src/ts/modules/settings/registry.ts`. It centralizes the frontend metadata for editability, control ownership, validation shape, apply mode, and special writers.

## End-To-End Flow

| Stage | Main Files | Notes |
| --- | --- | --- |
| Canonical model | `Settings/MidTermSettings.cs`, `Settings/MidTermSettingsPublic.cs` | Internal settings and public API settings are separate types. |
| Backend mapping | `Settings/MidTermSettingsPublic.Runtime.cs` | `FromSettings()` and `ApplyTo()` manually copy fields. |
| Persistence and migration | `Settings/SettingsService.cs` | Handles disk save, backup write, secrets, missing defaults, `.old` migration, installer merge. |
| Initial load | `src/ts/modules/bootstrap/index.ts` | `/api/bootstrap` seeds `$currentSettings`, populates form controls, and triggers first live apply. |
| Settings panel save | `src/ts/modules/settings/persistence.ts` | Reads DOM controls, builds a full PUT payload, applies optimistic local changes. |
| Server save | `Startup/EndpointSetup.cs` | `PUT /api/settings` loads current settings, applies public updates, and saves. |
| Cross-client sync | `Services/WebSockets/SettingsWebSocketHandler.cs`, `src/ts/modules/comms/settingsChannel.ts` | Saved settings are rebroadcast over `/ws/settings`. |
| Runtime consumers | `main.ts`, `modules/terminal/*`, `modules/i18n`, `modules/updating`, `modules/managerBar`, `modules/smartInput` | Each setting has its own consumer logic; there is no single application registry. |

## Findings

- Font behavior was split across save wiring, terminal option updates, preload, and sizing. The selected font was not consistently used for preload/calibration/sizing, so font changes could lag or size against old Cascadia metrics.
- `fontSize` used manual text-input semantics (`change` / Enter / button), so it did not react on `input` like the preview-style settings.
- Normal session creation was not sending `defaultShell` or `defaultWorkingDirectory`, so those settings were effectively no-ops in the common "new terminal" path.
- The public update type exposed background image metadata even though the server treats that metadata as read-only.
- There is no single authoritative "settings registry". The repo currently recreates that knowledge by hand across backend model mapping, migration, frontend form wiring, and runtime consumers.

## Settings Table

### Session Defaults And Spawn Settings

| Setting | Written From | Applied By | Live Effect | Notes |
| --- | --- | --- | --- | --- |
| `defaultShell` | Settings panel select | Sent in normal `apiCreateSession()` call; used by session spawn on server | New sessions only | Fixed in this change; previously not sent in the standard create path. |
| `defaultCols` | No current settings control | Read in `main.ts` as fallback for new session dimensions | New sessions only | Model-backed, but not user-editable in current UI. |
| `defaultRows` | No current settings control | Read in `main.ts` as fallback for new session dimensions | New sessions only | Model-backed, but not user-editable in current UI. |
| `defaultWorkingDirectory` | Settings panel text input | Sent in normal `apiCreateSession()` call | New sessions only | Fixed in this change; previously not sent in the standard create path. |
| `runAsUser` | Settings panel select | Server updates `TtyHostSessionManager`, `GitCommandRunner`, and clipboard service paths | New sessions / server actions only | Affects future spawns and service-mode helpers, not already running shells. |
| `runAsUserSid` | Server / installer derived | Exposed to client, ignored on PUT | Read-only | Should be treated as metadata, not a user setting. |

### Appearance Settings

| Setting | Written From | Applied By | Live Effect | Notes |
| --- | --- | --- | --- | --- |
| `fontSize` | Settings panel number input | `applySettingsToTerminals()`, terminal sizing, new-session measurement | Immediate on existing terminals and new session sizing | Now previews and saves on `input`; was previously a delayed manual-save path. |
| `fontFamily` | Settings panel select | `applySettingsToTerminals()`, font preload, terminal sizing, new-session measurement | Immediate on existing terminals and new session sizing | Fixed to use selected font in preload and measurement rather than default Cascadia-only assumptions. |
| `cursorStyle` | Settings panel select | xterm option update in `applySettingsToTerminals()` | Immediate | Existing terminals update in place. |
| `cursorBlink` | Settings panel checkbox | xterm option update in `applySettingsToTerminals()` | Immediate | Also interacts with cursor-blink refresh logic in terminal manager. |
| `cursorInactiveStyle` | Settings panel select | xterm option update in `applySettingsToTerminals()` | Immediate | Existing terminals update in place. |
| `hideCursorOnInputBursts` | Settings panel checkbox | Read by mux burst handling and partially reconciled in `applySettingsToTerminals()` | Immediate / next burst | Live effect is mixed because some behavior is event-driven. |
| `theme` | Settings panel select | `applyCssTheme()`, terminal theme, cookie, update panel render | Immediate | Full UI and terminal chrome update. |
| `terminalColorScheme` | Settings panel select | `getEffectiveXtermTheme()` -> `applySettingsToTerminals()` | Immediate | Terminal palette only. |
| `backgroundImageEnabled` | Settings panel checkbox, upload/delete endpoints | `applyBackgroundAppearance()` | Immediate | UI background only; file metadata comes from special endpoints. |
| `backgroundImageFileName` | Upload/delete endpoints | `applyBackgroundAppearance()` / preview UI | Read-only metadata | Returned in GET/bootstrap; ignored on normal PUT. |
| `backgroundImageRevision` | Upload/delete endpoints | Cache-busting for background image URL | Read-only metadata | Returned in GET/bootstrap; ignored on normal PUT. |
| `backgroundKenBurnsEnabled` | Settings panel checkbox | `applyBackgroundAppearance()` | Immediate | Enables or disables the animated background orbit for wallpaper images. |
| `backgroundKenBurnsZoomPercent` | Settings panel range input | `applyBackgroundAppearance()` | Immediate | Controls the wallpaper zoom factor from 150% to 300%. |
| `backgroundKenBurnsSpeedPxPerSecond` | Settings panel range input | `applyBackgroundAppearance()` | Immediate | Controls the wallpaper orbit speed in pixels per second. |
| `uiTransparency` | Settings panel range input | `applyBackgroundAppearance()` | Immediate | Already had live slider preview before save. |
| `tabTitleMode` | Settings panel select | `updateTabTitle()` / `modules/tabTitle.ts` | Immediate | Applies to browser tab title logic. |
| `minimumContrastRatio` | Settings panel select | xterm option update in `applySettingsToTerminals()` | Immediate | Terminal text contrast only. |
| `smoothScrolling` | Settings panel checkbox | xterm option update in `applySettingsToTerminals()` and `getTerminalOptions()` | Immediate | Existing terminals and future terminals. |
| `scrollbarStyle` | Settings panel select | Terminal container CSS classes | Immediate | Container-level presentation. |
| `useWebGL` | Settings panel checkbox | `syncTerminalWebglState()` | Immediate | Existing terminals can attach/detach WebGL. |
| `language` | Settings panel select | `setLocale()`, language cookie, i18n render paths | Immediate | Full UI language update. |

### Terminal Behavior And Interaction Settings

| Setting | Written From | Applied By | Live Effect | Notes |
| --- | --- | --- | --- | --- |
| `scrollbackLines` | Settings panel number input | xterm option update in `applySettingsToTerminals()` and `getTerminalOptions()` | Immediate | Existing terminals update in place; also affects future terminals. |
| `bellStyle` | Settings panel select | Read from `$currentSettings` in `main.ts` when bell events arrive | Next bell event | Event-driven rather than pushed into terminal options. |
| `copyOnSelect` | Settings panel checkbox | Read from `$currentSettings` inside terminal selection handlers | Next selection event | Lazy-read behavior. |
| `rightClickPaste` | Settings panel checkbox | Read from `$currentSettings` inside terminal context-menu handlers | Next right click | Lazy-read behavior. |
| `clipboardShortcuts` | Settings panel select | Read from `$currentSettings` inside keyboard shortcut handling | Next shortcut event | Lazy-read behavior. |
| `terminalEnterMode` | Settings panel select | Read from `$currentSettings` inside Enter-key handling | Next Enter key | Lazy-read behavior. |
| `scrollbackProtection` | Settings panel checkbox | Read in `applyScrollbackProtection()` visibility path | Next protection-triggering event | Not a continuous terminal option. |
| `inputMode` | Settings panel select | `smartInput.ts`, touch controller, focus logic | Immediate | Has a subscription-based live UI path. |
| `fileRadar` | Settings panel checkbox | Read from `$currentSettings` in file-link detection | Next scan / output event | Lazy-read behavior. |

### Integration, UI, Update, And Runtime Settings

| Setting | Written From | Applied By | Live Effect | Notes |
| --- | --- | --- | --- | --- |
| `tmuxCompatibility` | Settings panel checkbox | Server-side tmux compatibility paths | Future spawns / server behavior | No meaningful frontend live-apply path. |
| `ideMode` | Settings panel checkbox | `$currentSettings` subscription in `main.ts`, session tabs, git/web/commands docking | Immediate | Also affects some server-side IDE behavior checks. |
| `managerBarEnabled` | Settings panel checkbox | `managerBar.ts` subscription | Immediate | UI only. |
| `managerBarButtons` | Manager bar editor UI | `managerBar.ts` subscription and direct `updateSettings()` call | Immediate | Special-case writer that bypasses normal `saveAllSettings()`. |
| `devMode` | Hidden version-click toggle and settings payload | Bootstrap display, update environment checks, voice/dev feature gating | Mixed | Another special-case writer outside the main settings form. |
| `showChangelogAfterUpdate` | Settings panel checkbox and changelog modal | Update UI logic in `modules/updating` | Immediate / next update result | Also has a direct special-case writer in `changelog.ts`. |
| `showUpdateNotification` | Settings panel checkbox | Update panel visibility logic in `modules/updating/checker.ts` | Immediate | UI only. |
| `updateChannel` | Settings panel select | Server-side `UpdateService` | Next update check | Server behavior only; not an immediate UI toggle. |

### Security And Install Metadata

| Setting | Written From | Applied By | Live Effect | Notes |
| --- | --- | --- | --- | --- |
| `authenticationEnabled` | Auth/password endpoints and installer merge | Auth middleware, login/bootstrap state | Read-only in normal settings PUT | Publicly exposed, but intentionally not user-editable via `/api/settings`. |
| `certificatePath` | Installer, certificate generation, CLI | HTTPS certificate loading and diagnostics | Read-only in normal settings PUT | Publicly exposed metadata, not a user-facing settings control. |

## Biggest Repeated Patterns

- Manual property copying: backend and frontend both rebuild the full settings object by hand.
- Mixed write surfaces: normal form PUT, special endpoints, hidden toggles, and direct module writes all coexist.
- Mixed apply styles: immediate push, lazy runtime reads, and creation-time server-only behavior are all stored in the same shape.
- Public/read-only metadata lives next to true user-editable settings, which makes the client update contract harder to reason about.

## Registry Status

The frontend now has a real settings registry that defines, for each setting:

- whether it is editable or read-only,
- where it is stored,
- how it is validated,
- how it is applied (`immediate`, `lazy`, `new-session`, `server-only`),
- which frontend control owns it,
- and whether it has a special writer outside the main settings form.

That removes one large source of duplication from the settings form path. The next step, if we want to push this further, is to make backend mapping/migration consume the same kind of registry instead of maintaining a separate manual list there.
