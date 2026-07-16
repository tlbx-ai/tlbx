# tlbx Settings Inventory

This document is a current-source inventory of tlbx's settings surfaces. It is meant as a starting point for a future settings reorganization, not as polished end-user documentation.

Current sources used for this pass:

- `src/Ai.Tlbx.MidTerm/src/static/index.html`
- `src/Ai.Tlbx.MidTerm/src/ts/modules/settings/registry.ts`
- `src/Ai.Tlbx.MidTerm/src/ts/modules/settings/persistence.ts`
- `src/Ai.Tlbx.MidTerm/src/ts/modules/appServerControl/quickSettings.ts`
- `src/Ai.Tlbx.MidTerm/src/ts/modules/hub/settings.ts`
- `src/Ai.Tlbx.MidTerm/src/ts/modules/diagnostics/panel.ts`
- `src/Ai.Tlbx.MidTerm/src/ts/modules/managerBar/managerBar.ts`

## Legend

- `settings.json`: regular persisted setting owned by the main settings model.
- `endpoint-managed`: persisted or stateful data owned by a dedicated API, not the normal settings PUT flow.
- `localStorage`: browser-local setting or toggle, not part of `settings.json`.
- `read-only`: surfaced in a settings menu but not editable.
- `action`: button, link, or workflow launcher rather than a persisted setting.
- `Immediate`: takes effect on the current UI/runtime right away.
- `Next event`: applied the next time the relevant event happens.
- `New sessions`: affects new sessions/runtimes rather than already-running ones.
- `Server-only`: changes backend/runtime behavior more than the current browser UI.

## Settings Surfaces

tlbx currently has four separate settings-like surfaces:

1. The main tabbed Settings panel.
2. Agent Controller Session quick settings in the session footer rail/sheet.
3. The Automation Bar button editor modal.
4. A handful of hidden or indirect settings that exist in the settings model but are not exposed as normal settings rows.

## Main Settings Panel

Current top-level order in the UI:

1. `Updates & About`
2. `Terminal`
3. `AI Agents`
4. `Workflow & Git`
5. `Appearance`
6. `Sessions & Startup`
7. `Security & Access`
8. `Connected Hosts`
9. `Advanced & Diagnostics`

### Updates & About

- `Update Preferences`
  `Show updates prominently in sidebar` — `showUpdateNotification`, `settings.json`, Immediate. Controls whether update notices are surfaced prominently in sidebar/update UI.
  `Update channel` — `updateChannel`, `settings.json`, Server-only. Chooses stable vs dev/prerelease feed.
  `Show changelog after update` — `showChangelogAfterUpdate`, `settings.json`, Next update result. Reopens the changelog after a successful update.
- `Update Actions`
  `Check for Updates` — action, Immediate. Triggers an update check against the configured channel.
  `Changelog` — action, Immediate. Opens the current changelog UI.
  `Update Log` — action, Immediate. Opens update-log details from the updater.
  `Install as App` — action, Immediate. Triggers the browser/PWA install flow when supported.
- `Versions & Trust`
  `Server` — read-only, Immediate. Shows the `mt.exe` server version. Clicking it 7 times toggles hidden `devMode`.
  `Frontend` — read-only, Immediate. Shows the current frontend bundle version.
  `Host` — read-only, Immediate. Shows the `mthost` / tty host version.
  `Environment` — read-only / hidden toggle, Immediate. Shows a `DEV` badge when hidden dev mode is enabled.
  `Code Signing` — read-only, Immediate. Shows whether the running binary reports a valid code signature.
- `System Status`
  `Health summary` — read-only, Immediate. Shows health, mode, sessions, uptime, platform, process ID, and tty host compatibility status.
- `Server`
  `CLI flag hints` — read-only. Explains that bind/port are controlled by command-line arguments rather than the settings model.
- `Open Source Licenses`
  `OSS list` — read-only. Lists bundled third-party components and licenses.

### Terminal

- `Typography & Theme`
  `Font Size` — `fontSize`, `settings.json`, Immediate. Changes terminal font size for existing terminals and new-session sizing.
  `Font` — `fontFamily`, `settings.json`, Immediate. Chooses the terminal font family used for rendering and sizing.
  `Terminal Colors` — `terminalColorScheme`, `settings.json`, Immediate. Chooses the active terminal palette. `auto` follows the UI theme; `Dark2` provides a pure black terminal background and direct RGB ANSI colors.
  `Custom Scheme Editor` — `terminalColorSchemes`, special writer, Immediate. Loads presets, creates blank schemes, edits palette fields, saves custom schemes, and deletes custom schemes.
  `Enable Ligatures` — `terminalLigaturesEnabled`, `settings.json`, Immediate. Enables ligatures in terminal rendering.
- `Layout & Cursor`
  `Line Height` — `lineHeight`, `settings.json`, Immediate.
  `Letter Spacing` — `letterSpacing`, `settings.json`, Immediate.
  `Font Weight` — `fontWeight`, `settings.json`, Immediate.
  `Bold Font Weight` — `fontWeightBold`, `settings.json`, Immediate.
  `Terminal Scrollbar` — `scrollbarStyle`, `settings.json`, Immediate.
  `Cursor Style` — `cursorStyle`, `settings.json`, Immediate.
  `Cursor Blink` — `cursorBlink`, `settings.json`, Immediate.
  `Cursor (Unfocused)` — `cursorInactiveStyle`, `settings.json`, Immediate.
- `Drawing & Transparency`
  `Box Drawing` — `customGlyphs`, `settings.json`, Immediate. Chooses custom glyphs vs font glyphs for box drawing.
  `Box Drawing Style` — `boxDrawingStyle`, `settings.json`, Immediate.
  `Box Stroke Scale` — `boxDrawingScale`, `settings.json`, Immediate.
  `Terminal Transparency` — `terminalTransparency`, `settings.json`, Immediate.
  `Terminal Cell Background Transparency` — `terminalCellBackgroundTransparency`, `settings.json`, Immediate.
- `Terminal Behavior`
  `Scrollback Lines` — `scrollbackLines`, `settings.json`, Immediate.
  `Scrollback Bytes` — `scrollbackBytes`, `settings.json`, Immediate.
  `GPU Rendering (WebGL)` — `useWebGL`, `settings.json`, Immediate.
  `Smooth Scrolling` — `smoothScrolling`, `settings.json`, Immediate.
  `Bell Style` — `bellStyle`, `settings.json`, Next event.
  `Copy on Select` — `copyOnSelect`, `settings.json`, Next event.
  `Right-Click Paste` — `rightClickPaste`, `settings.json`, Next event.
  `Copy/Paste Shortcuts` — `clipboardShortcuts`, `settings.json`, Next event.
  `Modified Enter = newline in Terminal` — `terminalEnterMode`, `settings.json`, Immediate.
  `Claude Code scrollback glitch protection` — `scrollbackProtection`, `settings.json`, Next event.

### AI Agents

- `Conversation View`
  `Message Font` — `agentMessageFontFamily`, `settings.json`, Immediate. Changes the font stack used for rendered agent history.
  `Show Timestamps On Agent Messages` — `showAgentMessageTimestamps`, `settings.json`, Immediate.
  `Show Unknown Agent Messages` — `showUnknownAgentMessages`, `settings.json`, Immediate.
- `Codex Defaults`
  `Enable Codex --yolo by default` — `codexYoloDefault`, `settings.json`, New AI sessions.
  `Codex Environment Variables` — `codexEnvironmentVariables`, `settings.json`, New AI sessions.
- `Claude Defaults`
  `Enable Claude --dangerously-skip-permissions by default` — `claudeDangerouslySkipPermissionsDefault`, `settings.json`, New AI sessions.
  `Claude Environment Variables` — `claudeEnvironmentVariables`, `settings.json`, New AI sessions.

### Workflow & Git

- `Workflow Controls`
  `Input Mode` — `inputMode`, `settings.json`, Immediate. Chooses keyboard, Smart Input, or both.
  `Automation Bar` — `managerBarEnabled`, `settings.json`, Immediate. Shows or hides the Automation Bar.
  `Enable Ligatures` — `commandBayLigaturesEnabled`, `settings.json`, Immediate. Enables ligature-style rendering in Smart Input / Automation Bar surfaces.
- `Git & Worktrees`
  `Worktree Root Directory` — `worktreeRootDirectory`, `settings.json`, New sessions / server workflows. Sets the root folder tlbx uses for managed worktree and Spaces flows.
- `Navigation & Discovery`
  `Browser Tab Title` — `tabTitleMode`, `settings.json`, Immediate. Chooses hostname/static/session-name/terminal-title/foreground-process tab naming.
  `Show Bookmarks` — `showBookmarks`, `settings.json`, Immediate.
  `Allow Ad-hoc Session Bookmarks` — `allowAdHocSessionBookmarks`, `settings.json`, Immediate.
  `Sidebar Terminal Filter` — `showSidebarSessionFilter`, `settings.json`, Immediate.
  `File Radar` — `fileRadar`, `settings.json`, Next event. Detects file paths in terminal output and makes them clickable.

### Appearance

- `Theme & Language`
  `Theme` — `theme`, `settings.json`, Immediate. Changes the main UI theme and the default terminal palette when terminal colors are set to `auto`.
  `Language` — `language`, `settings.json`, Immediate. `auto` follows the browser language.
- `Background Image`
  `Upload Image` — endpoint-managed, Immediate. Uploads a PNG/JPG wallpaper and stores it outside the normal settings PUT path.
  `Remove` — endpoint-managed, Immediate. Deletes the uploaded wallpaper and clears the active background.
  `Show Background Image` — `backgroundImageEnabled`, `settings.json`, Immediate.
  `Hide Background On Mobile` — `hideBackgroundImageOnMobile`, `settings.json`, Immediate.
- `Motion & Transparency`
  `Animated Ken Burns` — `backgroundKenBurnsEnabled`, `settings.json`, Immediate.
  `Ken Burns Zoom` — `backgroundKenBurnsZoomPercent`, `settings.json`, Immediate.
  `Ken Burns Speed` — `backgroundKenBurnsSpeedPxPerSecond`, `settings.json`, Immediate.
  `UI Transparency` — `uiTransparency`, `settings.json`, Immediate.

Notes:

- Wallpaper file metadata is stored separately as `backgroundImageFileName` and `backgroundImageRevision`.
- The uploaded image itself is managed by dedicated upload/delete endpoints, not the normal settings payload.

### Sessions & Startup

- `New Sessions`
  `Default Shell` — `defaultShell`, `settings.json`, New sessions.
  `New Session Start Location` — `defaultWorkingDirectory`, `settings.json`, New sessions.
  `Terminal Environment Variables` — `terminalEnvironmentVariables`, `settings.json`, New sessions.
- `Session Runtime`
  `Session Resume Mode` — `resumeMode`, `settings.json`, Server-only. Chooses `fullReplay` vs `quickResume` behavior for hidden session catch-up.
  `Keep computer awake while sessions exist` — `keepSystemAwakeWithActiveSessions`, `settings.json`, Server-only.
- `Browser Ownership`
  `Disable auto-promotion to main browser` — `disableAutoMainBrowserPromotion`, `settings.json`, Next event. Prevents another browser/device from automatically taking over main-browser ownership for terminal size authority.

### Security & Access

- `Authentication`
  `Password status` — auth status, read-only, Immediate.
  `Change Password` — auth endpoint, action, Immediate.
  `Logout` — auth/session endpoint, action, Immediate.
- `API Keys`
  `Name` — API key create request, endpoint-managed, Immediate after create.
  `Create API Key` — API key create endpoint, action, Immediate.
  `API key list` — read-only, Immediate. Shows preview, created-at time, and last-used time.
  `Revoke API Key` — API key delete endpoint, action, Immediate.
- `Process Identity`
  `Terminal User` — `runAsUser`, `settings.json`, Server-only / new sessions.
- `HTTPS Certificate`
  `Fingerprint` — share/certificate API, read-only, Immediate.
  `Valid Until` — share/certificate API, read-only, Immediate.
  `Trust on Other Devices` — `/trust` helper, action, Immediate.
  `Regenerate Certificate` — certificate endpoint, action, Immediate.
- `Windows Firewall`
  `Status / Port / Exposure` — firewall status API, read-only, Immediate.
  `Add Rule` — firewall API, action, Immediate.
  `Remove Rule` — firewall API, action, Immediate.

### Connected Hosts

- `tlbx Hub`
  `Configured Machines` — `hubMachines` mirror plus hub APIs, endpoint-managed, Immediate. Shows remote tlbx machines, session counts, update state, auth mode, trust state, and any current error.
  `Control Updates` — hub update API, action, Immediate.
  `Add Host` — hub machine modal, action, Immediate.
  `Host Modal / URL` — hub machine `baseUrl`, endpoint-managed, Immediate after save.
  `Host Modal / API Key` — hub machine API key, endpoint-managed, New connections.
  `Host Modal / Password` — hub machine password, endpoint-managed, New connections.
  `Host Modal / Save Host` — hub machine upsert API, action, Immediate.
  `Per-machine Create Session` — hub session API, action, Immediate.
  `Per-machine Edit` — hub machine modal, action, Immediate.
  `Per-machine Refresh` — hub refresh API, action, Immediate.
  `Per-machine Pin / Clear Pin` — hub trust pin API, endpoint-managed, Immediate.
  `Per-machine Remove Machine` — hub delete API, endpoint-managed, Immediate.
- `Integrations`
  `Tmux Compatibility` — `tmuxCompatibility`, `settings.json`, Server-only. Injects the tlbx tmux shim for AI tools. Disable it if you are running real tmux inside tlbx sessions, especially over SSH.

Notes:

- `hubMachines` exists in the public settings model but is effectively a read-only mirror.
- Hub configuration is not edited through the normal `/api/settings` flow.
- The current modal exposes URL, API key, and password only. Machine naming is not a first-class field in this surface.

### Advanced & Diagnostics

This remains a mixed surface. It combines browser-local toggles, read-only transport telemetry, and destructive admin actions.

- `Latency`
  `Server RTT / MTHost RTT / Active Session / Output RTT` — diagnostics APIs and live comms, read-only, Immediate.
  `Show overlay on terminal` — latency overlay toggle, `localStorage`, Immediate.
  `Show git debug overlay` — git debug overlay toggle, `localStorage`, Immediate.
  `Terminal buffer dump` — downloads active terminal rendered scrollback, xterm cell color/style runs, and raw PTY output with visible escape bytes, action, Immediate.
- `Terminal Transport`
  `Sequence / backlog / replay / reconnect / data-loss fields` — session state and browser transport snapshot, read-only, Immediate.
- `File Paths`
  `Settings / Secrets / Certificate / Log Directory` — paths API, read-only, Immediate.
  `Reload settings from file` — reload settings API, action, Immediate.
- `Frontend Logging`
  `Logging command help` — static help text, read-only.
- `Terminal Key Log`
  `Enable terminal key log` — diagnostics key-log toggle, `localStorage`, Immediate.
  `Clear log` — diagnostics key log, action, Immediate.
- `Server`
  `Restart Server` — restart API, action, Immediate.

## Agent Controller Session Quick Settings

Agent Controller Session quick settings are a separate settings surface shown only for explicit Agent Controller Sessions. They are not part of the main tabbed Settings page.

| Control | Backing key / source | Persistence | Applies | What it does |
| --- | --- | --- | --- | --- |
| Model | session draft plus `codexDefaultAppServerControlModel` / `claudeDefaultAppServerControlModel` | provider-sticky plus `settings.json` for remembered default model | Next turn | Chooses the model sent with the next Agent Controller Session turn. The dropdown is provider-specific and includes presets plus current custom values. Changing it also updates the stored default model for that provider. |
| Effort | session draft | provider-sticky in `localStorage` | Next turn | Chooses the next-turn reasoning effort (`Default`, `Low`, `Medium`, `High`). |
| Plan | session draft | provider-sticky in `localStorage` | Next turn | Chooses whether the next Agent Controller Session turn starts with plan mode off or on. |
| Permissions | session draft | provider-sticky in `localStorage` | Next turn | Chooses `Manual` vs `Auto` approval mode for the next Agent Controller Session turn. Defaults are seeded from `codexYoloDefault` or `claudeDangerouslySkipPermissionsDefault`. |
| Resume | provider resume picker | action | Immediate | Opens the provider-backed "Resume Conversation" picker for eligible Agent Controller Sessions tied to a space and working directory. |

Notes:

- These controls are hidden for ordinary terminal sessions.
- The quick settings controls lock while an Agent Controller Session turn is running, submitting, or queued.
- Effort/Plan/Permissions are not ordinary global settings rows in the main settings panel.

## Automation Bar Button Editor

The Automation Bar button editor is another settings-like surface outside the main Settings panel. It writes directly to `managerBarButtons`.

| Control | Backing key / source | Persistence | Applies | What it does |
| --- | --- | --- | --- | --- |
| Type | `managerBarButtons[].actionType` | `settings.json` via special writer | Immediate | Chooses whether the button runs a single prompt or a prompt chain. |
| Trigger | `managerBarButtons[].trigger.kind` | `settings.json` via special writer | Immediate | Chooses when the Automation Bar action should run: fire-and-forget, on cooldown, repeat count, repeat interval, or schedule. |
| Repeat Count | trigger detail | `settings.json` via special writer | Immediate | Sets how many times a repeating action should run. |
| Repeat Every value/unit | trigger detail | `settings.json` via special writer | Immediate | Sets the repeat cadence for interval-based actions. |
| Schedule list | trigger detail | `settings.json` via special writer | Immediate | Defines one or more scheduled trigger times. |
| Prompt / Prompts | `managerBarButtons[].prompts` | `settings.json` via special writer | Immediate | Defines the prompt text sent when the action runs. Chain actions can contain multiple prompts. |
| Label (optional) | `managerBarButtons[].label` | `settings.json` via special writer | Immediate | Overrides the button label instead of using the first prompt line. |
| Save action | manager bar modal | action | Immediate | Writes the normalized button list back into `managerBarButtons`. |

## Model-backed Settings Not Exposed As Normal Settings Rows

These fields exist in the settings model but are not currently first-class rows in the main Settings panel.

| Setting | Current owner / surface | Applies | What it does |
| --- | --- | --- | --- |
| `defaultCols` | hidden model-only field | New sessions | Fallback default terminal width used when sizing a new session. No current UI row. |
| `defaultRows` | hidden model-only field | New sessions | Fallback default terminal height used when sizing a new session. No current UI row. |
| `minimumContrastRatio` | hidden model-only field | Immediate | Passed through to xterm's `minimumContrastRatio` option for terminal text contrast. No current UI row. |
| `codexDefaultAppServerControlModel` | Agent Controller Session quick settings `Model` | New Agent Controller Session turns / sessions | Remembers the preferred default Codex model used to seed future Agent Controller Sessions. |
| `claudeDefaultAppServerControlModel` | Agent Controller Session quick settings `Model` | New Agent Controller Session turns / sessions | Remembers the preferred default Claude model used to seed future Agent Controller Sessions. |
| `terminalColorSchemes` | Terminal custom scheme editor | Immediate | Stores the saved custom terminal palette definitions. |
| `managerBarButtons` | Automation Bar button editor | Immediate | Stores the full Automation Bar button definitions and triggers. |
| `devMode` | hidden 7-click toggle on Server version | Immediate / server features | Enables developer-only UI and behaviors. It is not a normal settings row. |
| `backgroundImageFileName` | background image endpoints | Immediate | Read-only wallpaper metadata used by the settings preview and asset cache busting. |
| `backgroundImageRevision` | background image endpoints | Immediate | Read-only revision counter used to force new wallpaper URLs after upload/remove. |
| `runAsUserSid` | server-side derivation from `runAsUser` | Server-only | Read-only SID metadata for the chosen run-as user. |
| `authenticationEnabled` | auth endpoints and security UI status | Server-only | Read-only flag indicating whether auth is enabled. Surfaced indirectly through password/session status. |
| `certificatePath` | installer/certificate setup plus Diagnostics paths | Server-only | Read-only path to the current certificate file. Not editable from Security. |
| `hubMachines` | Hub APIs and Hub tab | Server-only | Read-only mirror of configured Hub machines. Actual edits happen through dedicated Hub endpoints. |

## Reorg Hotspots

- The main Settings panel is cleaner than before, but it still mixes plain preferences with admin actions, status dashboards, and destructive workflows.
- `Advanced & Diagnostics` is still only partly a settings surface; several entries are read-only or action-only.
- Agent Controller Session quick settings and Automation Bar configuration are still real settings surfaces outside the main Settings panel.
- A few real model fields still have no first-class UI row: `defaultCols`, `defaultRows`, and `minimumContrastRatio`.
- Hidden `devMode` still lives behind a version-click easter egg rather than a normal advanced-settings path.
- Several settings-model fields are really endpoint-managed metadata: background image fields, `hubMachines`, `runAsUserSid`, `authenticationEnabled`, and `certificatePath`.
- Related AI defaults are still split between the `AI Agents` tab and provider-specific quick settings shown inside AI sessions.
