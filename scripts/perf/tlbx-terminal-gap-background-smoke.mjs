import fs from "node:fs/promises";
import fsSync from "node:fs";
import { spawn } from "node:child_process";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const {
  chromium,
} = require("../../docs/marketing/ScreenshotAutomation/node_modules/playwright");
const {
  PNG,
} = require("../../docs/marketing/ScreenshotAutomation/node_modules/playwright-core/lib/utilsBundle.js");

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
  "..",
);
const artifactRoot =
  process.env.TLBX_PERF_ARTIFACT_ROOT ||
  path.join(
    process.env.USERPROFILE || process.env.HOME || repoRoot,
    ".codex",
    "artifacts",
    "chrome-perf",
  );
const stamp = new Date()
  .toISOString()
  .replace(/[-:]/g, "")
  .replace(/\..+/, "")
  .replace("T", "-");
const runDir = path.resolve(
  artifactRoot,
  `${stamp}-tlbx-terminal-gap-background`,
);
const profileDir = path.join(runDir, "chrome-profile");
const screenshotPath = path.join(runDir, "terminal-gap-background.png");
const summaryPath = path.join(runDir, "summary.json");
const url = process.env.TLBX_PERF_URL || "https://127.0.0.1:2100/";

await fs.mkdir(profileDir, { recursive: true });

let context;
let ownerContext;
let page;
let ownerPage;
let sessionId;
let summary;
let browser;
let chrome;
let savedTransparency;

async function launchBrowser() {
  chrome = spawn(
    "C:/Program Files/Google/Chrome/Application/chrome.exe",
    [
      "--headless=new",
      "--remote-debugging-port=0",
      "--user-data-dir=" + profileDir,
      "--ignore-certificate-errors",
      "--no-first-run",
      "--no-default-browser-check",
    ],
    {
      windowsHide: true,
      stdio: [
        "ignore",
        fsSync.openSync(path.join(runDir, "chrome.stdout.log"), "w"),
        fsSync.openSync(path.join(runDir, "chrome.stderr.log"), "w"),
      ],
    },
  );
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      const port = Number(
        (
          await fs.readFile(path.join(profileDir, "DevToolsActivePort"), "utf8")
        ).split("\n")[0],
      );
      return await chromium.connectOverCDP("http://127.0.0.1:" + port);
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
  }
  throw new Error("Chrome startup failed");
}

async function authenticate(targetContext) {
  const cookie = process.env.TLBX_COOKIE_HEADER;
  if (!cookie) throw new Error("TLBX_COOKIE_HEADER required");
  const separator = cookie.indexOf("=");
  await targetContext.addCookies([
    {
      name: cookie.slice(0, separator),
      value: cookie.slice(separator + 1),
      url,
    },
  ]);
}

async function waitForTerminal(targetPage, id) {
  await targetPage.waitForFunction(
    (targetId) =>
      Boolean(
        document.querySelector(`.session-item[data-session-id="${targetId}"]`),
      ),
    id,
  );
  await targetPage.evaluate((targetId) => {
    document
      .querySelector(`.session-item[data-session-id="${targetId}"]`)
      ?.click();
  }, id);
  await targetPage.waitForFunction(
    (targetId) =>
      window.mmDebug?.activeId === targetId &&
      window.mmDebug?.terminals?.get(targetId)?.opened,
    id,
  );
}

async function ensureTerminalOwner(targetPage, id) {
  const containerSelector = `#terminal-${id}`;
  await targetPage.waitForFunction(
    (targetId) =>
      Boolean(
        document.getElementById(`terminal-${targetId}`)?.dataset
          .terminalPresentationRole,
      ),
    id,
  );
  const role = await targetPage
    .locator(containerSelector)
    .getAttribute("data-terminal-presentation-role");
  if (role !== "owner") {
    await targetPage.locator(`${containerSelector} > .scaled-overlay`).click();
    await targetPage.waitForFunction(
      (targetId) =>
        document.getElementById(`terminal-${targetId}`)?.dataset
          .terminalPresentationRole === "owner",
      id,
    );
  }
}

try {
  browser = await launchBrowser();
  context = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport: { width: 1703, height: 900 },
  });
  await authenticate(context);
  context.setDefaultTimeout(10_000);
  page = context.pages()[0] || (await context.newPage());
  await page.goto(url, { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() =>
    Boolean(window.mmDebug && document.querySelector(".terminal-page")),
  );

  ownerContext = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport: { width: 1901, height: 1100 },
  });
  await authenticate(ownerContext);
  ownerContext.setDefaultTimeout(10_000);
  ownerPage = ownerContext.pages()[0] || (await ownerContext.newPage());
  await ownerPage.goto(url, { waitUntil: "domcontentloaded" });
  await ownerPage.waitForFunction(() =>
    Boolean(window.mmDebug && document.querySelector(".terminal-page")),
  );

  sessionId = await ownerPage.evaluate(async () => {
    const response = await fetch("/api/sessions", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-MidTerm-Tab-Id": sessionStorage.getItem("mt-tab-id"),
      },
      body: JSON.stringify({
        shell: "Pwsh",
        workingDirectory: "Q:\\repos\\Jpa",
        cols: 42,
        rows: 22,
        launchRequestId: crypto.randomUUID(),
      }),
    });
    const body = await response.json();
    if (!response.ok)
      throw new Error(
        `Session creation failed: ${response.status} ${JSON.stringify(body)}`,
      );
    return body?.session?.id ?? body?.id;
  });

  await waitForTerminal(ownerPage, sessionId);
  await ensureTerminalOwner(ownerPage, sessionId);
  await waitForTerminal(page, sessionId);
  await page.locator(`#terminal-${sessionId} > .scaled-overlay`).waitFor();
  savedTransparency = await page.evaluate(async () => {
    const settings = await (await fetch("/api/settings")).json();
    return {
      terminalTransparency: settings.terminalTransparency,
      terminalCellBackgroundTransparency:
        settings.terminalCellBackgroundTransparency,
    };
  });

  const readEvidence = () =>
    page.evaluate((targetId) => {
      const container = document.getElementById(`terminal-${targetId}`);
      const xterm = container?.querySelector(".xterm");
      const fillers = [
        ...(container?.querySelectorAll(":scope > .terminal-gap-fill") ?? []),
      ];
      const filler = fillers[0] ?? null;
      if (!container || !xterm || !filler)
        throw new Error("Terminal gap presentation is incomplete.");
      const containerRect = container.getBoundingClientRect();
      const xtermRect = xterm.getBoundingClientRect();
      const fillerRect = filler.getBoundingClientRect();
      const fillerStyle = getComputedStyle(filler);
      const containerStyle = getComputedStyle(container);
      const terminalLayers = [
        ".xterm",
        ".xterm-scrollable-element",
        ".xterm-viewport",
        ".xterm-screen",
      ].map((selector) => {
        const element = container.querySelector(selector);
        if (!element) return { selector, missing: true };
        const style = getComputedStyle(element);
        return {
          selector,
          backgroundColor: style.backgroundColor,
          backgroundImage: style.backgroundImage,
          opacity: style.opacity,
        };
      });
      return {
        viewport: { width: innerWidth, height: innerHeight },
        cols: window.mmDebug.terminals.get(targetId).terminal.cols,
        rows: window.mmDebug.terminals.get(targetId).terminal.rows,
        containerRect: {
          left: containerRect.left,
          top: containerRect.top,
          width: containerRect.width,
          height: containerRect.height,
        },
        xtermRect: {
          left: xtermRect.left,
          top: xtermRect.top,
          width: xtermRect.width,
          height: xtermRect.height,
        },
        fillerRect: {
          left: fillerRect.left,
          top: fillerRect.top,
          width: fillerRect.width,
          height: fillerRect.height,
        },
        fillerCount: fillers.length,
        fillerClasses: [...filler.classList],
        fillerBackground: fillerStyle.backgroundImage,
        fillerBackgroundColor: fillerStyle.backgroundColor,
        fillerClipPath: fillerStyle.clipPath,
        terminalCanvasBackgroundStack: containerStyle
          .getPropertyValue("--terminal-canvas-background-stack")
          .trim(),
        terminalThemeBackground:
          window.mmDebug.terminals.get(targetId).terminal.options.theme
            ?.background,
        role: container.dataset.terminalPresentationRole,
        ratio: devicePixelRatio,
        contentWidth: parseFloat(
          containerStyle.getPropertyValue("--terminal-gap-content-width"),
        ),
        contentHeight: parseFloat(
          containerStyle.getPropertyValue("--terminal-gap-content-height"),
        ),
        terminalLayers,
        gapRight: containerStyle
          .getPropertyValue("--terminal-gap-right-width")
          .trim(),
        gapBottom: containerStyle
          .getPropertyValue("--terminal-gap-bottom-height")
          .trim(),
      };
    }, sessionId);

  const evidence = await readEvidence();

  if (evidence.fillerCount !== 1)
    throw new Error(`Expected one gap surface, found ${evidence.fillerCount}.`);
  if (!evidence.fillerClasses.includes("terminal-gap-fill-surface")) {
    throw new Error("The continuous terminal gap surface is missing.");
  }
  if (!evidence.fillerClipPath.startsWith("polygon("))
    throw new Error("The L-shaped gap clip is missing.");
  if (!evidence.fillerBackground || evidence.fillerBackground === "none") {
    throw new Error("The free pane area has no terminal background.");
  }

  const samples = [];
  for (const [ratio, transparency, height] of [
    [1, 0, 900],
    [1, 60, 900],
    [1.25, 60, 1200],
    [1.5, 60, 900],
    [2, 90, 1200],
  ]) {
    await context.close();
    context = await browser.newContext({
      ignoreHTTPSErrors: true,
      viewport: { width: 1703, height },
      deviceScaleFactor: ratio,
    });
    await authenticate(context);
    page = await context.newPage();
    await page.goto(url, { waitUntil: "domcontentloaded" });
    await waitForTerminal(page, sessionId);
    await page.evaluate(async (transparency) => {
      const response = await fetch("/api/settings", {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
          "X-MidTerm-Tab-Id": sessionStorage.getItem("mt-tab-id"),
        },
        body: JSON.stringify({
          terminalTransparency: transparency,
          terminalCellBackgroundTransparency: transparency,
        }),
      });
      if (!response.ok)
        throw new Error("Transparency setup failed: " + response.status);
    }, transparency);
    await page.reload({ waitUntil: "domcontentloaded" });
    await waitForTerminal(page, sessionId);
    await page.evaluate((targetId) => {
      document.getElementById(`terminal-${targetId}`).style.background =
        "rgb(160, 160, 160)";
      window.dispatchEvent(new Event("resize"));
    }, sessionId);
    await page.waitForTimeout(700);
    if (process.env.TLBX_GAP_BASELINE)
      await page.evaluate((targetId) => {
        const container = document.getElementById(`terminal-${targetId}`),
          screen = container.querySelector(".xterm-screen");
        container.querySelector(".xterm").style.clipPath = "none";
        const rect = screen.getBoundingClientRect();
        container.style.setProperty(
          "--terminal-gap-content-width",
          `${Math.min(container.clientWidth, rect.width).toFixed(3)}px`,
        );
        container.style.setProperty(
          "--terminal-gap-content-height",
          `${Math.min(container.clientHeight, rect.height).toFixed(3)}px`,
        );
      }, sessionId);
    const current = await readEvidence();
    if (current.role !== "follower")
      throw new Error("Second browser changed terminal ownership");
    const file = path.join(
      runDir,
      `edge-dpr${ratio}-transparency${transparency}.png`,
    );
    const png = PNG.sync.read(await page.screenshot({ path: file }));
    const pixel = (x, y) => {
      const i = (y * png.width + x) * 4;
      return Array.from(png.data.subarray(i, i + 3));
    };
    const seams = [];
    const { left, top, width, height: containerHeight } = current.containerRect;
    if (width - current.contentWidth > 4) {
      const x = Math.round((left + current.contentWidth) * ratio),
        y = Math.round(
          (top + Math.min(200, current.contentHeight / 2)) * ratio,
        );
      seams.push(Array.from({ length: 9 }, (_, i) => pixel(x + i - 4, y)));
    }
    if (containerHeight - current.contentHeight > 4) {
      const y = Math.round((top + current.contentHeight) * ratio),
        x = Math.round((left + current.contentWidth / 2) * ratio);
      seams.push(Array.from({ length: 9 }, (_, i) => pixel(x, y + i - 4)));
    }
    if (!seams.length) throw new Error("No exposed terminal edge was tested");
    const maxDelta = Math.max(
      ...seams.flatMap((line) =>
        [0, 1, 2].map(
          (channel) =>
            Math.max(...line.map((p) => p[channel])) -
            Math.min(...line.map((p) => p[channel])),
        ),
      ),
    );
    samples.push({
      ratio,
      transparency,
      maxDelta,
      seams,
      evidence: current,
      screenshotPath: file,
    });
  }
  await fs.copyFile(samples[0].screenshotPath, screenshotPath);
  // Independently composited alpha layers can differ by two 8-bit levels.
  // Reject an edge halo beyond that rounding noise; the original seam exceeds it.
  summary = {
    ok: samples.every((sample) => sample.maxDelta <= 2),
    url,
    sessionId,
    evidence,
    samples,
    screenshotPath,
  };
  if (!summary.ok) process.exitCode = 1;
} catch (error) {
  summary = {
    ok: false,
    url,
    sessionId: sessionId ?? null,
    error: error instanceof Error ? error.stack : String(error),
    screenshotPath: null,
  };
  throw error;
} finally {
  if (page && sessionId) {
    if (savedTransparency)
      await page
        .evaluate(async (settings) => {
          await fetch("/api/settings", {
            method: "PATCH",
            headers: {
              "Content-Type": "application/json",
              "X-MidTerm-Tab-Id": sessionStorage.getItem("mt-tab-id"),
            },
            body: JSON.stringify(settings),
          });
        }, savedTransparency)
        .catch(() => undefined);
    await page
      .evaluate(async (targetId) => {
        const removed = await fetch(
          `/api/sessions/${encodeURIComponent(targetId)}`,
          {
            method: "DELETE",
          },
        );
        if (!removed.ok)
          throw new Error("Fixture deletion failed: " + removed.status);
      }, sessionId)
      .catch(() => undefined);
    const cleanupVerified = await page
      .evaluate(
        async ({ targetId, settings }) => {
          const sessions = await (await fetch("/api/sessions")).json();
          const current = await (await fetch("/api/settings")).json();
          return (
            !sessions.sessions.some((session) => session.id === targetId) &&
            current.terminalTransparency === settings.terminalTransparency &&
            current.terminalCellBackgroundTransparency ===
              settings.terminalCellBackgroundTransparency
          );
        },
        { targetId: sessionId, settings: savedTransparency },
      )
      .catch(() => false);
    if (summary) {
      summary.cleanupVerified = cleanupVerified;
      if (!cleanupVerified) {
        summary.ok = false;
        process.exitCode = 1;
      }
    }
  }
  await ownerPage?.close().catch(() => undefined);
  await ownerContext?.close().catch(() => undefined);
  await context?.close().catch(() => undefined);
  if (browser) {
    const cdp = await browser.newBrowserCDPSession();
    await cdp.send("Browser.close").catch(() => undefined);
  } else chrome?.kill();
  if (summary)
    await fs.writeFile(
      summaryPath,
      `${JSON.stringify(summary, null, 2)}\n`,
      "utf8",
    );
}

console.log(JSON.stringify({ ...summary, summaryPath }, null, 2));
