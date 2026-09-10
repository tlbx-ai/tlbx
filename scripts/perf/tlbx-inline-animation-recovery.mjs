// Real Codex sparkle regression. Run only against an isolated source instance:
// TLBX_PERF_URL=https://127.0.0.1:2102/ node scripts/perf/tlbx-inline-animation-recovery.mjs
// No prompt is submitted; the marker remains an unsent composer draft.
import fs from "node:fs/promises";
import path from "node:path";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const {
  chromium,
} = require("../../docs/marketing/ScreenshotAutomation/node_modules/playwright");
const url = process.env.TLBX_PERF_URL || "https://127.0.0.1:2102/";
const target = new URL(url);
if (
  !["localhost", "127.0.0.1"].includes(target.hostname) ||
  ["", "2000", "2001"].includes(target.port)
) {
  throw new Error(
    "Use an isolated loopback source instance; this test changes its scrollback limit.",
  );
}
const repo = path.resolve(import.meta.dirname, "../..");
const artifacts = path.join(repo, ".tlbx/animation-analysis/browser");
await fs.mkdir(artifacts, { recursive: true });
const context = await chromium.launchPersistentContext(
  path.join(artifacts, "profile"),
  {
    channel: "chrome",
    headless: true,
    ignoreHTTPSErrors: true,
    viewport: { width: 1600, height: 1000 },
  },
);
const cookie = process.env.TLBX_COOKIE_HEADER;
if (cookie) {
  await context.addCookies(
    cookie
      .split(";")
      .filter(Boolean)
      .map((part) => {
        const i = part.indexOf("=");
        return {
          name: part.slice(0, i).trim(),
          value: part.slice(i + 1).trim(),
          url,
        };
      }),
  );
}
const page = context.pages()[0] || (await context.newPage());
const api = context.request;
let sessionId;
let oldScrollback;
let client;
const summary = { url, checkpoints: [] };
const marker = "SPARKLE-DRAFT-KEPT";

async function json(method, route, data) {
  const response = await api[method](new URL(route, url).href, { data });
  if (!response.ok())
    throw new Error(
      `${method} ${route}: ${response.status()} ${await response.text()}`,
    );
  const body = await response.text();
  return body ? JSON.parse(body) : null;
}
async function selectSession() {
  await page.locator(`.session-item[data-session-id="${sessionId}"]`).click();
  await page.waitForFunction(
    (id) => window.mmDebug?.terminals.get(id)?.opened,
    sessionId,
  );
}
async function checkpoint(label) {
  await page.waitForFunction(
    ({ id, marker }) => {
      const terminal = window.mmDebug?.terminals.get(id)?.terminal;
      if (!terminal) return false;
      const b = terminal.buffer.active;
      const rows = Array.from(
        { length: terminal.rows },
        (_, i) => b.getLine(b.baseY + i)?.translateToString(true) || "",
      );
      const text = rows.join("\n");
      return (
        text.includes(marker) &&
        text.includes("gpt-6-astra") &&
        /[\u2801-\u28ff]/u.test(text)
      );
    },
    { id: sessionId, marker },
    { timeout: 30000 },
  );
  const state = await page.evaluate((id) => {
    const t = window.mmDebug.terminals.get(id).terminal;
    const b = t.buffer.active;
    const rows = Array.from(
      { length: t.rows },
      (_, i) => b.getLine(b.baseY + i)?.translateToString(true) || "",
    );
    return {
      text: rows.join("\n"),
      cols: t.cols,
      rows: t.rows,
      transport: window.mmDebug.transport(id),
      bufferType: b.type,
    };
  }, sessionId);
  if (state.bufferType !== "normal")
    throw new Error("Test must exercise Codex in the normal buffer.");
  summary.checkpoints.push({ label, ...state });
  await page.screenshot({ path: path.join(artifacts, `${label}.png`) });
}
async function waitForEviction() {
  const before = await json(
    "get",
    `/api/sessions/${sessionId}/state?includeBuffer=false`,
  );
  const start = BigInt(before.terminalTransport.sourceSeq);
  const deadline = Date.now() + 60000;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 1000));
    const state = await json(
      "get",
      `/api/sessions/${sessionId}/state?includeBuffer=false`,
    );
    if (BigInt(state.terminalTransport.sourceSeq) - start > 70000n) return;
  }
  throw new Error(
    "Sparkle did not produce enough output to expire the 64 KiB replay ring.",
  );
}
try {
  const settings = await json("get", "/api/settings");
  oldScrollback = settings.scrollbackBytes;
  await json("patch", "/api/settings", { scrollbackBytes: 65536 });
  await page.goto(url);
  await page.waitForFunction(() => !!window.mmDebug);
  const created = await json("post", "/api/sessions", {
    shell: "Pwsh",
    workingDirectory: path.join(repo, ".tlbx/animation-analysis"),
    cols: 160,
    rows: 48,
    launchRequestId: crypto.randomUUID(),
    launchCommand:
      "codex --no-alt-screen --model gpt-6-astra -c tui.whimsy=true",
  });
  sessionId = created.session?.id || created.id;
  summary.sessionId = sessionId;
  await selectSession();
  await page.waitForFunction(
    (id) => {
      const t = window.mmDebug.terminals.get(id).terminal;
      const text = Array.from({ length: t.buffer.active.length }, (_, i) =>
        t.buffer.active.getLine(i)?.translateToString(true),
      ).join("\n");
      return text.includes("Do you trust") || /[\u2801-\u28ff]/u.test(text);
    },
    sessionId,
    { timeout: 30000 },
  );
  const trustPrompt = await page.evaluate((id) => {
    const t = window.mmDebug.terminals.get(id).terminal;
    return Array.from({ length: t.buffer.active.length }, (_, i) =>
      t.buffer.active.getLine(i)?.translateToString(true),
    )
      .join("\n")
      .includes("Do you trust");
  }, sessionId);
  if (trustPrompt)
    await json("post", `/api/sessions/${sessionId}/input/keys`, {
      keys: ["Enter"],
    });
  await page.waitForFunction(
    (id) => {
      const t = window.mmDebug.terminals.get(id).terminal;
      const b = t.buffer.active;
      return Array.from({ length: t.rows }, (_, i) =>
        b.getLine(b.baseY + i)?.translateToString(true),
      ).some((line) => /[\u2801-\u28ff]/u.test(line));
    },
    sessionId,
    { timeout: 30000 },
  );
  await json("post", `/api/sessions/${sessionId}/input/text`, { text: marker });
  await checkpoint("before-background");
  const resetsBeforeBackground =
    summary.checkpoints.at(-1).transport.recoveryResetCount;
  client = await context.newCDPSession(page);
  // Headless Chrome does not reliably hide a tab on OS minimization. Control the
  // Page Visibility input, then genuinely freeze renderer work through CDP.
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", {
      configurable: true,
      get: () => "hidden",
    });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await client.send("Page.setWebLifecycleState", { state: "frozen" });
  await waitForEviction();
  await client.send("Page.setWebLifecycleState", { state: "active" });
  await page.evaluate(() => {
    delete document.visibilityState;
    document.dispatchEvent(new Event("visibilitychange"));
    document.dispatchEvent(new Event("resume"));
  });
  await page.waitForFunction(
    ({ id, resets }) =>
      window.mmDebug.transport(id).recoveryResetCount > resets,
    { id: sessionId, resets: resetsBeforeBackground },
  );
  await checkpoint("after-background");
  await waitForEviction();
  await page.reload();
  await selectSession();
  await checkpoint("after-reload");
  const raw = await json("get", `/api/sessions/${sessionId}/buffer/text`);
  if (!/[\u2801-\u28ff]/u.test(raw.text) || !raw.text.includes("\x1b[?2026h")) {
    throw new Error("Sparkle is not active after recovery.");
  }
  summary.passed = true;
} catch (error) {
  summary.passed = false;
  summary.error = String(error);
  await client
    ?.send("Page.setWebLifecycleState", { state: "active" })
    .catch(() => {});
  await page
    .screenshot({ path: path.join(artifacts, "failure.png"), timeout: 5000 })
    .catch(() => {});
  process.exitCode = 1;
} finally {
  if (sessionId)
    await api.delete(new URL(`/api/sessions/${sessionId}`, url).href);
  if (oldScrollback !== undefined)
    await json("patch", "/api/settings", { scrollbackBytes: oldScrollback });
  await context.close();
  await fs.writeFile(
    path.join(artifacts, "summary.json"),
    JSON.stringify(
      summary,
      (_, value) => (typeof value === "bigint" ? value.toString() : value),
      2,
    ),
  );
  console.log(
    JSON.stringify({
      passed: summary.passed,
      error: summary.error,
      summary: path.join(artifacts, "summary.json"),
    }),
  );
}
