# Web Preview Dev Browser — Route-Keyed Proxy Design

The web preview reverse proxy (`/webpreview/{routeKey}/*`) intercepts browser requests and forwards them to an upstream target that belongs to one named preview session under one tlbx terminal session. HTTP requests are straightforward (strip prefix, forward, return response). WebSocket connections are relayed without content modification.

Targets may also be local `file:///...` URLs. tlbx only accepts `file:` URLs that resolve to the same machine as the running `mt` process. Remote file-share targets such as `file://server/share/...` remain blocked.

## URL Space Design

The proxy uses a **write-only interception** strategy. Each preview gets its own route prefix (`/webpreview/{routeKey}`), and the injected `UrlRewriteScript` patches outgoing APIs to add that prefix to URLs before they leave JavaScript:

- `fetch`, `XMLHttpRequest.open` — HTTP requests
- `WebSocket`, `EventSource` — connection constructors
- `history.pushState`, `history.replaceState` — navigation
- `location.assign`, `location.replace` — redirects
- Element `.src`, `.href`, `.action` setters — DOM properties
- `setAttribute` — attribute writes

For `fetch(Request)` calls, the injected shim now rebuilds the request from the original method/headers/body instead of relying on `new Request(rewrittenUrl, request)`. In Chromium, rewriting a `Request` URL that way can drop or corrupt non-`GET` bodies, which breaks generated API clients that send JSON via `fetch(new Request(...))`.

Read-side APIs (`location.href`, `location.pathname`, `document.URL`, `document.baseURI`) are **not spoofed**. The page sees its real URL including `/webpreview/{routeKey}/`.

| Layer | URL the code sees | Example |
|-------|-------------------|---------|
| **Browser** | `https://proxy:2000/webpreview/{routeKey}/page` | Real browser URL |
| **JavaScript** | `https://proxy:2000/webpreview/{routeKey}/page` | `location.pathname` = `/webpreview/{routeKey}/page` |
| **Upstream** | `https://upstream.example.com/page` | What the real server knows |

The `<base href="/webpreview/{routeKey}/">` tag is injected into every HTML response, so:
- `document.baseURI` = `https://proxy:2000/webpreview/{routeKey}/` (from `<base>` tag)
- `location.href` = `https://proxy:2000/webpreview/{routeKey}/page` (real browser URL)
- Both are consistent — frameworks see the app mounted at `/webpreview/{routeKey}/`

For local file previews there is no upstream HTTP origin. tlbx serves the requested file directly from disk, still injects the proxy `<base>` tag plus runtime rewrite script, and keeps all subsequent asset requests inside `/webpreview/{routeKey}/...`.

### Navigation Notifications

Each docked or detached preview now gets a registered preview identity (`sessionId`, `previewName`, `routeKey`, `previewId`, `previewToken`) from `POST /api/browser/preview-client`. The parent writes that identity into `iframe.name` before loading the proxied page, and the injected script uses it for all bridge traffic.

The injected script sends `postMessage({type: "mt-navigation", url: location.href, upstreamUrl: ..., targetOrigin: window.__mtTargetOrigin, previewId, previewToken})` to the parent window whenever in-iframe navigation occurs:

- `history.pushState` / `history.replaceState` — SPA navigation
- `popstate` / `hashchange` events — back/forward navigation
- Initial page load (`setTimeout(ntfyNow, 0)`) — captures redirects

Navigation notifications are now coalesced and deduplicated inside the injected runtime before they reach the parent shell. A preview that churns `history.replaceState` without actually changing its effective URL can no longer flood the owning tlbx tab with redundant `postMessage` traffic.

The parent `webPanel.ts` / detached popup listener accepts these messages only when the preview identity matches the current iframe. It prefers the injected `upstreamUrl` field, so redirects and `_ext` navigations no longer need to be reverse-engineered from the iframe URL bar.

### Why No Read-Side Spoofing?

Chrome's `Location.prototype` properties have `configurable: false`. `Object.defineProperty(location, "href", ...)` silently fails. But `document.baseURI` and `document.URL` *can* be overridden. This inconsistency is fatal for frameworks like Blazor that compare `location.href` against `document.baseURI` — they see mismatched URL spaces and fail to route.

## WebSocket Relay (No Content Rewriting)

WebSocket messages are relayed **untouched** between client and upstream. No URL rewriting, no binary manipulation, no protocol-specific handling.

This works because:
- **Frameworks use relative paths for routing.** Blazor's `NavigationManager` computes routes as `currentUri` minus `baseUri`. If both are proxy URLs (`https://proxy:2000/webpreview/{routeKey}/...`), the relative path is identical to what it would be with upstream URLs.
- **Server state comes from the client.** Blazor's `StartCircuit` receives `baseUri` and `currentUri` from the client. The server stores these and uses them for all subsequent URL operations. Since the client sends proxy URLs, the server's `NavigationManager` operates in the proxy URL space.
- **Server echoes client-provided URLs.** When the server sends URLs back (e.g., `OnLocationChanged`), they're already proxy URLs. No rewriting needed.
- **No message corruption risk.** Previous approaches rewrote URL strings inside JSON and MessagePack binary frames, which required: text `string.Replace`, MessagePack string header adjustment, SignalR VarInt length prefix re-encoding. Each layer was a source of bugs.

### What About Server-Generated URLs?

If the upstream server independently generates URLs using its own origin (not from client state), those URLs would point to the upstream directly. The client's `fetch`/`XHR` interceptors would route them through the `/_ext` external proxy. This is functional, though slightly less efficient than direct `/webpreview/{routeKey}/` routing.

In practice, Blazor and most SPA frameworks derive all URLs from client-provided state, so this edge case rarely occurs.

## Cookie Bridge

Upstream cookies are stored in tlbx's server-side `CookieContainer`. The browser bridge under `/webpreview/{routeKey}/_cookies` intentionally exposes only **script-visible** cookies:

- `HttpOnly` cookies stay server-only and are still forwarded upstream on HTTP/WebSocket requests
- `document.cookie` inside the proxied page sees only non-`HttpOnly` cookies
- `document.cookie = ...` writes also behave like a browser: `HttpOnly` is ignored on writes from page JavaScript

The proxied page no longer calls `/webpreview/{routeKey}/_cookies` directly. Instead, the injected script posts `mt-cookie-request` messages to its parent window, and the parent performs the authenticated fetch on the page's behalf. This removes the last iframe dependency on `contentWindow`/same-origin access and keeps the cookie bridge working once the iframe is sandboxed.

The bridge resolves cookies against the current upstream page URL either from the explicit `?u=` query parameter supplied by the parent or, as a fallback, the iframe referer.

The injected runtime now refreshes its `document.cookie` cache after proxied `fetch`, `XMLHttpRequest`, and `sendBeacon` calls settle. The server-side cookie jar remains the source of truth; the refresh path just re-reads the filtered bridge for the current document URL so script-visible cookies stay in sync after upstream responses mutate the jar.

## Sandboxed Runtime Compatibility

When the preview iframe is sandboxed without a usable same-origin storage context, the injected runtime now provides safe compatibility fallbacks before any upstream JavaScript runs:

- `localStorage` and `sessionStorage` fall back to per-frame in-memory stores instead of throwing `SecurityError`
- `navigator.serviceWorker` falls back to a no-op container that resolves registration calls without taking over the real page scope

These shims exist specifically so tlbx-in-tlbx and similar apps can still bootstrap inside an opaque-origin sandbox. They do **not** provide persistence across reloads, and they are intentionally weaker than a real same-origin browser context.

## Browser Bridge Targeting

Browser automation is now scoped per named preview session instead of "whichever iframe connected last":

- `/ws/browser` accepts preview-scoped connections with `previewId` / `token`
- auth middleware lets valid preview-token `/ws/browser` upgrades through before normal browser-session auth, so isolated preview-origin bridge connections do not get trapped behind `mm-session`
- `BrowserCommandService` keeps one command listener per connected preview client
- only one browser bridge connection is accepted per preview id; later duplicates are rejected
- the shell now exposes `MT_SESSION_ID` automatically; `mt_session` prints it, `mt_preview [name]` switches the current named preview, and `mt_previews` lists the preview set for the current terminal
- browser commands without explicit flags default to `--session $MT_SESSION_ID --preview $MT_PREVIEW_NAME`
- commands with `--session` and `--preview` route only to that named preview
- `mt_status` / `/api/browser/status` now report the explicitly targeted preview/session scope instead of only the global default browser client
- docked UI screenshot capture sends the active docked `previewId`, so sibling previews under the same terminal session do not collide
- the tlbx UI shows one tab per named preview under the active terminal session; each tab keeps its own target URL, cookies, proxy log, and detached popup state

The injected browser bridge now connects immediately from the server-injected head script, before upstream page scripts run. This lets tlbx claim the preview's browser-control channel before page JavaScript can open its own `/ws/browser` socket. The injected screenshot command also loads `html2canvas` via a blob URL created from the native fetch response, so proxy URL rewriting no longer breaks `mtbrowser screenshot`.

Browser UI instructions (`open`, `dock`, `detach`, `viewport`, `mobile-device`) are now targeted to a registered `/ws/state` UI listener instead of being fire-and-forget broadcasts. If no tlbx browser UI is connected, the API returns a helpful `409` error instead of silently succeeding.

The responsive mobile-frame toggle is deliberately dimension-only. Full Chromium mobile signals are owned by the optional local Chrome extension described in [MOBILE_DEVICE_LAB.md](MOBILE_DEVICE_LAB.md). Its top-level target registers a separate preview identity and therefore participates in the same preview ownership and browser-command selection rules as docked and detached clients.

Preview control ownership is now backend-owned per `(sessionId, previewName)` instead of being inferred only from focus/visibility heuristics:

- the first browser that creates or bootstraps a named preview becomes that preview's control owner
- browser commands and browser-UI instructions for that named preview route to the owned browser first
- if the owner disappears and exactly one other browser remains attached for that preview, tlbx reassigns ownership to that sole remaining browser deterministically
- if the owner disappears and the explicit leading browser is attached, tlbx reassigns ownership to the leading browser deterministically
- if the owner disappears and multiple different non-leading browsers remain attached, the preview stays non-controllable instead of silently picking one by focus or recency
- presentation state such as docked vs detached mode, viewport size, and scroll position remains browser-local and is not replicated globally

Agents can explicitly recover from stale preview ownership with `mt_claim_preview` or `mt_open --claim <url>`. Normal `mt_open <url>` also reclaims a stale owner to the attached leading browser and activates the target session before it docks the preview, so two connected tlbx tabs or an inactive source session cannot leave CLI browser automation stranded on an offline tab. The claim path assigns the selected `(sessionId, previewName)` to the connected leading tlbx browser UI listener, then normal `open`, `reload`, and browser-command routing use that owner. `mt_status` now includes a compact `bridge phase` field such as `no-ui-client`, `no-target`, `owner-offline`, `preview-frame-disconnected`, `ambiguous-preview`, or `ready`, plus a one-line recovery hint.

`mt_open` and `/api/browser/open` require the selected browser bridge to reconnect from a visible preview frame before they report success. When the dock activates a hidden iframe, the parent posts `mt-refresh-browser-state` into that frame so the injected bridge immediately refreshes its visibility/focus flags and reconnects if needed. This prevents agents from receiving a false-ready result from a stale hidden frame while the user-visible dev browser has not visibly moved.

For token-efficient discovery and diagnostics, agents should prefer:

- `mt_capabilities` / `mt_capabilities --json` for available commands, current status, and recommended recovery commands
- `mt_inspect` for status, proxy summary, page metadata, a shallow outline, forms, and console errors in one compact response
- `mt_inspect --screenshot` when the screenshot path is needed in the same diagnostic pass
- `mt_proxylog_summary [limit]` for status buckets, websocket totals, slow requests, and recent failures without dumping full headers

When the proxied page leaks root-relative asset URLs outside `/webpreview/{routeKey}` and those URLs collide with tlbx's own static prefixes (`/js/*`, `/css/*`, `/fonts/*`, `/img/*`, `/locales/*`, `/favicon/*`), tlbx now treats them as preview traffic when the request referer is a preview route. The only built-in exception today is `/js/html2canvas.min.js`, which remains a local tlbx asset used by the injected screenshot helper.

## Embedded tlbx Guardrails

When tlbx itself is running inside `/webpreview/`, the nested tlbx UI now treats its own web-preview controls as inactive:

- embedded tlbx pages no-op frontend calls that mutate `/api/webpreview/*`
- embedded tlbx pages do not create nested browser preview clients through the normal docked-preview path
- browser-ui `open` / `dock` / `detach` / `viewport` instructions are ignored inside embedded tlbx pages

This prevents the previewed tlbx app from clearing or repointing the host tlbx dev-browser target during bootstrap.

## Preview Sandbox

In dev-mode and local-dev runs, the docked preview iframe and detached popup iframe opt into a real sandbox for every target. Outside that mode, tlbx still force-sandboxes external HTTP(S) sites and local `file:` previews so an arbitrary page cannot execute with full access to the owning tlbx shell origin:

- baseline flags: `allow-scripts allow-forms allow-popups allow-modals allow-downloads`
- when the preview is loaded from the dedicated preview origin (`https://host:port+1`), tlbx also adds `allow-same-origin`
- when tlbx falls back to the primary app origin, it still omits `allow-same-origin`, so the proxied page runs with an opaque origin
- tlbx's own `localStorage`, `CacheStorage`, and service-worker scope are no longer shared with the previewed app

The dedicated preview origin makes `allow-same-origin` safe for self-preview and similar apps that require `localStorage` or `navigator.serviceWorker`: the iframe becomes same-origin with `port + 1`, not with the main tlbx shell on `port`.

Because sandboxed preview frames are cross-site from the main app's perspective, tlbx relaxes the auth cookie to `SameSite=None` only for dev-mode/local-dev runs where tlbx itself needs to operate inside that sandbox. Production/stable-style runs keep `SameSite=Lax`.

## Dedicated Preview Origin

When tlbx can reserve `port + 1`, preview clients now receive a dedicated frame origin on that secondary listener:

- the main app stays on `https://host:port`
- the iframe loads proxied content from `https://host:port+1`
- preview client registration returns that origin to the docked panel and detached popup

The preview listener blocks normal tlbx app pages and non-browser WebSockets on the secondary port, so leaked navigations do not fall back into the tlbx application on the preview origin. If the extra port is unavailable, tlbx falls back to the primary origin and keeps the sandbox protections from step 3.

The server must bind both the main app URL and the preview URL explicitly at startup. Advertising a preview origin without listening on `port + 1` breaks tlbx-in-tlbx immediately once the iframe tries to navigate to the isolated frame host.

## tlbx in tlbx

Self-preview is supported only when the dedicated preview origin is active:

- target the main app origin (`https://host:port`), not the preview origin (`port + 1`)
- the preview-origin listener itself is still rejected as a web-preview target, so the proxy never points at its own isolated frame host
- proxied requests to tlbx itself mirror the current `mm-session` auth cookie from the browser request into the in-memory proxy cookie jar before each upstream HTTP/WebSocket hop
- that mirrored auth cookie is deliberately excluded from cookie-disk persistence, so nested tlbx stays authenticated without writing tlbx session tokens into the preview cookie files

This is what keeps nested tlbx from falling into `/login.html` once its own `/api/*` and `/ws/*` traffic starts flowing through the dev browser.

The main tlbx shell also has to allow that isolated frame host in its own CSP `frame-src`. Without that, the browser blocks `https://host:port+1/webpreview/...` before the nested app can render.

For self-targets, internal upstream hops must not re-enter the catch-all "leaked root-relative URL" proxy path. tlbx marks those server-originated self-proxy requests and lets them fall through to local static files and normal handlers, which prevents recursive `/site.webmanifest` and `/favicon.ico` loops that otherwise explode into `431 Request Header Fields Too Large`.

## Canonical Host Adoption

tlbx only auto-updates the stored preview target when a **document/iframe HTML navigation** lands on a different authority:

- asset redirects no longer rewrite the preview target
- same-host/different-port URLs are treated as different authorities
- host canonicalization preserves the current preview base path for normal `/webpreview/{routeKey}/*` navigations
- `/_ext` HTML navigations switch the stored target to the new authority root so refresh/detach continue from the external site instead of the previous host

## Proxy Log

`GET /api/webpreview/proxylog?limit=N` returns the last N proxy requests (default 100) with full details:

- Request/response headers, cookies
- Upstream URL, status code, duration
- WebSocket sub-protocols, negotiated protocol
- Error messages on failure

`requestCookies` now reflects the effective cookie header tlbx forwarded from the preview's server-side cookie jar for that upstream URL, not just any explicit `Cookie` header present on the outgoing request object.

CLI: `mt_proxylog [limit]` / `Mt-ProxyLog [-Limit N]`

`GET /api/webpreview/proxylog/summary?limit=N` returns a compact text summary for agent use. CLI: `mt_proxylog_summary [limit]` / `Mt-ProxyLogSummary [-Limit N]`.

Use this as the **first diagnostic step** when a site doesn't work through the proxy.

## Upstream TLS

The Dev Browser proxy connects to upstream HTTPS targets from the `mt` server process. For explicitly targeted preview URLs, the proxy proceeds through upstream certificate validation errors such as expired certificates, matching the developer-browser "proceed anyway" workflow and `curl -k` diagnostics. This exception is scoped to Web Preview upstream HTTP/WebSocket connections; it does not affect tlbx's own server certificate, authentication, installer, update downloads, or other app HTTP clients.

The proxy also uses its own DNS connect fallback for upstream preview traffic. If a target host resolves to multiple addresses and the first address stalls, the proxy retries the remaining addresses before surfacing a tlbx timeout. This keeps dynamic DNS and mixed IPv6/IPv4 targets aligned with browser behavior.

## Debugging Checklist

When a website doesn't load through the web preview:

1. **`mt_proxylog`** — Check if requests reach upstream and what status codes come back
2. **`mt_log error`** — Check browser console for JS errors
3. **`mt_outline`** — Check if the page has any rendered content
4. **WebSocket entries in proxylog** — Check `statusCode` (101 = connected, 502 = failed), `subProtocols`, `error`
5. **`mt_exec` to inspect framework state** — e.g., `Blazor._internal.navigationManager` for baseUri/currentUri

### Common Failures

| Symptom | Likely Cause |
|---------|-------------|
| WS status 502 | Upstream rejected connection (wrong Origin, missing cookies, SSL error) |
| WS 101 but page empty | Framework routing issue — check NavigationManager or router state |
| Page renders but navigation broken | URL inconsistency between location.href and document.baseURI |
| CSS/JS 404s | Root-relative URLs claimed by `IsMidTermPath` or missing leaked-asset fallback — check whether the failing path collides with tlbx static prefixes and whether the request referer is the preview route |
| Login redirect loops | Cookies not forwarding — check `requestCookies`/`responseCookies` in proxylog |
| All assets return HTML | Host redirect (e.g. `foo.com` → `www.foo.com`) drops the path — proxy auto-updates target on first redirect |

Leaked root-relative asset chains can lose the original `/webpreview/{routeKey}` referer after the first rescued request. tlbx now remembers which preview first claimed leaked paths like `/js/login.js`, so follow-up imports from referers such as `/js/login.js` or `/js3/html2-login.js` can still recover the same `routeKey` instead of falling through to local `404`s on the preview origin.

## Implementation Files

| File | Role |
|------|------|
| `WebPreviewProxyMiddleware.cs` | Core proxy: HTTP forwarding, WebSocket relay, injected JS |
| `WebPreviewService.cs` | State: named preview sessions, target URLs, cookie jars, HTTP clients, proxy log ring buffers |
| `WebPreviewEndpoints.cs` | REST API: target CRUD, cookie management, proxy log, snapshots |
| `WebPreviewHtmlSnapshotSanitizer.cs` | Snapshot HTML cleanup helpers for stripping proxy artifacts and decoding external proxy URLs before writing exported previews |
| `MtcliScriptWriter.cs` | CLI helpers: `mt_*` / `Mt-*` browser and session commands, including `mt_session`, `mt_preview`, `mt_previews`, `mt_proxylog`, `mt_navigate`, etc. |

## Key Design Decisions

**No read-side spoofing.** Chrome blocks overriding `Location.prototype` properties. Partial spoofing creates fatal inconsistencies. Let all URLs consistently include `/webpreview/{routeKey}/`.

**No WebSocket content rewriting.** Frameworks use relative paths for routing. The absolute origin in URLs doesn't matter as long as `baseUri` and `currentUri` share the same origin. Relaying messages untouched eliminates an entire class of bugs (JSON corruption, MessagePack header mismatch, VarInt framing errors).

**Write-side interception is sufficient.** Outgoing APIs (fetch, XHR, WebSocket, history, element setters) are patched to add `/webpreview/{routeKey}` before requests leave JS. This ensures all requests route through the correct preview-scoped proxy middleware.

For targets that live under a deep document path but serve assets from the origin root (for example docs sites that load `/_astro/*` from a page under `/foo/bar/...`), tlbx now primes its root-fallback cache directly from the rewritten HTML before the browser requests those assets. That avoids the first-wave `404` noise where the proxy would otherwise try `targetBase + /_astro/...` once and only then learn to retry the server-root path.
