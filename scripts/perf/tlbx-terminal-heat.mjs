// Run against an isolated source instance; no Codex prompt is submitted.
import fs from "node:fs/promises";
import path from "node:path";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const {
  chromium,
} = require("../../docs/marketing/ScreenshotAutomation/node_modules/playwright");
const repo = path.resolve(import.meta.dirname, "../..");
const installed = process.env.TLBX_PERF_INSTALLED === "1";
const artifacts = path.join(
  repo,
  `.tlbx/animation-analysis/heat-browser${installed ? "-installed" : ""}`,
);
await fs.mkdir(artifacts, { recursive: true });
const url = process.env.TLBX_PERF_URL || "https://127.0.0.1:2102/";
assert(
  ["localhost", "127.0.0.1"].includes(new URL(url).hostname) &&
    (installed || !["", "2000", "2001"].includes(new URL(url).port)),
);
const trigger = path.join(artifacts, "trigger.txt");
const emitter = path.join(artifacts, "emitter.ps1");
await fs.writeFile(trigger, "idle");
await fs.writeFile(
  emitter,
  `
$last = 'idle'
while ($true) {
  $value = [IO.File]::ReadAllText('${trigger.replaceAll("'", "''")}')
  if ($value -ne $last -or $value -eq 'stream') { [Console]::WriteLine('Text: '+$value); $last=$value }
  [Console]::Write("$([char]27)[?2026h$([char]27)[38;2;120;130;140m⠁ ⠂ ⠄ ⠈ ⠐ ⠠ ⡀ ⢀$([char]27)[0m\r$([char]27)[?2026l")
  Start-Sleep -Milliseconds 150
}
`,
);
const context = await chromium.launchPersistentContext(
  path.join(artifacts, "profile"),
  {
    channel: "chrome",
    headless: true,
    ignoreHTTPSErrors: true,
    viewport: { width: 1600, height: 1000 },
  },
);
await context.addCookies(
  (process.env.TLBX_COOKIE_HEADER || "")
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
const page = context.pages()[0];
const ids = [];
const messages = [];
const summary = { checkpoints: [] };
page.on("websocket", (ws) =>
  ws.on("framereceived", ({ payload }) => {
    try {
      const value = JSON.parse(payload.toString());
      if (value.type === "terminal-text-activity")
        messages.push({ ...value, received: Date.now() });
    } catch {}
  }),
);
const api = async (method, route, data) => {
  const response = await context.request[method](new URL(route, url).href, {
    data,
  });
  assert(
    response.ok(),
    `${route}: ${response.status()} ${await response.text()}`,
  );
  const text = await response.text();
  return text ? JSON.parse(text) : null;
};
const create = async (launchCommand) => {
  const s = await api("post", "/api/sessions", {
    workingDirectory: artifacts,
    launchCommand,
  });
  ids.push(s.id);
  return s.id;
};
const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const read = (id) =>
  page
    .locator(`.session-item[data-session-id="${id}"] .heat-canvas`)
    .evaluate((el) => ({
      red: +getComputedStyle(el, "::before").opacity,
      blue: +getComputedStyle(el, "::after").opacity,
      base: getComputedStyle(el).backgroundColor,
      opacity: +getComputedStyle(el).opacity,
      running: el
        .getAnimations({ subtree: true })
        .filter((a) => a.playState === "running").length,
    }));
const checkpoint = async (id, label) => {
  const state = await read(id);
  const activity = await api(
    "get",
    `/api/sessions/${id}/activity?seconds=45&bellLimit=1`,
  );
  state.heat = activity.currentHeat;
  summary.checkpoints.push({ label, ...state });
  await page.screenshot({ path: path.join(artifacts, `${label}.png`) });
  return state;
};
const select = async (id) => {
  await page
    .locator(`.session-item[data-session-id="${id}"]`)
    .click({ position: { x: 12, y: 12 } });
  await page.waitForFunction(
    (id) => window.mmDebug?.terminals.get(id)?.opened,
    id,
  );
};
const terminalText = (id) =>
  page.evaluate((id) => {
    const t = window.mmDebug?.terminals.get(id)?.terminal;
    if (!t) return "";
    const b = t.buffer.active;
    return Array.from(
      { length: t.rows },
      (_, i) => b.getLine(b.baseY + i)?.translateToString(true) || "",
    ).join("\n");
  }, id);
try {
  await page.goto(url);
  const a = await create(`pwsh -NoProfile -File "${emitter}"`);
  const b = await create();
  await select(a);
  await wait(32_000);
  assert.equal((await checkpoint(a, "initial-cold")).red, 0);
  await fs.writeFile(trigger, "short");
  await page.waitForFunction(
    (id) =>
      +getComputedStyle(
        document.querySelector(
          `.session-item[data-session-id="${id}"] .heat-canvas`,
        ),
        "::before",
      ).opacity > 0.7,
    a,
  );
  assert((await checkpoint(a, "short-red")).red > 0.7);
  assert.equal((await read(b)).red, 0);
  await wait(5000);
  const blue = await checkpoint(a, "five-seconds-blue");
  assert(blue.blue > 0.85);
  assert(blue.heat > 0.78 && blue.heat < 0.85);
  await wait(10400);
  const cold = await checkpoint(a, "fifteen-seconds-grey");
  assert.equal(cold.red, 0);
  assert.equal(cold.blue, 0);
  assert(cold.opacity > 0.8);
  assert(cold.heat > 0.43 && cold.heat < 0.51);
  await wait(15_000);
  const gone = await checkpoint(a, "thirty-seconds-gone");
  assert.equal(gone.opacity, 0);
  assert.equal(gone.heat, 0);
  assert.equal(gone.running, 0);
  await fs.writeFile(trigger, "stream");
  await wait(1000);
  const streamStart = Date.now();
  for (let i = 0; i < 5; i++) {
    await wait(1000);
    assert((await read(a)).red > 0.65);
  }
  summary.streamMessages = messages.filter(
    (m) => m.sessionId === a && m.received >= streamStart,
  ).length;
  assert(summary.streamMessages <= 22);
  await fs.writeFile(trigger, "stopped");
  await wait(31_000);
  await select(b);
  await select(a);
  assert.equal((await checkpoint(a, "session-switch-cold")).running, 0);
  await page.reload();
  await select(a);
  await wait(1000);
  assert.equal((await checkpoint(a, "reload-cold")).red, 0);
  const client = await context.newCDPSession(page);
  await fs.writeFile(trigger, "before-freeze");
  await wait(500);
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", {
      configurable: true,
      get: () => "hidden",
    });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await client.send("Page.setWebLifecycleState", { state: "frozen" });
  await wait(32_000);
  await client.send("Page.setWebLifecycleState", { state: "active" });
  await page.evaluate(() => {
    delete document.visibilityState;
    document.dispatchEvent(new Event("visibilitychange"));
    document.dispatchEvent(new Event("resume"));
  });
  await wait(1000);
  assert.equal((await checkpoint(a, "background-return-cold")).red, 0);
  const codex = await create(
    "codex --no-alt-screen --model gpt-6-astra -c tui.whimsy=true",
  );
  await select(codex);
  for (let i = 0; i < 60; i++) {
    const t = await terminalText(codex);
    if (t.includes("Do you trust"))
      await api("post", `/api/sessions/${codex}/input/keys`, {
        keys: ["Enter"],
      });
    if (/[\u2801-\u28ff]/u.test(t)) break;
    await wait(500);
  }
  assert(
    /[\u2801-\u28ff]/u.test(await terminalText(codex)),
    "Real Codex sparkle must be visible",
  );
  await wait(32_000);
  await api("post", `/api/sessions/${codex}/redraw`);
  await wait(1500);
  const afterRedraw = await api(
    "get",
    `/api/sessions/${codex}/activity?seconds=45&bellLimit=1`,
  );
  assert.equal(afterRedraw.currentHeat, 0, "Repaint must stay cold");
  assert.equal((await checkpoint(codex, "explicit-redraw-cold")).opacity, 0);
  summary.redrawCold = true;
  const other = await context.newPage();
  await other.goto("about:blank");
  await other.bringToFront();
  await wait(500);
  await page.bringToFront();
  await wait(1500);
  await other.close();
  assert.equal(
    (await read(codex)).opacity,
    0,
    "Short background return must stay cold",
  );
  await client.send("Performance.enable");
  await client.send("Profiler.enable");
  await client.send("Profiler.start");
  const before = await client.send("Performance.getMetrics");
  const idleStart = Date.now();
  await page.evaluate((ids) => {
    window.heatMutations = 0;
    window.heatObserver = new MutationObserver(
      (ms) => (window.heatMutations += ms.length),
    );
    document.querySelectorAll(".heat-canvas").forEach((el) => {
      if (ids.includes(el.closest("[data-session-id]")?.dataset.sessionId))
        window.heatObserver.observe(el, { attributes: true });
    });
  }, ids);
  await wait(60_000);
  const after = await client.send("Performance.getMetrics");
  const profile = await client.send("Profiler.stop");
  await fs.writeFile(
    path.join(artifacts, "cpu-profile.cpuprofile"),
    JSON.stringify(profile.profile),
  );
  summary.idleHeatMessages = messages.filter(
    (m) => ids.includes(m.sessionId) && m.received >= idleStart,
  ).length;
  summary.idleHeatMutations = await page.evaluate(() => {
    window.heatObserver.disconnect();
    return window.heatMutations;
  });
  summary.metrics = Object.fromEntries(
    after.metrics.map((m) => [
      m.name,
      m.value - (before.metrics.find((b) => b.name === m.name)?.value || 0),
    ]),
  );
  assert.equal(summary.idleHeatMessages, 0);
  assert.equal(summary.idleHeatMutations, 0);
  for (const id of ids) {
    const s = await read(id);
    assert.equal(s.red, 0);
    assert.equal(s.blue, 0);
    assert.equal(s.running, 0);
  }
  await checkpoint(codex, "real-codex-minute-cold");
  summary.success = true;
} catch (error) {
  summary.error = String(error.stack || error);
  await page.screenshot({ path: path.join(artifacts, "failure.png") });
  process.exitCode = 1;
} finally {
  for (const id of ids.reverse())
    await api("delete", `/api/sessions/${id}`).catch(() => {});
  await context.close();
  await fs.writeFile(
    path.join(artifacts, "summary.json"),
    JSON.stringify(summary, null, 2),
  );
  console.log(JSON.stringify(summary));
}
