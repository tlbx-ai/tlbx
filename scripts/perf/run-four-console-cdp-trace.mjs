import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

const require = createRequire(import.meta.url);
const { chromium } = require('../../docs/marketing/ScreenshotAutomation/node_modules/playwright');

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const artifactRoot =
  process.env.MIDTERM_PERF_ARTIFACT_ROOT ||
  path.join(process.env.USERPROFILE || process.env.HOME || repoRoot, '.codex', 'artifacts', 'chrome-perf');
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\..+/, '').replace('T', '-');
const runDir = path.join(artifactRoot, `${stamp}-midterm-four-console-cdp-trace`);
const profileDir = path.join(runDir, 'chrome-profile');
const tracePath = path.join(runDir, 'trace.json');
const summaryPath = path.join(runDir, 'summary.json');
const url = process.env.MIDTERM_PERF_URL || 'https://127.0.0.1:2100/';
const sessionCount = 4;
const lineCount = Number(process.env.MIDTERM_PERF_LINE_COUNT || 1800);
const switchRounds = Number(process.env.MIDTERM_PERF_SWITCH_ROUNDS || 80);

await fs.mkdir(profileDir, { recursive: true });

function buildFloodCommand(index) {
  const payload = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'.repeat(8);
  return [
    "$ProgressPreference='SilentlyContinue'",
    `$payload='${payload}'`,
    `1..${lineCount} | ForEach-Object { "console-${index}-$($_) $payload" }`,
    `"console-${index}-done"`,
  ].join('; ');
}

async function requestJson(requestUrl, options = {}) {
  const response = await fetch(new URL(requestUrl, url), {
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    ...options,
  });
  if (!response.ok) {
    throw new Error(`${options.method || 'GET'} ${requestUrl} failed: ${response.status}`);
  }
  if (response.status === 204) return null;
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

async function requestText(requestUrl) {
  const response = await fetch(new URL(requestUrl, url));
  if (!response.ok) {
    throw new Error(`GET ${requestUrl} failed: ${response.status}`);
  }
  return response.text();
}

function getSessionId(response) {
  return response?.id || response?.session?.id || response?.sessionId || response?.sessionInfo?.id || null;
}

async function readTracingStream(client, streamHandle) {
  const chunks = [];
  let eof = false;
  while (!eof) {
    const read = await client.send('IO.read', { handle: streamHandle });
    if (read.data) chunks.push(read.data);
    eof = Boolean(read.eof);
  }
  await client.send('IO.close', { handle: streamHandle });
  return chunks.join('');
}

const createdSessionIds = [];
let browserContext;
let traceComplete;
try {
  browserContext = await chromium.launchPersistentContext(profileDir, {
    channel: 'chrome',
    headless: false,
    ignoreHTTPSErrors: true,
    viewport: { width: 1440, height: 1000 },
    args: ['--ignore-certificate-errors', '--disable-background-networking'],
  });
  const page = browserContext.pages()[0] || (await browserContext.newPage());
  const client = await browserContext.newCDPSession(page);

  traceComplete = new Promise((resolve) => {
    client.on('Tracing.tracingComplete', resolve);
  });

  await client.send('Tracing.start', {
    categories:
      'devtools.timeline,v8,blink,blink.user_timing,loading,disabled-by-default-devtools.timeline',
    transferMode: 'ReturnAsStream',
  });

  await page.goto(url, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.terminal-page', { timeout: 30000 });

  for (let i = 1; i <= sessionCount; i += 1) {
    const created = await requestJson('/api/sessions', {
      method: 'POST',
      body: JSON.stringify({ cols: 128, rows: 32, shell: 'Pwsh' }),
    });
    const sessionId = getSessionId(created);
    if (!sessionId) throw new Error(`Create session had no id: ${JSON.stringify(created)}`);
    createdSessionIds.push(sessionId);
    await requestJson(`/api/sessions/${encodeURIComponent(sessionId)}/name`, {
      method: 'PUT',
      body: JSON.stringify({ name: `perf-console-${i}` }),
    });
  }

  await page.waitForFunction(
    (ids) => ids.every((id) => document.querySelector(`.session-item[data-session-id="${CSS.escape(id)}"]`)),
    createdSessionIds,
    { timeout: 30000 },
  );

  const openedSessionResults = [];
  for (const sessionId of createdSessionIds) {
    const opened = await page.evaluate(async (id) => {
      const item = document.querySelector(`.session-item[data-session-id="${CSS.escape(id)}"]`);
      if (!(item instanceof HTMLElement)) {
        return false;
      }

      item.scrollIntoView({ block: 'nearest' });
      item.click();
      await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      return window.mmDebug?.activeId === id;
    }, sessionId);
    openedSessionResults.push({ sessionId, opened });
  }

  for (let index = 0; index < createdSessionIds.length; index += 1) {
    await requestJson(`/api/sessions/${encodeURIComponent(createdSessionIds[index])}/input/text`, {
      method: 'POST',
      body: JSON.stringify({ text: buildFloodCommand(index + 1), appendNewline: true }),
    });
  }

  const switchResults = await page.evaluate(
    ({ ids, rounds }) => {
      const results = [];
      for (let i = 0; i < rounds; i += 1) {
        const sessionId = ids[i % ids.length];
        const item = document.querySelector(`.session-item[data-session-id="${CSS.escape(sessionId)}"]`);
        const startedAt = performance.now();
        const beforeActiveId = window.mmDebug?.activeId ?? null;
        if (item instanceof HTMLElement) {
          item.scrollIntoView({ block: 'nearest' });
          item.click();
        }
        const afterActiveId = window.mmDebug?.activeId ?? null;
        results.push({
          sessionId,
          durationMs: performance.now() - startedAt,
          beforeActiveId,
          afterActiveId,
          selected: afterActiveId === sessionId,
          missingItem: !(item instanceof HTMLElement),
        });
      }
      return results;
    },
    { ids: createdSessionIds, rounds: switchRounds },
  );

  await page.waitForTimeout(8000);

  const tailChecks = [];
  for (let index = 0; index < createdSessionIds.length; index += 1) {
    const sessionId = createdSessionIds[index];
    const tail = await requestText(`/api/sessions/${encodeURIComponent(sessionId)}/buffer/tail?lines=8&stripAnsi=true`);
    tailChecks.push({
      sessionId,
      doneSeen: tail.includes(`console-${index + 1}-done`),
      tailLength: tail.length,
    });
  }

  await client.send('Tracing.end');
  const traceEvent = await traceComplete;
  const trace = await readTracingStream(client, traceEvent.stream);
  await fs.writeFile(tracePath, trace, 'utf8');

  const durations = switchResults.map((entry) => entry.durationMs).sort((a, b) => a - b);
  const percentile = (p) =>
    durations.length ? durations[Math.min(durations.length - 1, Math.floor(durations.length * p))] : null;
  const summary = {
    ok: true,
    url,
    runDir,
    tracePath,
    createdSessionIds,
    openedSessionResults,
    outputCommands: createdSessionIds.length,
    switchRounds,
    switchStats: {
      count: durations.length,
      p50Ms: percentile(0.5),
      p95Ms: percentile(0.95),
      maxMs: durations.at(-1) ?? null,
      selectedCount: switchResults.filter((entry) => entry.selected).length,
      missingItemCount: switchResults.filter((entry) => entry.missingItem).length,
    },
    tailChecks,
  };
  await fs.writeFile(summaryPath, `${JSON.stringify(summary, null, 2)}\n`, 'utf8');
  console.log(JSON.stringify(summary, null, 2));
} finally {
  for (const sessionId of createdSessionIds) {
    try {
      await fetch(new URL(`/api/sessions/${encodeURIComponent(sessionId)}`, url), { method: 'DELETE' });
    } catch {
      // External cleanup best effort.
    }
  }
  if (browserContext) {
    await browserContext.close();
  }
}
