import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";
import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const {
  chromium,
} = require("../../docs/marketing/ScreenshotAutomation/node_modules/playwright");
const url = process.env.TLBX_PERF_URL;
const out = path.resolve(
  process.env.TLBX_PERF_ARTIFACT_ROOT || ".dev/vertical-bounds/browser",
);
if (!url || !process.env.TLBX_COOKIE_HEADER)
  throw Error("URL and private cookie required");
fs.mkdirSync(out, { recursive: true });
const tuiPath = path.join(out, "last-row-tui.py");
fs.writeFileSync(
  tuiPath,
  `import os, sys, time, msvcrt, json
sys.stdout.write('\\x1b[?1049h\\x1b[?25l')
last = None
next_frame = 0
try:
    while True:
        size = os.get_terminal_size()
        if size != last:
            with open(sys.argv[0] + '.' + str(os.getpid()) + '.json', 'w') as evidence:
                json.dump({'cols': size.columns, 'rows': size.lines, 'stdout_tty': sys.stdout.isatty()}, evidence)
        if size != last or time.monotonic() >= next_frame:
            last = size
            next_frame = time.monotonic() + 0.25
            lines = [f'Row {row + 1}' for row in range(size.lines)]
            lines[-2] = '+--- INPUT BOX: last two terminal rows ---+'
            lines[-1] = '> Prompt must remain fully reachable'
            sys.stdout.write('\\x1b[2J\\x1b[H' + '\\r\\n'.join(line[:size.columns - 1] for line in lines))
            sys.stdout.flush()
        if msvcrt.kbhit() and msvcrt.getwch() == '\\x03':
            break
        time.sleep(0.05)
finally:
    sys.stdout.write('\\x1b[?1049l\\x1b[?25h')
`,
);
const profile = path.join(out, "chrome-" + Date.now());
fs.mkdirSync(profile, { recursive: true });
const chrome = spawn(
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
  [
    "--headless=new",
    "--remote-debugging-port=0",
    "--user-data-dir=" + profile,
    "--ignore-certificate-errors",
    "--no-first-run",
    "--no-default-browser-check",
  ],
  {
    windowsHide: true,
    stdio: [
      "ignore",
      fs.openSync(path.join(out, "chrome.stdout.log"), "w"),
      fs.openSync(path.join(out, "chrome.stderr.log"), "w"),
    ],
  },
);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let browser, context, page;
const summary = {
  ok: false,
  sessions: [],
  samples: [],
  errors: [],
  websockets: [],
};
async function api(method, route, data) {
  const tabId =
    page && (await page.evaluate(() => sessionStorage.getItem("mt-tab-id")));
  const headers = tabId ? { "X-MidTerm-Tab-Id": tabId } : undefined;
  const r = await context.request.fetch(url + route, {
    method,
    data,
    headers,
    ignoreHTTPSErrors: true,
  });
  if (!r.ok()) throw Error(route + ": " + r.status() + " " + (await r.text()));
  const t = await r.text();
  return t
    ? r.headers()["content-type"]?.includes("json")
      ? JSON.parse(t)
      : t
    : null;
}
async function sample(stage) {
  const expectedCount = {
    "desktop-expanded": 1,
    "desktop-collapsed": 1,
    "mobile-retained-collapse": 1,
    "vertical-two": 2,
    "vertical-three-nested": 3,
    "vertical-four-nested": 4,
    "short-deep-split": 4,
    "deep-split-restored": 4,
    "mixed-five": 5,
    "mixed-breakpoint": 5,
  }[stage];
  for (const id of summary.sessions) {
    const overlay = page.locator("#terminal-" + id + " > .scaled-overlay");
    if (await overlay.isVisible())
      await overlay.evaluate((button) => button.click());
  }
  await sleep(700);
  await page.waitForFunction((expected) => {
    const visible = [...window.mmDebug.terminals.values()].filter((state) => {
      const bounds = state.container.getBoundingClientRect();
      return bounds.width > 0 && bounds.height > 0;
    });
    return (
      visible.length === expected &&
      visible.every((state) => {
        const buffer = state.terminal.buffer.active;
        return (
          state.container.dataset.terminalPresentationRole === "owner" &&
          buffer
            .getLine(buffer.baseY + state.terminal.rows - 1)
            ?.translateToString(true)
            .includes("Prompt must remain fully reachable")
        );
      })
    );
  }, expectedCount);
  const evidence = await page.evaluate((stage) => {
    const rect = (e) => e?.getBoundingClientRect().toJSON();
    const area = document.querySelector(".terminals-area"),
      main = document.querySelector(".main-content"),
      footer = document.querySelector(".adaptive-footer-reserve");
    const bottom = Math.min(
      innerHeight,
      footer?.getBoundingClientRect().top ??
        main.getBoundingClientRect().bottom,
    );
    return {
      stage,
      width: innerWidth,
      height: innerHeight,
      bottom,
      area: rect(area),
      main: rect(main),
      footer: rect(footer),
      collapsed: document
        .querySelector(".terminal-page")
        .classList.contains("sidebar-collapsed"),
      panes: [...window.mmDebug.terminals.entries()]
        .filter(
          ([id, s]) =>
            s.container.getBoundingClientRect().height > 0 &&
            s.container.getBoundingClientRect().width > 0,
        )
        .map(([id, s]) => {
          const screen = s.container.querySelector(".xterm-screen"),
            wrap = s.container.closest(".session-wrapper"),
            panel = s.container.parentElement;
          return {
            id,
            container: rect(s.container),
            screen: rect(screen),
            wrapper: rect(wrap),
            panel: rect(panel),
            role: s.container.dataset.terminalPresentationRole,
            rows: s.terminal.rows,
            serverRows: s.serverRows,
            cols: s.terminal.cols,
            cell: s.terminal._core._renderService.dimensions.css.cell.height,
            transform: s.container.querySelector(".xterm").style.transform,
            last: s.terminal.buffer.active
              .getLine(s.terminal.buffer.active.baseY + s.terminal.rows - 1)
              ?.translateToString(true),
          };
        }),
    };
  }, stage);
  summary.samples.push(evidence);
  for (const pane of evidence.panes) {
    if (pane.role !== "owner")
      summary.errors.push(
        stage + " visible pane is not size-owned: " + pane.id,
      );
    if (!pane.last?.includes("Prompt must remain fully reachable"))
      summary.errors.push(
        stage + " live last-row marker missing in " + pane.id,
      );
    if (pane.container.bottom > evidence.bottom + 1)
      summary.errors.push(
        stage +
          " container below footer: " +
          (pane.container.bottom - evidence.bottom),
      );
    if (
      pane.screen.bottom >
      Math.min(evidence.bottom, pane.container.bottom) + 1
    )
      summary.errors.push(
        stage +
          " clipped last row: " +
          (pane.screen.bottom -
            Math.min(evidence.bottom, pane.container.bottom)),
      );
  }
  await page.screenshot({ path: path.join(out, stage + ".png") });
}
try {
  for (let i = 0; i < 100; i++) {
    try {
      const port = Number(
        fs
          .readFileSync(path.join(profile, "DevToolsActivePort"), "utf8")
          .split("\n")[0],
      );
      browser = await chromium.connectOverCDP("http://127.0.0.1:" + port);
      break;
    } catch {
      await sleep(100);
    }
  }
  if (!browser) throw Error("Chrome startup failed");
  context = browser.contexts()[0];
  const cookie = process.env.TLBX_COOKIE_HEADER,
    j = cookie.indexOf("=");
  await context.addCookies([
    { name: cookie.slice(0, j), value: cookie.slice(j + 1), url },
  ]);
  page = await context.newPage();
  page.setDefaultTimeout(10000);
  page.on("pageerror", (e) => summary.errors.push(e.message));
  page.on("websocket", (socket) => {
    const endpoint = new URL(socket.url()),
      record = {
        endpoint: endpoint.origin + endpoint.pathname,
        received: 0,
        sent: 0,
        receivedBytes: 0,
        closed: false,
        hints: [],
      };
    summary.websockets.push(record);
    socket.on("framereceived", ({ payload }) => {
      record.received++;
      record.receivedBytes += Buffer.byteLength(payload);
    });
    socket.on("framesent", ({ payload }) => {
      record.sent++;
      if (Buffer.isBuffer(payload) && [8, 14, 19].includes(payload[0]))
        record.hints.push({
          type: payload[0],
          session: payload.subarray(1, 9).toString(),
          ids: payload.subarray(9).toString(),
        });
    });
    socket.on("close", () => (record.closed = true));
    socket.on("socketerror", (error) =>
      summary.errors.push(record.endpoint + ": " + error),
    );
  });
  if (process.env.TLBX_BASELINE_CSS) {
    const css = fs.readFileSync(process.env.TLBX_BASELINE_CSS, "utf8");
    await page.route("**/css/app.css*", (route) =>
      route.fulfill({ status: 200, contentType: "text/css", body: css }),
    );
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(url);
  await page.waitForFunction(() => window.mmDebug?.terminals);
  for (let i = 0; i < 5; i++) {
    const s = await api("POST", "/api/sessions", {
      shell: "Pwsh",
      cols: 100,
      rows: 30,
      workingDirectory: "Q:\\repos\\Jpa",
      launchRequestId: crypto.randomUUID(),
    });
    summary.sessions.push(s.id);
  }
  await page.waitForFunction(
    (ids) =>
      ids.every((id) =>
        document.querySelector('.session-item[data-session-id="' + id + '"]'),
      ),
    summary.sessions,
  );
  const [a, b, c, d, e] = summary.sessions;
  await page
    .locator('.session-item[data-session-id="' + a + '"]')
    .click({ position: { x: 32, y: 12 } });
  await page.waitForFunction(
    (id) => window.mmDebug.terminals.get(id)?.opened,
    a,
  );
  for (const id of summary.sessions) {
    await page
      .locator('.session-item[data-session-id="' + id + '"]')
      .click({ position: { x: 32, y: 12 } });
    await page.waitForFunction(
      (id) => window.mmDebug.terminals.get(id)?.opened,
      id,
    );
    const takeover = page.locator("#terminal-" + id + " > .scaled-overlay");
    if (await takeover.isVisible())
      await takeover.evaluate((button) => button.click());
    await page.waitForFunction((id) => {
      const transport = window.mmDebug.transport(id);
      return transport && transport.renderedSeq === transport.receivedSeq;
    }, id);
    let shellReady = false;
    for (let attempt = 0; attempt < 50; attempt++) {
      const tail = await api(
        "GET",
        "/api/sessions/" + id + "/buffer/tail?lines=10&stripAnsi=true",
      );
      if (tail?.includes("PS ") && tail?.includes(">")) {
        shellReady = true;
        break;
      }
      await sleep(100);
    }
    if (!shellReady)
      throw Error("PowerShell did not become ready for fixture: " + id);
    await page.locator("#terminal-" + id + " .xterm-helper-textarea").focus();
    await page.keyboard.type("python '" + tuiPath.replaceAll("'", "''") + "'", {
      delay: 1,
    });
    await page.keyboard.press("Enter");
    await page.waitForFunction((id) => {
      const state = window.mmDebug.terminals.get(id),
        buffer = state?.terminal.buffer.active;
      return buffer
        ?.getLine(buffer.baseY + state.terminal.rows - 1)
        ?.translateToString(true)
        .includes("Prompt must remain fully reachable");
    }, id);
  }
  await page
    .locator('.session-item[data-session-id="' + a + '"]')
    .click({ position: { x: 32, y: 12 } });
  await page.waitForFunction(
    (id) =>
      window.mmDebug.terminals
        .get(id)
        ?.terminal.buffer.active.getLine(
          window.mmDebug.terminals.get(id).terminal.buffer.active.baseY +
            window.mmDebug.terminals.get(id).terminal.rows -
            1,
        )
        ?.translateToString(true)
        .includes("Prompt must remain fully reachable"),
    a,
  );

  await sample("desktop-expanded");
  await page.locator("#btn-collapse-sidebar").click();
  await sample("desktop-collapsed");
  await page.setViewportSize({ width: 390, height: 740 });
  await sample("mobile-retained-collapse");
  await page.setViewportSize({ width: 1440, height: 600 });
  await page.evaluate(
    ([a, b]) => window.mmDebug.layout.dock(a, b, "bottom"),
    [a, b],
  );
  await sample("vertical-two");
  await page.evaluate(
    ([b, c]) => window.mmDebug.layout.dock(b, c, "bottom"),
    [b, c],
  );
  await sample("vertical-three-nested");
  await page.evaluate(
    ([c, d]) => window.mmDebug.layout.dock(c, d, "bottom"),
    [c, d],
  );
  await sample("vertical-four-nested");
  await page.setViewportSize({ width: 1024, height: 420 });
  await sample("short-deep-split");
  await page.setViewportSize({ width: 1024, height: 900 });
  await sample("deep-split-restored");
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.evaluate(
    ([a, e]) => window.mmDebug.layout.dock(a, e, "right"),
    [a, e],
  );
  await sample("mixed-five");
  await page.setViewportSize({ width: 780, height: 620 });
  await sample("mixed-breakpoint");
  summary.ok = summary.errors.length === 0;
} catch (error) {
  summary.errors.push(error.stack || String(error));
  summary.failureTails = {};
  for (const id of summary.sessions) {
    summary.failureTails[id] = await api(
      "GET",
      "/api/sessions/" + id + "/buffer/tail?lines=50&stripAnsi=true",
    ).catch((e) => e.message);
  }
  const live = await api("GET", "/api/sessions").catch(() => null);
  summary.failureSessions = live?.sessions?.filter((session) =>
    summary.sessions.includes(session.id),
  );
  if (page)
    summary.failurePresentation = await page
      .evaluate(() => ({
        visibility: document.visibilityState,
        activeId: window.mmDebug.activeId,
        terminals: [...window.mmDebug.terminals].map(([id, state]) => ({
          id,
          opened: state.opened,
          role: state.container.dataset.terminalPresentationRole,
          rows: state.terminal.rows,
          activeBuffer: state.terminal.buffer.active.type,
          transport: Object.fromEntries(
            Object.entries(window.mmDebug.transport(id) || {}).map(
              ([key, value]) => [
                key,
                typeof value === "bigint" ? value.toString() : value,
              ],
            ),
          ),
          lines: Array.from({ length: state.terminal.rows }, (_, index) =>
            state.terminal.buffer.active
              .getLine(state.terminal.buffer.active.baseY + index)
              ?.translateToString(true),
          ).filter(Boolean),
        })),
      }))
      .catch(() => null);
  if (page)
    await page
      .screenshot({ path: path.join(out, "failure.png") })
      .catch(() => {});
} finally {
  for (const id of summary.sessions)
    await api("DELETE", "/api/sessions/" + id).catch((e) =>
      summary.errors.push("cleanup " + e.message),
    );
  if (context) {
    const remaining = await api("GET", "/api/sessions").catch((error) => {
      summary.errors.push("cleanup verification " + error.message);
      return null;
    });
    summary.cleanupVerified =
      remaining != null &&
      !remaining.sessions.some((session) =>
        summary.sessions.includes(session.id),
      );
    if (!summary.cleanupVerified)
      summary.errors.push(
        "Test-owned sessions remain or cleanup could not be verified",
      );
  }
  if (summary.errors.length) summary.ok = false;
  fs.writeFileSync(
    path.join(out, "summary.json"),
    JSON.stringify(summary, null, 2),
  );
  if (browser) {
    const cdp = await browser.newBrowserCDPSession();
    await cdp.send("Browser.close").catch(() => {});
  } else chrome.kill();
}
console.log(
  JSON.stringify({
    ok: summary.ok,
    samples: summary.samples.length,
    errors: summary.errors,
    summaryPath: path.join(out, "summary.json"),
  }),
);
if (!summary.ok) process.exitCode = 1;
