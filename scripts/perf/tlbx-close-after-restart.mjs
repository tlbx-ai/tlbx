import fs from "node:fs/promises";
import path from "node:path";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const {
  chromium,
} = require("../../docs/marketing/ScreenshotAutomation/node_modules/playwright");
const repo = path.resolve(import.meta.dirname, "../..");
const dir = path.join(repo, ".tlbx/animation-analysis/close-browser");
await fs.mkdir(dir, { recursive: true });
const ready = path.join(dir, "ready.json");
const resumed = path.join(dir, "resumed");
await fs.rm(ready, { force: true });
await fs.rm(resumed, { force: true });
const url = "https://127.0.0.1:2102/";
const context = await chromium.launchPersistentContext(
  path.join(dir, "profile"),
  {
    channel: "chrome",
    headless: true,
    ignoreHTTPSErrors: true,
    viewport: { width: 1600, height: 1000 },
  },
);
await context.addCookies(
  process.env.TLBX_COOKIE_HEADER.split(";").map((part) => {
    const i = part.indexOf("=");
    return {
      name: part.slice(0, i).trim(),
      value: part.slice(i + 1).trim(),
      url,
    };
  }),
);
const page = context.pages()[0];
const sessions = [];
const result = { closed: [] };
async function api(method, route, data) {
  const r = await context.request[method](new URL(route, url).href, { data });
  assert(r.ok(), `${route}: ${r.status()}`);
  const t = await r.text();
  return t ? JSON.parse(t) : null;
}
try {
  await page.goto(url);
  for (let i = 0; i < 3; i++)
    sessions.push(
      await api("post", "/api/sessions", {
        workingDirectory: "Q:\\repos\\Jpa",
      }),
    );
  for (const s of sessions)
    await page.locator(`.session-item[data-session-id="${s.id}"]`).waitFor();
  await fs.writeFile(
    ready,
    JSON.stringify(sessions.map((s) => ({ id: s.id, pid: s.pid }))),
  );
  const deadline = Date.now() + 180000;
  while (Date.now() < deadline) {
    try {
      await fs.access(resumed);
      break;
    } catch {}
    await new Promise((r) => setTimeout(r, 500));
  }
  await fs.access(resumed);
  const after = await api("get", "/api/sessions");
  for (const s of sessions)
    assert.equal(after.sessions.find((a) => a.id === s.id)?.pid, s.pid);
  for (const s of sessions) {
    const row = page.locator(`.session-item[data-session-id="${s.id}"]`);
    await row.hover({ position: { x: 12, y: 12 } });
    const response = page.waitForResponse(
      (r) =>
        r.request().method() === "DELETE" &&
        new URL(r.url()).pathname === `/api/sessions/${s.id}`,
    );
    const start = Date.now();
    await row.locator(".session-close").click();
    const r = await response;
    assert(r.ok(), `Close ${s.id}: ${r.status()}`);
    await row.waitFor({ state: "detached", timeout: 10000 });
    result.closed.push({ id: s.id, pid: s.pid, ms: Date.now() - start });
  }
  await new Promise((r) => setTimeout(r, 10000));
  const final = await api("get", "/api/sessions");
  for (const s of sessions) {
    assert(!final.sessions.some((a) => a.id === s.id));
    assert.equal(
      await page.locator(`.session-item[data-session-id="${s.id}"]`).count(),
      0,
    );
  }
  await page.screenshot({ path: path.join(dir, "closed-after-restart.png") });
  result.success = true;
} catch (e) {
  result.error = String(e.stack || e);
  await page.screenshot({ path: path.join(dir, "failure.png") });
  process.exitCode = 1;
} finally {
  for (const s of sessions)
    await api("delete", `/api/sessions/${s.id}`).catch(() => {});
  await context.close();
  await fs.writeFile(
    path.join(dir, "summary.json"),
    JSON.stringify(result, null, 2),
  );
  console.log(JSON.stringify(result));
}
