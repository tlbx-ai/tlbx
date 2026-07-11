# MidTerm Feature Inventory

This is the canonical feature sweep baseline for the current codebase, with a current delta addendum last reconciled against Git history on 2026-05-25 at `v9.15.22-dev` / `476d75a4`. The coverage order follows the requested path: settings and their rabbit holes, the sidebar and its buttons and drag behavior, the session bar and IDE-adjacent panels, the Command Bay / Automation Bar, the smart input bar, the protocols, and the installer/update system.

## Cluster Map

| Cluster | Range | Count | Focus |
| --- | --- | ---: | --- |
| 1. Platform, Install, Update | `F001-F030` | 30 | Runtime shape, install modes, release/update pipeline |
| 2. Settings and Presentation | `F031-F084` | 54 | Settings UI, appearance, behavior, localization |
| 3. Sidebar, Sessions, History | `F085-F149` | 65 | Session list, update/voice/network sidebars, history, drag/drop |
| 4. Terminal Runtime and Layout | `F150-F202` | 53 | Terminal lifecycle, resize model, search, clipboard, file radar |
| 5. Session Bar, Files, Git, Commands | `F203-F254` | 52 | IDE-adjacent workflow surfaces around each terminal |
| 6. Command Bay / Automation Bar | `F255-F262` | 8 | Quick-action command bar baseline, now superseded by the shared Command Bay surface |
| 7. Smart Input, Voice, Touch, Mobile | `F263-F323` | 61 | Alternative input, voice/chat, mobile affordances, PWA |
| 8. Web Preview and Browser Automation | `F324-F384` | 61 | Reverse-proxied previews, detach/dock, browser control |
| 9. Sharing, Security, Protocols, Diagnostics, Ops | `F385-F432` | 48 | Auth, certs, share links, WebSockets, diagnostics, operations |
| Total | `F001-F432` | 432 | Canonical inventory baseline |

## Feature Inventory

### 1. Platform, Install, Update

- `F001` MidTerm ships as a Native AOT self-contained web server binary.
- `F002` MidTerm uses a separate PTY host binary so each terminal runs outside the web server process.
- `F003` MidTerm targets Windows x64, macOS x64/arm64, and Linux x64/arm64 from the same product model.
- `F004` MidTerm chooses a platform-default shell automatically: `Pwsh` on Windows, `zsh` on macOS, and `bash` on Linux.
- `F005` MidTerm serves the browser UI over HTTPS rather than exposing a raw terminal socket.
- `F006` One browser shell hosts terminals, settings, diagnostics, files, git, commands, and previews together.
- `F007` MidTerm supports user-mode installs that do not require admin or sudo.
- `F008` MidTerm supports system-service installs for always-on remote access.
- `F009` The Windows installer can configure firewall access for the service install.
- `F010` The installers prompt for a required password on first install.
- `F011` Installer updates preserve an existing password by default and allow setting a replacement password during reinstall.
- `F012` Installers can reuse an existing valid local certificate.
- `F013` Installers can trust the current local certificate on the machine, including reused certificates during reinstall.
- `F014` Installers log their actions to `update.log` in the mode-specific log location.
- `F015` Installers detect the current OS and architecture and fetch the matching release asset.
- `F016` Installers support stable and dev release channels.
- `F017` Service installs register MidTerm with the platform service manager.
- `F018` Unix installs integrate with `launchd` on macOS.
- `F019` Unix installs integrate with system service management on Linux.
- `F020` Windows installs separate binary storage from settings storage.
- `F021` Unix installs separate binary, config, and log locations.
- `F022` `version.json` tracks web and PTY component versions as the single version source of truth.
- `F023` Update checks poll GitHub releases rather than a proprietary update backend.
- `F024` Update settings let the user stay on stable builds or opt into dev builds.
- `F025` Dev environments can surface local unreleased builds in addition to GitHub releases.
- `F026` Update logic distinguishes web-only updates from full server-plus-host updates.
- `F027` Web-only updates preserve running terminal sessions.
- `F028` Full updates replace both the server and PTY host binaries.
- `F029` Generated update scripts back up binaries, settings, secrets, and certificates before replacing files.
- `F030` Generated update scripts verify copies, write structured result files, and support rollback paths.

### 2. Settings and Presentation

- `F031` MidTerm exposes an in-browser settings UI split into Updates / Info, Appearance, Behaviour, Agent UI, Security, Multi Server, and Diagnostics tabs.
- `F032` Settings changes sync live across connected browsers over `/ws/settings`.
- `F033` The settings UI shows frontend, server, and host version information.
- `F034` The settings UI shows environment and code-signing status badges.
- `F035` Users can check for updates on demand from settings.
- `F036` Users can open changelog and update-log views from settings.
- `F037` Users can enable or suppress prominent update notifications.
- `F038` Users can switch between stable and dev update channels.
- `F039` The settings UI shows CLI snippets for launching the server manually.
- `F040` The settings UI includes system status and OSS license information.
- `F041` UI language can be set to auto-detect.
- `F042` MidTerm ships an English locale.
- `F043` MidTerm ships a Chinese locale.
- `F044` MidTerm ships a Spanish locale.
- `F045` MidTerm ships a Hindi locale.
- `F046` MidTerm ships a French locale.
- `F047` MidTerm ships a Bengali locale.
- `F048` MidTerm ships a Portuguese locale.
- `F049` MidTerm ships a Russian locale.
- `F050` MidTerm ships a Japanese locale.
- `F051` MidTerm ships a German locale.
- `F052` Appearance settings control the overall UI theme.
- `F053` Appearance settings control the terminal color scheme independently of the UI theme.
- `F054` Appearance settings control terminal font size.
- `F055` Appearance settings control bundled font-family selection.
- `F056` Appearance settings control browser tab title mode.
- `F057` Appearance settings control scrollbar style.
- `F058` Appearance settings control cursor shape.
- `F059` Appearance settings control cursor blink.
- `F060` Appearance settings control the unfocused cursor style.
- `F061` Appearance settings can hide the cursor during input bursts.
- `F062` Appearance settings can raise terminal minimum contrast.
- `F063` Appearance settings support background-image upload.
- `F064` Appearance settings can enable or disable the background image.
- `F065` Appearance settings can remove the background image.
- `F066` Background images always use cover layout and can optionally run a configurable Ken Burns zoom/pan effect.
- `F067` Appearance settings control UI transparency.
- `F068` Behavior settings control the default working directory for new sessions.
- `F069` Behavior settings control scrollback size.
- `F070` Behavior settings toggle WebGL terminal rendering.
- `F071` Behavior settings control bell behavior.
- `F072` Behavior settings enable copy-on-select.
- `F073` Behavior settings enable right-click paste.
- `F074` Behavior settings choose the clipboard shortcut mode.
- `F075` Behavior settings choose Enter-key behavior.
- `F076` Behavior settings toggle smooth scrolling.
- `F077` Behavior settings choose keyboard, smart-input, or both-mode input.
- `F078` Behavior settings toggle scrollback protection.
- `F079` Behavior settings can keep the system awake while sessions exist.
- `F080` Behavior settings toggle File Radar path detection.
- `F081` Behavior settings toggle the manager bar.
- `F082` Behavior settings can auto-show the changelog after a successful update.
- `F083` Behavior settings surface the PWA install flow.
- `F084` Behavior settings toggle tmux compatibility.

### 3. Sidebar, Sessions, History

- `F085` The sidebar can create a new terminal session.
- `F086` The sidebar includes a bookmarks and history launcher.
- `F087` The sidebar includes a settings button.
- `F088` The sidebar shows a security warning when no password is set.
- `F089` The sidebar shows update cards with version information when a release is available.
- `F090` Update notices can be dismissed from the sidebar.
- `F091` Update notices can trigger update or restart actions from the sidebar.
- `F092` Update notices can open the changelog directly.
- `F093` Update notices surface safety hints about web-only versus full updates.
- `F094` The sidebar includes a dedicated voice section.
- `F095` Voice recording can be started from the sidebar.
- `F096` Voice recording can be stopped from the sidebar.
- `F097` The sidebar lets users choose a voice provider.
- `F098` The sidebar lets users choose a microphone device.
- `F099` The sidebar lets users adjust voice playback speed.
- `F100` The sidebar can open the chat panel.
- `F101` Dev mode adds a voice sync and test control.
- `F102` The sidebar includes a dedicated network and remote access section.
- `F103` The network section lists detected network endpoints.
- `F104` The network section links to certificate trust help.
- `F105` The sidebar provides a Share Access action that builds a connection-share email.
- `F106` The sidebar footer shows WebSocket throughput.
- `F107` The sidebar footer shows the current version.
- `F108` The sidebar footer can surface subtle update hints even when large notices are hidden.
- `F109` The sidebar can collapse to icon-only mode on desktop.
- `F110` The sidebar can slide open and closed on mobile.
- `F111` Sidebar width can be resized with pointer input.
- `F112` Sidebar width persistence is stored in cookies.
- `F113` Sidebar collapsed state persistence is stored in cookies.
- `F114` The network section remembers its collapsed state.
- `F115` The voice section remembers its collapsed state.
- `F116` The session list can show a custom session name.
- `F117` The session list can fall back to the terminal title when no custom name exists.
- `F118` The session list can fall back to foreground cwd or process information when no title exists.
- `F119` The session list suppresses redundant shell-process labels.
- `F120` Layout-member sessions show split-layout badges in the session list.
- `F121` Named sessions can show process and cwd detail rows.
- `F122` Pending session creation is shown with a spinner state.
- `F123` Each session can render an activity heat strip in the sidebar.
- `F124` Sessions can be pinned into bookmarks and quick-launch entries.
- `F125` Sessions support inline rename actions from the sidebar.
- `F126` Sessions support inject-guidance actions from the sidebar.
- `F127` Sessions support close actions from the sidebar.
- `F128` Sessions support undock-from-layout actions from the sidebar.
- `F129` Layout sessions are kept contiguous in layout-tree order inside the session list.
- `F130` The session list supports a mobile-title and collapsed-title presentation.
- `F131` Clicking a session selects it and closes the mobile sidebar.
- `F132` Desktop users can drag sessions to reorder the sidebar.
- `F133` Touch users can drag sessions to reorder the sidebar.
- `F134` Session dragging renders a visual drag ghost.
- `F135` Dragging a session into the terminal area opens a dock overlay.
- `F136` Sessions can be docked into layout splits by drag and drop.
- `F137` Session reorder persists back to the server.
- `F138` Session drag suppresses terminal file-drop overlays while a move is in progress.
- `F139` Tmux child sessions render with distinct styling.
- `F140` Tmux child sessions are excluded from normal drag behavior.
- `F141` The sidebar opens a history and bookmarks dropdown.
- `F142` The history dropdown separates pinned entries from recent entries.
- `F143` History entries can launch a new session from saved shell context.
- `F144` History entries support inline rename.
- `F145` History entries support delete.
- `F146` Pinned history entries support mouse drag reorder.
- `F147` Pinned history entries support touch drag reorder.
- `F148` The history dropdown repositions itself on resize and orientation changes.
- `F149` The history dropdown closes on outside click.

### 4. Terminal Runtime and Layout

- `F150` MidTerm maintains one xterm.js terminal instance per session.
- `F151` Terminal presentation is derived from live settings.
- `F152` Terminals preload the configured font before measurement.
- `F153` Hidden calibration terminals compute accurate cell sizes.
- `F154` New sessions are sized to the creating viewport.
- `F155` Existing sessions keep their server-side size until the user explicitly resizes them.
- `F156` Secondary clients CSS-scale terminals instead of resizing them.
- `F157` CSS-scaled terminals stay visually centered in their containers.
- `F158` Main-browser coordination selects which client can auto-fit sessions.
- `F159` Manual fit-to-screen sends an explicit resize command.
- `F160` Layout panes can split to the left.
- `F161` Layout panes can split to the right.
- `F162` Layout panes can split upward.
- `F163` Layout panes can split downward.
- `F164` Sessions can be undocked back out of a layout.
- `F165` Layout sessions can be swapped.
- `F166` Focus can move between sessions inside a split layout.
- `F167` Layout trees persist in backend layout state.
- `F168` Layout restore reattaches sessions without auto-resizing them.
- `F169` Resize observers keep terminal presentation aligned with container changes.
- `F170` Visual viewport listeners adapt the UI to mobile keyboards and viewport changes.
- `F171` Terminals can recover from WebGL context loss or context pressure.
- `F172` Web links in terminal output are clickable.
- `F173` Unicode 11 support improves glyph coverage in terminals.
- `F174` Terminal search supports next-match navigation.
- `F175` Terminal search supports previous-match navigation.
- `F176` Terminal search shows result counts.
- `F177` Terminal search supports Enter for forward search.
- `F178` Terminal search supports Shift+Enter for reverse search.
- `F179` Terminal search closes on Escape.
- `F180` Global focus reclaim returns keyboard focus to the active terminal after stray clicks.
- `F181` Terminal titles are pushed back to the server with debounce.
- `F182` Terminals can scroll to the bottom programmatically.
- `F183` Buffer refresh utilities support resync and replay flows.
- `F184` Terminal bells can trigger UI callbacks.
- `F185` OSC52 clipboard writes from terminal applications are supported.
- `F186` Clipboard shortcuts adapt to platform mode.
- `F187` Right-click paste can be enabled or disabled.
- `F188` MidTerm intercepts paste in insecure browser contexts when possible.
- `F189` `Alt+V` can paste clipboard images into a session.
- `F190` Drag-and-drop can upload files into terminals.
- `F191` Terminal drop targets show transfer overlays.
- `F192` Unsupported binary, document, and archive file types are rejected from inline paste flows.
- `F193` Pasted and copied text is sanitized before terminal injection.
- `F194` Image pastes can go through a dedicated clipboard-image endpoint.
- `F195` File Radar parses terminal output for file paths.
- `F196` File Radar uses a per-session allowlist boundary.
- `F197` Clicking detected files can open them in MidTerm viewers.
- `F198` Scrollback protection can mitigate runaway redraw behavior.
- `F199` Input mode can divert typing away from the terminal and into Smart Input.
- `F200` Multi-client state tracks the active session and sends active-session hints to the mux.
- `F201` Session byte tracking powers heat indicators and mobile PiP.
- `F202` Data-loss warnings surface when background buffering overflows.

### 5. Session Bar, Files, Git, Commands

- `F203` Each session gets its own wrapper and tab bar.
- `F204` The session bar exposes Terminal and Files tabs, and can surface an experimental Agent Controller Session tab for supported agent sessions while Agent Controller Session remains dev-gated.
- `F205` The session bar shows the foreground cwd.
- `F206` Clicking the cwd copies it to the clipboard.
- `F207` The session bar includes a web preview action.
- `F208` The session bar includes a commands action.
- `F209` The session bar includes a session-share action.
- `F210` The session bar includes a git action.
- `F211` Session-bar actions can light up when their docks are active.
- `F212` Git indicator badges show the current branch name.
- `F213` Git indicator badges show ahead and behind state.
- `F214` Git indicator badges show aggregate change counts.
- `F215` Git indicator badges show total added and deleted lines.
- `F216` Tab switching reparents the terminal container without recreating it.
- `F217` Returning to the terminal tab restores terminal focus.
- `F218` Files tabs can render a session-scoped file browser.
- `F219` File trees lazy-load directories.
- `F220` File trees sort directories before files.
- `F221` File trees sort entries alphabetically.
- `F222` File trees can show git-state badges.
- `F223` File trees can show file sizes.
- `F224` The file browser roots itself in the session foreground cwd.
- `F225` File previews can render images.
- `F226` File previews can render video.
- `F227` File previews can render audio.
- `F228` File previews can render text.
- `F229` File previews can render binary content with formatted dumps.
- `F230` Markdown files can open directly into editor mode.
- `F231` Text previews support syntax highlighting.
- `F232` Text previews support inline editing.
- `F233` Text previews support explicit save buttons.
- `F234` Text previews support `Ctrl+S` and `Cmd+S` saving.
- `F235` File saves go through the file-save API.
- `F236` Git panels detect when the cwd is not a repository.
- `F237` Git panels summarize repo root and current branch.
- `F238` Git panels show pills for conflicts, changes, sync state, stash, and clean state.
- `F239` Git panels group files into conflicts.
- `F240` Git panels group files into staged changes.
- `F241` Git panels group files into unstaged changes.
- `F242` Git panels group files into untracked files.
- `F243` Git panels build hierarchical path trees for each file section.
- `F244` Git panels show recent commits.
- `F245` Git panels can open dock-native diff inspectors for tracked files.
- `F245a` Git panels can inspect recent commits with structured patch details.
- `F245b` Git panels can suggest terminal git commands for explicit write handoff.
- `F246` Commands panels list per-session scripts.
- `F247` Commands panels show empty-state guidance when no scripts exist.
- `F248` Commands panels can create new scripts inline.
- `F249` Commands panels can edit existing scripts inline.
- `F250` Commands panels can delete scripts with confirmation.
- `F251` Commands panels can run scripts.
- `F252` Commands panels can stop running scripts.
- `F253` Running scripts stream into hidden-session output overlays.
- `F254` Commands state maps visible scripts to their hidden execution sessions.

### 6. Command Bay / Automation Bar Baseline

- `F255` The Automation Bar can be enabled or hidden by setting.
- `F256` The Automation Bar renders configurable quick-action buttons.
- `F257` Clicking an Automation Bar button sends text to the active terminal.
- `F258` Automation Bar execution can automatically send Enter after the text.
- `F259` Automation Bar buttons can be added inline from the UI.
- `F260` Automation Bar buttons can be renamed inline from the UI.
- `F261` Automation Bar buttons can be deleted from the UI.
- `F262` Automation Bar buttons are mirrored into the mobile actions menu.

### 7. Smart Input, Voice, Touch, Mobile

- `F263` Smart Input can fully replace direct terminal keyboard focus, and Agent Controller Session reuses that same docked infrastructure as its in-conversation composer lane.
- `F264` Smart Input can coexist with direct terminal keyboard focus in both mode, but Agent Controller Session keeps a single composer path so conversation turns do not split across two inputs.
- `F264a` Terminal and Agent Controller Session now share one adaptive active-session footer dock instead of separate smart-input and manager bars.
- `F265` Smart Input keeps a per-session draft buffer.
- `F266` Switching active sessions preserves and restores Smart Input drafts.
- `F267` Smart Input auto-grows its textarea up to one base line plus seven upward overlay lines, then falls back to internal scrolling.
- `F267a` The footer dock reserves only its collapsed height, so multiline input growth expands upward over the pane instead of shrinking the active viewport.
- `F268` Smart Input sends on Enter.
- `F269` Smart Input inserts newlines on `Shift+Enter`.
- `F270` Smart Input includes an explicit Send button.
- `F271` Smart Input supports an auto-send toggle for transcribed input.
- `F272` Auto-send preference persists in `localStorage`.
- `F273` Right Ctrl acts as push-to-talk when the experimental Smart Input voice path is available.
- `F274` The mic button supports hold-to-record with pointer events, but only appears when dev mode and the voice credential path expose that workflow.
- `F275` Smart Input can attach multiple files.
- `F276` Smart Input can capture touch-device photos via camera input.
- `F277` Smart Input can capture desktop images from a webcam overlay.
- `F278` Smart Input can paste submitted text into the terminal and send Enter after a delay.
- `F279` Smart Input can embed the touch controller as a second row.
- `F280` The embedded touch-controller row can be expanded or collapsed.
- `F281` Touch-controller expansion state persists in `localStorage`.
- `F281a` Mobile Agent Controller Session surfaces keep media actions explicit in the dock while mobile Terminal keeps terminal control buttons in the dock context row instead of on desktop.
- `F282` Smart Input state is removed when a session closes.
- `F283` Voice support checks MidTerm.Voice availability before enabling controls.
- `F284` Voice support can populate provider and voice dropdowns from server health data.
- `F285` Voice support can enumerate microphones without prompting when permission already exists.
- `F286` Voice support can request microphone permission interactively when recording starts.
- `F287` Voice sessions connect over a dedicated voice WebSocket.
- `F288` Voice sessions stream recorded audio frames to the server.
- `F289` Voice sessions can play server audio responses.
- `F290` Voice sessions show status text in the sidebar.
- `F291` Voice sessions open the chat panel automatically when a session starts.
- `F292` Voice tools can request confirmation before acting.
- `F293` Voice tool output is grouped into collapsible chat bubbles.
- `F294` Chat panel visibility persists in `localStorage`.
- `F295` The touch controller exposes arrow keys for mobile terminal control.
- `F296` The touch controller exposes modifier keys such as Ctrl, Alt, and Shift.
- `F297` The touch controller exposes extra special keys for terminal workflows.
- `F298` The touch controller supports long-press alternates.
- `F299` The touch controller can be dismissed and restored.
- `F300` The touch controller adapts visibility based on touch context.
- `F301` The touch stack includes dedicated gesture and event handling for the terminal area.
- `F302` The mobile top bar exposes a hamburger and sidebar toggle.
- `F303` Mobile actions can create a new terminal.
- `F304` Mobile actions can show the touch bar.
- `F305` Mobile actions can enter fullscreen.
- `F306` Mobile actions can send `Ctrl+C` to the active session.
- `F307` Mobile actions can paste into the active session.
- `F308` Mobile actions can rename the active session.
- `F309` Mobile actions can inject MidTerm guidance into the active session.
- `F310` Mobile actions can close the active session.
- `F311` Mobile actions can switch between Terminal and Files views.
- `F312` Mobile actions can open web preview.
- `F313` Mobile actions can open commands.
- `F314` Mobile actions can open share.
- `F315` Mobile actions can open git.
- `F316` MidTerm can request Document Picture-in-Picture on mobile or PWA backgrounding.
- `F317` Mobile PiP shows a miniature preview of the active terminal.
- `F318` Mobile PiP shows the active session title.
- `F319` Mobile PiP shows live output-rate information.
- `F320` Mobile PiP flashes when recent activity cools down.
- `F321` MidTerm ships a web manifest for PWA installation.
- `F322` MidTerm can request browser notification permission for UI notifications.
- `F323` The settings UI includes an install-as-app flow for PWA-capable browsers.

### 8. Web Preview and Browser Automation

- `F324` Web preview is session-scoped rather than global.
- `F325` Web preview supports multiple named previews per session.
- `F326` Each preview tracks its own target URL.
- `F327` Each preview tracks its own cookie jar.
- `F328` Each preview tracks its own proxy log.
- `F329` Each preview tracks its own detached-window state.
- `F330` Previews can be hidden.
- `F331` Previews can be docked inside the main UI.
- `F332` Previews can be detached into a separate chromeless window.
- `F333` Detached previews can dock back into the main UI.
- `F334` Docked preview width is resizable.
- `F335` Dock width persistence is stored per browser client.
- `F336` Preview docks coexist with files, git, and commands panels by adjusting inner margins.
- `F337` The web dock exposes a URL bar.
- `F338` URL entry normalizes localhost and protocol handling.
- `F339` The web dock can refresh the active preview.
- `F340` The web dock supports reload flows after URL or mode changes.
- `F341` The web dock can clear preview cookies.
- `F342` The web dock can capture screenshots.
- `F343` The web dock can send an agent hint into the active session.
- `F344` The web dock supports viewport size overrides.
- `F345` The web dock can reset viewport overrides.
- `F346` The web dock renders tabs for named previews.
- `F347` Preview tabs distinguish empty, docked, and detached states.
- `F348` Preview selection follows the active session.
- `F349` Preview state is re-synced from the server when sessions change.
- `F350` Proxy routes are keyed by per-preview route identifiers.
- `F351` HTML responses inject a base tag for proxied routing.
- `F352` The injected runtime rewrites `fetch` requests to stay inside the preview proxy.
- `F353` The injected runtime rewrites XHR requests to stay inside the preview proxy.
- `F354` The injected runtime rewrites WebSocket requests to stay inside the preview proxy.
- `F355` The injected runtime rewrites `EventSource` requests to stay inside the preview proxy.
- `F356` The injected runtime rewrites DOM `src`, `href`, and `action` writes.
- `F357` The injected runtime rewrites `history.pushState` and `history.replaceState`.
- `F358` The injected runtime reports navigation changes back to the parent UI.
- `F359` Preview WebSocket traffic is relayed without content rewriting.
- `F360` Preview cookies bridge between the browser frame and the server-side cookie container.
- `F361` `HttpOnly` preview cookies stay server-side.
- `F362` Sandboxed previews get safe `localStorage` and `sessionStorage` fallbacks when needed.
- `F363` Sandboxed previews get a no-op service-worker container when needed.
- `F364` Dev mode can move previews onto a dedicated secondary origin.
- `F365` Preview CSP allows the isolated preview host when needed.
- `F366` Self-preview of MidTerm is supported when the dedicated preview origin is active.
- `F367` Browser preview snapshots can be saved into `.midterm/snapshot_*`.
- `F368` Proxy logs record request, response, cookie, WebSocket, and error details.
- `F369` Browser automation registers preview-scoped browser clients.
- `F370` Browser automation rejects duplicate bridge clients for the same preview identity.
- `F371` Browser automation can open a preview from CLI or API calls.
- `F372` Browser automation can dock a preview from CLI or API calls.
- `F373` Browser automation can detach a preview from CLI or API calls.
- `F374` Browser automation can set preview viewport from CLI or API calls.
- `F375` Browser automation can query DOM state.
- `F376` Browser automation can click elements.
- `F377` Browser automation can fill form fields.
- `F378` Browser automation can execute JavaScript in the preview.
- `F379` Browser automation can wait for conditions.
- `F380` Browser automation can capture processed screenshots.
- `F381` Browser automation can capture raw screenshots.
- `F382` Browser automation can emit DOM outlines.
- `F383` Browser automation can read element attributes or CSS.
- `F384` Browser automation can list links, list forms, show logs, and submit forms.

### 9. Sharing, Security, Protocols, Diagnostics, Ops

- `F385` Password authentication can be enabled for browser access.
- `F386` Password hashes use PBKDF2-SHA256 with fixed-time verification.
- `F387` Session cookies use signed HMAC tokens.
- `F388` Login failures are rate-limited.
- `F389` Password changes invalidate existing sessions.
- `F390` Secrets are stored outside the public settings model.
- `F391` Windows secret storage uses DPAPI-backed `secrets.bin`.
- `F392` macOS user-mode secret storage can use Keychain.
- `F393` Unix secret storage uses file-backed secrets with restricted permissions.
- `F394` MidTerm can generate and manage a local TLS certificate.
- `F395` Certificate generation includes `localhost`, hostname, and discovered IP SANs.
- `F396` The certificate UI shows fingerprint and validity information.
- `F397` Certificate tools can download PEM output.
- `F398` Certificate tools can download Apple `mobileconfig` trust profiles.
- `F399` The trust page guides device-specific certificate installation.
- `F400` The UI can show Windows firewall status.
- `F401` The UI can add Windows firewall rules.
- `F402` The UI can remove Windows firewall rules.
- `F403` Service mode can spawn terminals as a selected OS user.
- `F404` Security settings can mint named API keys.
- `F405` API keys are masked after creation and in later listings.
- `F406` API keys can be revoked from the UI.
- `F407` Session sharing can create scoped share grants for one terminal.
- `F408` Shared-session links exchange a URL secret for a scoped cookie.
- `F409` Shared-session mode hides the normal sidebar, settings, and manager surfaces.
- `F410` Shared-session bootstrap ships reduced settings and expiry data.
- `F411` Share grants can expire automatically.
- `F412` Share grants can be read-only or writable depending on mode.
- `F413` `/ws/mux` carries multiplexed binary terminal I/O.
- `F414` `/ws/state` pushes session-list and update state.
- `F415` `/ws/settings` synchronizes settings changes live.
- `F416` Mux buffering prioritizes the active session over background sessions.
- `F417` Background session output can be gzip-compressed before delivery.
- `F418` Mux resync frames recover clients after queue overflow.
- `F419` Foreground-process change frames keep titles and cwd information fresh.
- `F420` Data-loss frames surface dropped background output to the UI.
- `F421` Diagnostics can measure server round-trip time.
- `F422` Diagnostics can measure `mthost` round-trip time.
- `F423` Diagnostics can track output latency from typing to render.
- `F424` Diagnostics can show a latency overlay.
- `F425` Diagnostics can show a git debug overlay.
- `F426` Diagnostics can surface settings, secrets, certificate, and log paths.
- `F427` Diagnostics can reload settings from disk.
- `F428` Diagnostics can restart the server from the UI.
- `F429` Diagnostics expose frontend logging helpers through `mtlog.*`.
- `F430` The bootstrap API consolidates startup state into a single fetch.
- `F431` Log APIs can list, read, and tail server logs.
- `F432` Power APIs can restart or shut down the server process.

## Documentation Plan

## Current Delta Since Baseline

The feature inventory above remains the numbered baseline. The following current deltas should be folded into the next full renumbered sweep:

- `D001` Agent Controller Session is now a concrete product surface for explicit provider-backed agent work, not just an experimental tab label.
- `D002` Agent Controller Session uses a dedicated `/ws/app-server-control` synchronization path for attach, history windows, turn submission, interrupts, approvals, and user-input answers.
- `D003` MidTerm owns canonical reduced Agent Controller history and transports bounded windows instead of asking browsers to retain full provider event streams.
- `D004` Codex app-server protocol work and Codex model-selection fixes make Codex Agent Controller operation part of the current product truth.
- `D005` Grok Build launcher/controller support and Grok ACP protocol updates exist in the current dev line.
- `D006` The Command Bay queue can schedule delayed prompts with `delayMs` / `runAt` and expose queue visibility/cancellation.
- `D007` Generated helpers include `mt_wake` and `mt_wake_cancel` for CLI-driven scheduled prompts.
- `D008` Terminal reconnect hardening keeps background sessions warm and uses viewport-sized replay hints on reconnect/buffer refresh.
- `D009` Hidden/background terminal output deferral prevents non-visible flood output from being written into xterm until the session becomes streamable.
- `D010` Windows tmux compatibility includes a `tmux.exe` shim and probe responses for clients that require tmux-like capability checks.
- `D011` Terminal text brightness boost blends foreground/ANSI colors toward white without boosting backgrounds, selection, cursor, or scrollbar backgrounds.
- `D012` Dev Browser proxying injects a MidTerm-scoped base href for Blazor Server apps so proxied app routes render correctly.
- `D013` Dev Browser chrome is now tab/URL-bar centered, with screenshot on the active tab URL bar and utilities in overflow.
- `D014` Dev Browser responsive-frame mode constrains the preview without claiming that a desktop iframe is a mobile browser.
- `D015` Dev Browser soft-keyboard simulation reserves page layout space instead of overlaying the app under test.
- `D016` Mobile sidebar drawer readability and touch action placement have been hardened for transparent UI settings.
- `D017` Multi-repo Git monitoring can show session-scoped extra repos in addition to the cwd repo.
- `D018` Keyed sidebar DOM reconciliation preserves node identity during hot session-list updates.
- `D019` The remote-first Mobile Device Lab ships an explicit-activation Chrome extension that opens a local top-level Pixel 8 CDP target with touch, Android UA/Client Hints, rotation, keyboard-viewport, lifecycle, screenshots, and existing MidTerm DOM automation.
- `D020` The sidebar History entry now combines launch history with deterministic Terminal input history; `Alt+H` opens the input side directly.
- `D021` Server-owned input history records only exact MidTerm-handled prompts, text pastes, clipboard images, file drops, and uploads; it never reconstructs prompts from PTY output.
- `D022` Input history is bounded, atomically persisted, thumbnail-capable, replayable into another session, and available through generated `mt_input_history` helpers.
- `D023` The explicit agent control plane stores bounded work items, published session status, and checkpoints with timestamps, revision, source, project, repository, and session provenance.
- `D024` Operator presents exact process facts separately from agent-published meaning and aggregates trusted Hub-machine control planes through the existing authenticated proxy.
- `D025` Generated `mt_work_*`, `mt_publish_status`, `mt_checkpoint`, and `mt_control_plane` helpers make every control-plane record readable and writable as JSON without MidTerm-owned intelligence.
- `D026` `mt_agent_capabilities` reports product-authored feature flags and exact per-session runtime modes without process-name or transcript heuristics.
- `D027` `mt_dispatch` fans a turn directly to at most 32 explicit, deduplicated session IDs and returns an independent result per target without heat-based queue decisions.
- `D028` The bounded `mt_events` feed and Operator badge/notifications derive only from explicit control-plane mutations and use sequence cursors instead of terminal-text inference.

### README.md

- Lead with the real product shape: browser terminal workspace, not just “a terminal in a browser”.
- Summarize MidTerm by clusters instead of isolated bullets: terminal workspace, side panels, mobile input, browser preview, remote access, security, and operations.
- Explicitly mention the features that change how people evaluate the product: split layouts, files and git panels, commands runner, web preview, smart input, voice support, touch controls, session sharing, and update channels.
- Keep installation short, but explain the difference between user installs and service installs because that affects remote usage and auto-start behavior.
- Link to `docs/FEATURES.md` for the exhaustive inventory and `docs/ARCHITECTURE.md` for implementation depth.

### docs/ARCHITECTURE.md

- Describe the runtime as multiple cooperating subsystems: `mt`, per-session `mthost`, the browser frontend, the three WebSockets, and the preview/browser bridge.
- Add the terminal resize design principle explicitly: existing sessions are never auto-resized by reconnects or secondary clients.
- Add the major frontend surfaces that sit around the terminal: sidebar, layout engine, session wrappers, files, git, commands, manager bar, smart input, touch/mobile shell, and diagnostics.
- Add a first-class section for web preview and browser automation because it is a substantial proxy and bridge subsystem, not a minor panel.
- Add a first-class section for settings synchronization, secret storage, local certificate handling, shared-session links, and installer/update script generation.

### Canonical Split

- `docs/FEATURES.md` should remain the exhaustive feature inventory and discovery checklist.
- `README.md` should stay product- and workflow-oriented, using feature clusters and deep links rather than trying to carry 400-plus details inline.
- `docs/ARCHITECTURE.md` should stay implementation-oriented, explaining subsystem boundaries, data flow, storage, protocols, and operational guarantees behind the user-facing features.
