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
async function device(label, width, height, profileId) {
  const ctx = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport: { width, height },
  });
  contexts.push(ctx);
  await ctx.addCookies([
    ...cookie,
    { name: "mt-client-id", value: profileId || crypto.randomUUID(), url },
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
  // The item center can be its bookmark action; select the actual session label.
  await p
    .locator(`.session-item[data-session-id="${id}"]`)
    .click({ position: { x: 32, y: 12 } });
  await p.waitForFunction((id) => window.mmDebug.terminals.get(id)?.opened, id);
  assert.equal(
    await p.locator(".modal-overlay:visible").count(),
    0,
    "Session selection opened an unexpected modal",
  );
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
    const vpn = await device("vpn", 1250, 900);
    const profile = (await desktop.ctx.cookies()).find(
      (c) => c.name === "mt-client-id",
    ).value;
    const sibling = await device("sibling", 1180, 820, profile);
    for (const view of [ipad, vpn, sibling]) {
      await select(view.page, id);
      assert.equal(await role(view.page, id), "follower");
    }
    await ipad.page.screenshot({
      path: path.join(artifacts, "scaled-follower.png"),
    });
    const assertOwner = async (view, name) => {
      await view.page.waitForFunction(
        (id) =>
          document.getElementById("terminal-" + id)?.dataset
            .terminalPresentationRole === "owner",
        id,
      );
      await view.page.waitForFunction((id) => {
        const s = window.mmDebug.terminals.get(id);
        const screen = s.container
          .querySelector(".xterm-screen")
          .getBoundingClientRect();
        const container = s.container.getBoundingClientRect();
        const cell = s.terminal._core._renderService.dimensions.css.cell;
        return (
          s.serverCols === s.terminal.cols &&
          s.serverRows === s.terminal.rows &&
          screen.width <= container.width + 1 &&
          screen.bottom <= container.bottom + 1 &&
          container.width - screen.width < cell.width + 2 &&
          container.bottom - screen.bottom < cell.height + 2
        );
      }, id);
      const cp = await checkpoint(name, view.page, id);
      const geometry = await view.page.evaluate((id) => {
        const state = window.mmDebug.terminals.get(id);
        const screen = state.container
          .querySelector(".xterm-screen")
          .getBoundingClientRect();
        const container = state.container.getBoundingClientRect();
        const cell = state.terminal._core._renderService.dimensions.css.cell;
        return {
          screen: {
            width: screen.width,
            height: screen.height,
            bottom: screen.bottom,
          },
          container: {
            width: container.width,
            height: container.height,
            bottom: container.bottom,
          },
          cell,
          serverCols: state.serverCols,
          serverRows: state.serverRows,
          transform: state.container.querySelector(".xterm").style.transform,
        };
      }, id);
      assert.equal(geometry.transform, "");
      assert.equal(cp.cols, geometry.serverCols);
      assert.equal(cp.rows, geometry.serverRows);
      assert(
        geometry.screen.width <= geometry.container.width + 1,
        JSON.stringify(geometry),
      );
      assert(
        geometry.screen.bottom <= geometry.container.bottom + 1,
        JSON.stringify(geometry),
      );
      assert(
        geometry.container.width - geometry.screen.width <
          geometry.cell.width + 2,
        JSON.stringify(geometry),
      );
      assert(
        geometry.container.bottom - geometry.screen.bottom <
          geometry.cell.height + 2,
        JSON.stringify(geometry),
      );
      checkpoints.push({ name: name + "-geometry", ...geometry });
      return cp;
    };
    const typeHere = async (view, name, key) => {
      await view.page.bringToFront();
      await view.page.evaluate(
        (id) => window.mmDebug.terminals.get(id).terminal.focus(),
        id,
      );
      await view.page.keyboard.press(key);
      return assertOwner(view, name);
    };
    const active = await typeHere(ipad, "ipad-immediate-input-owner", "x");
    assert.equal(await role(desktop.page, id), "follower");
    for (const [name, view] of [
      ["desktop", desktop],
      ["vpn", vpn],
      ["sibling", sibling],
    ]) {
      for (let n = 0; n < 3; n++) {
        await view.page.bringToFront();
        await view.page.evaluate(
          (id) =>
            new Promise((resolve) =>
              window.mmDebug.terminals
                .get(id)
                .terminal.write("\x1b[6n", resolve),
            ),
          id,
        );
        await view.page.setViewportSize({
          width: 1190 + n * 30,
          height: 810 + n * 20,
        });
        await view.page.reload();
        await select(view.page, id);
        assert.equal(
          (await checkpoint(name + "-passive-" + n, view.page, id)).role,
          "follower",
        );
      }
      assert.equal(
        (await checkpoint("ipad-retains-after-" + name, ipad.page, id)).epoch,
        active.epoch,
      );
    }
    // Real transport gap: queue an old user's key, then type in the active browser.
    // Reconnect must deliver the queued key without reclaiming terminal dimensions.
    await desktop.page.evaluate(async (id) => {
      const socket = window.auditSockets.findLast(
        (s) => new URL(s.url).pathname === "/ws/mux" && s.readyState === 1,
      );
      const closed = new Promise((resolve) =>
        socket.addEventListener("close", resolve, { once: true }),
      );
      socket.close();
      await closed;
      window.mmDebug.terminals.get(id).terminal.input("Ω", true);
    }, id);
    await typeHere(ipad, "ipad-current-input-during-other-reconnect", "q");
    await desktop.page.waitForFunction((id) => {
      const b = window.mmDebug.terminals.get(id).terminal.buffer.active;
      return Array.from({ length: b.length }, (_, i) =>
        b.getLine(i)?.translateToString(true),
      )
        .join("\n")
        .includes("ECHO-Ω");
    }, id);
    assert.equal(await role(desktop.page, id), "follower");
    assert.equal(
      (await checkpoint("buffered-key-keeps-ipad-owner", ipad.page, id)).epoch,
      active.epoch,
    );
    const stale = await desktop.page.evaluate(
      ([id, epoch]) =>
        window.auditCommand("terminal.resize", {
          sessionId: id,
          cols: 80,
          rows: 24,
          expectedEpoch: Number(epoch),
        }),
      [id, before.epoch],
    );
    assert.equal(stale.resizeApplied, false);
    // Same-profile tabs obey the same human-input rule as different devices.
    await typeHere(desktop, "desktop-human-return", "y");
    const sameProfile = await typeHere(
      sibling,
      "same-profile-human-input",
      "z",
    );
    await desktop.page.bringToFront();
    await desktop.page.reload();
    await select(desktop.page, id);
    assert.equal(await role(desktop.page, id), "follower");
    assert.equal(
      (
        await checkpoint(
          "sibling-survives-passive-profile-return",
          sibling.page,
          id,
        )
      ).epoch,
      sameProfile.epoch,
    );
    await typeHere(vpn, "vpn-human-input", "v");
    await typeHere(ipad, "ipad-human-return", "i");
    const blocked = await desktop.ctx.request.post(
      new URL(`/api/sessions/${id}/resize`, url).href,
      { data: { cols: 80, rows: 24 } },
    );
    assert.equal(blocked.status(), 409);
    assert.equal(await ipad.page.locator(".modal-overlay:visible").count(), 0);
    assert.equal(
      await desktop.page.locator(".modal-overlay:visible").count(),
      0,
    );
    await ipad.page.screenshot({
      path: path.join(artifacts, "active-owner.png"),
    });
    await desktop.page.screenshot({
      path: path.join(artifacts, "passive-follower.png"),
    });
    await vpn.page.setViewportSize({ width: 390, height: 844 });
    const narrow = await typeHere(vpn, "narrow-human-input", "m");
    assert(narrow.cols < active.cols);
    // Capture the settled mobile view, after the existing off-canvas drawer transition.
    await vpn.page.waitForFunction(
      () =>
        document.querySelector(".sidebar").getBoundingClientRect().right <= 1,
    );
    await vpn.page.screenshot({
      path: path.join(artifacts, "narrow-owner.png"),
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
