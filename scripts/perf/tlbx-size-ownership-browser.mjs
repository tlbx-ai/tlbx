// Browser regression: passive second device, real input and rendered echo latency.
import fs from "node:fs/promises";
import path from "node:path";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const {
  chromium,
} = require("../../docs/marketing/ScreenshotAutomation/node_modules/playwright");
const root = path.resolve(import.meta.dirname, "../..");
const url = process.env.TLBX_PERF_URL || "https://127.0.0.1:2104/";
const full = process.env.TLBX_OWNERSHIP_FULL === "1";
const artifacts = path.join(
  root,
  ".tlbx/size-ownership",
  process.env.TLBX_PERF_LABEL || (full ? "browser" : "latency-baseline"),
);
await fs.mkdir(artifacts, { recursive: true });
const emitter = path.join(artifacts, "echo.ps1");
await fs.writeFile(
  emitter,
  "[Console]::WriteLine('ECHO-READY')\nwhile ($true) { $k=[Console]::ReadKey($true); [Console]::WriteLine('ECHO-'+$k.KeyChar) }\n",
);
assert(
  !["2000", "2001", ""].includes(new URL(url).port),
  "Run only on an isolated source instance",
);
const browser = await chromium.launch({ channel: "chrome", headless: true });
const ids = [];
const contexts = [];
const checkpoints = [];
const cookie = (process.env.TLBX_COOKIE_HEADER || "")
  .split(";")
  .filter(Boolean)
  .map((s) => {
    const i = s.indexOf("=");
    return { name: s.slice(0, i).trim(), value: s.slice(i + 1).trim(), url };
  });
async function device(label, width, height) {
  const ctx = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport: { width, height },
  });
  contexts.push(ctx);
  await ctx.addCookies([
    ...cookie,
    { name: "mt-client-id", value: label + "-" + Date.now(), url },
  ]);
  await ctx.addInitScript(() => {
    window.auditSockets = [];
    const Native = window.WebSocket;
    window.WebSocket = class extends Native {
      constructor(...args) {
        super(...args);
        window.auditSockets.push(this);
      }
    };
    window.auditCommand = (command, payload) =>
      new Promise((resolve, reject) => {
        const ws = window.auditSockets.findLast(
          (s) => new URL(s.url).pathname === "/ws/state" && s.readyState === 1,
        );
        if (!ws) {
          reject(Error("state channel absent"));
          return;
        }
        const id = "audit-" + crypto.randomUUID();
        const timer = setTimeout(() => {
          ws.removeEventListener("message", receive);
          reject(Error("command timeout " + command));
        }, 15000);
        function receive(e) {
          if (typeof e.data !== "string") return;
          const m = JSON.parse(e.data);
          if (m.id === id) {
            clearTimeout(timer);
            ws.removeEventListener("message", receive);
            m.success === false
              ? reject(Error(JSON.stringify(m)))
              : resolve(m.data);
          }
        }
        ws.addEventListener("message", receive);
        ws.send(
          JSON.stringify({ type: "command", id, action: command, payload }),
        );
      });
  });
  const page = await ctx.newPage();
  await page.goto(url);
  await page.waitForFunction(
    () =>
      window.mmDebug &&
      window.auditSockets.some(
        (s) => new URL(s.url).pathname === "/ws/state" && s.readyState === 1,
      ),
  );
  return { ctx, page };
}
const role = (p, id) =>
  p.locator("#terminal-" + id).getAttribute("data-terminal-presentation-role");
const select = async (p, id) => {
  await p.locator(`.session-item[data-session-id="${id}"]`).click();
  await p.waitForFunction((id) => window.mmDebug.terminals.get(id)?.opened, id);
};
const checkpoint = async (name, p, id) => {
  const v = await p.evaluate((id) => {
    const s = window.mmDebug.terminals.get(id);
    return {
      role: s.container.dataset.terminalPresentationRole,
      epoch: s.container.dataset.terminalPresentationEpoch,
      cols: s.terminal.cols,
      rows: s.terminal.rows,
      width: s.container.getBoundingClientRect().width,
    };
  }, id);
  checkpoints.push({ name, ...v });
  console.log(name, JSON.stringify(v));
  await fs.writeFile(
    path.join(artifacts, "progress.json"),
    JSON.stringify(checkpoints, null, 2),
  );
  return v;
};
let desktop;
try {
  desktop = await device("desktop", 1600, 1000);
  const created = await desktop.page.evaluate(
    async (payload) => {
      const r = await fetch("/api/sessions", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-MidTerm-Tab-Id": sessionStorage.getItem("mt-tab-id"),
        },
        body: JSON.stringify(payload),
      });
      if (!r.ok) throw Error(await r.text());
      return r.json();
    },
    {
      cols: 140,
      rows: 40,
      workingDirectory: artifacts,
      launchCommand: `pwsh -NoProfile -File "${emitter}"`,
    },
  );
  const id = created.id;
  ids.push(id);
  await select(desktop.page, id);
  await desktop.page.waitForFunction((id) => {
    const b = window.mmDebug.terminals.get(id)?.terminal.buffer.active;
    return (
      b &&
      Array.from({ length: b.length }, (_, i) =>
        b.getLine(i)?.translateToString(true),
      )
        .join("\n")
        .includes("ECHO-READY")
    );
  }, id);
  const before = await checkpoint("desktop-owner", desktop.page, id);
  assert.equal(before.role, "owner");
  const samples = await desktop.page.evaluate(async (id) => {
    const term = window.mmDebug.terminals.get(id).terminal;
    const samples = [];
    for (let i = 0; i < 80; i++) {
      const c = String.fromCharCode(33 + i);
      const marker = "ECHO-" + c;
      await new Promise((resolve, reject) => {
        const start = performance.now();
        const initial = term.buffer.active.baseY + term.buffer.active.cursorY;
        const timeout = setTimeout(() => {
          d.dispose();
          reject(Error("echo timeout " + c));
        }, 5000);
        const d = term.onWriteParsed(() => {
          const b = term.buffer.active;
          let found = false;
          for (let n = initial; n < b.length; n++) {
            if (b.getLine(n)?.translateToString(true).includes(marker)) {
              found = true;
              break;
            }
          }
          if (found) {
            d.dispose();
            clearTimeout(timeout);
            requestAnimationFrame(() => {
              samples.push(performance.now() - start);
              resolve();
            });
          }
        });
        term.input(c, true);
      });
    }
    return samples.sort((a, b) => a - b);
  }, id);
  const latency = {
    samples: samples.length,
    p50: samples[40],
    p95: samples[76],
    max: samples.at(-1),
  };
  console.log("latency", JSON.stringify(latency));
  assert(
    latency.p95 < 100,
    `Rendered echo p95 exceeded 100 ms: ${latency.p95}`,
  );
  let ownerScaling;
  if (process.env.TLBX_SCALING_PROBE === "1") {
    await desktop.page.evaluate(
      (id) => window.mmDebug.terminals.get(id).terminal.scrollToTop(),
      id,
    );
    await desktop.page.setViewportSize({ width: 1600, height: 700 });
    await new Promise((r) => setTimeout(r, 500));
    ownerScaling = await desktop.page
      .locator("#terminal-" + id + " .xterm")
      .evaluate((el) => ({
        transform: el.style.transform,
        width: el.getBoundingClientRect().width,
        parentWidth: el.parentElement.getBoundingClientRect().width,
      }));
    console.log("owner-scaling", JSON.stringify(ownerScaling));
    if (process.env.TLBX_EXPECT_NATURAL_OWNER === "1")
      assert.equal(ownerScaling.transform, "");
    await desktop.page.screenshot({
      path: path.join(artifacts, "owner-scrollback-height.png"),
    });
  }
  if (full) {
    const ipad = await device("ipad", 1024, 768);
    await select(ipad.page, id);
    assert.equal(await role(ipad.page, id), "follower");
    // Repeated terminal-generated replies must not count as input or steal a lease.
    await desktop.page.goto("about:blank");
    for (let n = 0; n < 7; n++) {
      await new Promise((r) => setTimeout(r, 45000));
      await ipad.page.evaluate(
        (id) =>
          new Promise((r) =>
            window.mmDebug.terminals.get(id).terminal.write("\x1b[6n", r),
          ),
        id,
      );
      await ipad.page.setViewportSize({
        width: 1024 + (n % 2) * 40,
        height: 768,
      });
      await ipad.page.reload();
      await select(ipad.page, id);
      assert.equal(
        (await checkpoint("passive-ipad-" + n, ipad.page, id)).role,
        "follower",
      );
    }
    await desktop.page.goto(url);
    await select(desktop.page, id);
    const returned = await checkpoint("desktop-return", desktop.page, id);
    assert.equal(returned.role, "owner");
    assert.equal(returned.epoch, before.epoch);
    assert.equal(returned.cols, before.cols);
    assert.equal(returned.rows, before.rows);
    assert.equal(await role(ipad.page, id), "follower");
    // The 5-minute protection has elapsed. Actual input on the iPad now transfers ownership.
    await ipad.page.evaluate(
      (id) => window.mmDebug.terminals.get(id).terminal.focus(),
      id,
    );
    await ipad.page.keyboard.press("x");
    await ipad.page.waitForFunction(
      (id) =>
        document.getElementById("terminal-" + id)?.dataset
          .terminalPresentationRole === "owner",
      id,
    );
    await checkpoint("ipad-input-owner", ipad.page, id);
    assert.equal(await role(desktop.page, id), "follower");
    // Explicit takeover overrides the fresh lease and fits the desktop viewport.
    for (const close of await desktop.page
      .locator(".modal-overlay .modal-close")
      .all())
      await close.click();
    await desktop.page
      .locator("#terminal-" + id + " > .scaled-overlay")
      .click();
    await desktop.page.waitForFunction(
      (id) =>
        document.getElementById("terminal-" + id)?.dataset
          .terminalPresentationRole === "owner",
      id,
    );
    await checkpoint("desktop-explicit-owner", desktop.page, id);
    const blocked = await ipad.ctx.request.post(
      new URL(`/api/sessions/${id}/resize`, url).href,
      { data: { cols: 80, rows: 24 } },
    );
    assert.equal(blocked.status(), 409);
    checkpoints.push({
      name: "rest-resize-rejected",
      status: blocked.status(),
    });
    await desktop.page.screenshot({
      path: path.join(artifacts, "desktop-final.png"),
    });
  }
  await fs.writeFile(
    path.join(artifacts, "summary.json"),
    JSON.stringify(
      { url, full, latency, ownerScaling, checkpoints, passed: true },
      null,
      2,
    ),
  );
} finally {
  for (const id of ids)
    if (desktop)
      await desktop.ctx.request
        .delete(new URL("/api/sessions/" + id, url).href)
        .catch(() => {});
  await browser.close();
}
