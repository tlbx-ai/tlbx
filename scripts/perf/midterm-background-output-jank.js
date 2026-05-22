const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const raf = () => new Promise((resolve) => requestAnimationFrame(resolve));
const twoRaf = async () => {
  await raf();
  await raf();
};

const STRESS_DURATION_MS = 4200;
const BACKGROUND_SESSION_COUNT = 3;
const BACKGROUND_LINE_COUNT = 3500;

async function waitFor(predicate, timeoutMs = 15000, intervalMs = 100) {
  const deadline = performance.now() + timeoutMs;
  let lastError = null;
  while (performance.now() < deadline) {
    try {
      const value = await predicate();
      if (value) return value;
    } catch (error) {
      lastError = error;
    }
    await sleep(intervalMs);
  }

  const suffix = lastError
    ? ` Last error: ${String(lastError.message || lastError)}`
    : "";
  throw new Error(`Timed out waiting for scenario condition.${suffix}`);
}

async function requestJson(url, options = {}) {
  const controller = new AbortController();
  const timer = setTimeout(
    () => controller.abort(),
    options.timeoutMs || 10000,
  );
  try {
    const response = await fetch(url, {
      headers: {
        "Content-Type": "application/json",
        ...(options.headers || {}),
      },
      ...options,
      signal: controller.signal,
    });
    if (!response.ok) {
      throw new Error(
        `${options.method || "GET"} ${url} failed: ${response.status}`,
      );
    }
    if (response.status === 204) return null;
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  } finally {
    clearTimeout(timer);
  }
}

async function requestText(url, options = {}) {
  const controller = new AbortController();
  const timer = setTimeout(
    () => controller.abort(),
    options.timeoutMs || 10000,
  );
  try {
    const response = await fetch(url, {
      headers: {
        "Content-Type": "application/json",
        ...(options.headers || {}),
      },
      ...options,
      signal: controller.signal,
    });
    if (!response.ok) {
      throw new Error(
        `${options.method || "GET"} ${url} failed: ${response.status}`,
      );
    }
    return await response.text();
  } finally {
    clearTimeout(timer);
  }
}

function getSessionId(session) {
  return (
    session?.id ||
    session?.session?.id ||
    session?.sessionId ||
    session?.sessionInfo?.id ||
    null
  );
}

function sessionItem(sessionId) {
  return document.querySelector(
    `.session-item[data-session-id="${CSS.escape(sessionId)}"]`,
  );
}

function terminalState(sessionId) {
  return window.mmDebug?.terminals?.get(sessionId) || null;
}

function collectDomCounts() {
  const selectors = [
    ".session-item",
    "[data-session-id]",
    ".session-terminal-wrapper",
    ".terminal-container",
    ".xterm",
    ".xterm-screen",
    ".xterm-rows > div",
    ".xterm-viewport",
    ".session-tab-bar",
    ".layout-pane",
  ];
  return Object.fromEntries(
    selectors.map((selector) => [
      selector,
      document.querySelectorAll(selector).length,
    ]),
  );
}

function collectCreatedTerminalState(sessionIds) {
  return Object.fromEntries(
    sessionIds.map((sessionId) => {
      const state = terminalState(sessionId);
      return [
        sessionId,
        {
          exists: !!state,
          opened: !!state?.opened,
          hidden: !!state?.container?.classList?.contains("hidden"),
          rows: state?.terminal?.rows ?? null,
          cols: state?.terminal?.cols ?? null,
        },
      ];
    }),
  );
}

function buildStats(values) {
  const sorted = values
    .filter((value) => Number.isFinite(value))
    .sort((a, b) => a - b);
  const percentile = (p) =>
    sorted.length
      ? sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))]
      : null;
  const average = sorted.length
    ? sorted.reduce((sum, value) => sum + value, 0) / sorted.length
    : null;
  return {
    count: sorted.length,
    p50Ms: percentile(0.5),
    p95Ms: percentile(0.95),
    p99Ms: percentile(0.99),
    maxMs: sorted.length ? sorted[sorted.length - 1] : null,
    averageMs: average,
  };
}

function startStressFrameSampler() {
  const frames = [];
  let running = true;
  let last = performance.now();

  const tick = (now) => {
    frames.push(now - last);
    last = now;
    if (running) requestAnimationFrame(tick);
  };

  requestAnimationFrame(tick);

  return {
    stop() {
      running = false;
      return {
        frameStats: buildStats(frames),
      };
    },
  };
}

function getProfilerLongTaskCount() {
  return window.__codexChromePerf?.longTasks?.length ?? 0;
}

function getProfilerLongTasksSince(index) {
  return (window.__codexChromePerf?.longTasks || []).slice(index);
}

function summarizeLongTasks(longTasks) {
  const durations = longTasks
    .map((task) => task.duration)
    .filter((duration) => Number.isFinite(duration));
  const stats = buildStats(durations);
  return {
    count: durations.length,
    maxMs: stats.maxMs,
    totalMs: durations.reduce((sum, duration) => sum + duration, 0),
    p95Ms: stats.p95Ms,
  };
}

async function createSession(name) {
  const session = await requestJson("/api/sessions", {
    method: "POST",
    body: JSON.stringify({ cols: 120, rows: 30, shell: "Pwsh" }),
    timeoutMs: 20000,
  });
  const sessionId = getSessionId(session);
  if (!sessionId) {
    throw new Error(
      `Create session response had no session id: ${JSON.stringify(session)}`,
    );
  }

  await waitFor(() => sessionItem(sessionId), 20000, 100);
  await requestJson(`/api/sessions/${encodeURIComponent(sessionId)}/name`, {
    method: "PUT",
    body: JSON.stringify({ name }),
    timeoutMs: 10000,
  });
  return sessionId;
}

async function switchToSession(sessionId) {
  const item = await waitFor(() => sessionItem(sessionId), 12000, 100);
  const startedAt = performance.now();
  item.scrollIntoView({ block: "nearest" });
  item.click();

  await waitFor(() => window.mmDebug?.activeId === sessionId, 10000, 50);
  await waitFor(() => terminalState(sessionId)?.opened, 15000, 100);
  await twoRaf();
  return performance.now() - startedAt;
}

async function sendTextInput(
  sessionId,
  text,
  appendNewline = true,
  timeoutMs = 15000,
) {
  await requestJson(
    `/api/sessions/${encodeURIComponent(sessionId)}/input/text`,
    {
      method: "POST",
      body: JSON.stringify({ text, appendNewline }),
      timeoutMs,
    },
  );
}

function buildBackgroundCommand(index) {
  const payload = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".repeat(5);
  return [
    "$ProgressPreference='SilentlyContinue'",
    `$payload='${payload}'`,
    `1..${BACKGROUND_LINE_COUNT} | ForEach-Object { "bg-${index}-$($_) $payload" }`,
    `"bg-${index}-done"`,
  ].join("; ");
}

async function sendForegroundTyping(activeSessionId, durationMs) {
  const startedAt = performance.now();
  let chunks = 0;
  let failedChunks = 0;

  while (performance.now() - startedAt < durationMs) {
    chunks += 1;
    const appendNewline = chunks % 12 === 0;
    const text = appendNewline ? ` #fg-${chunks}` : `fg${chunks} `;
    try {
      await sendTextInput(activeSessionId, text, appendNewline, 5000);
    } catch {
      failedChunks += 1;
    }
    await sleep(35);
  }

  return {
    chunks,
    failedChunks,
    durationMs: performance.now() - startedAt,
  };
}

async function readTail(sessionId) {
  return requestText(
    `/api/sessions/${encodeURIComponent(sessionId)}/buffer/tail?lines=8&stripAnsi=true`,
    { timeoutMs: 10000 },
  );
}

async function main() {
  const result = {
    startedAt: new Date().toISOString(),
    href: location.href,
    title: document.title,
    userAgent: navigator.userAgent,
    visibilityState: document.visibilityState,
    serviceVersion: null,
    initialDomCounts: null,
    finalDomCounts: null,
    createdSessionIds: [],
    activeSessionId: null,
    backgroundSessionIds: [],
    switchDurationsMs: [],
    terminalsBeforeStress: null,
    terminalsAfterStress: null,
    foregroundTyping: null,
    backgroundCommands: 0,
    backgroundTailChecks: [],
    stressFrameStats: null,
    stressLongTasks: null,
    cleanupDeleted: 0,
    step: "init",
  };

  window.__midtermBackgroundOutputScenario = result;

  await waitFor(
    () => window.mmDebug && document.querySelector(".terminal-page"),
    20000,
  );
  result.serviceVersion = await fetch("/api/version").then((response) =>
    response.text(),
  );
  result.initialDomCounts = collectDomCounts();

  try {
    result.step = "create-sessions";
    const activeSessionId = await createSession("perf-active-typing");
    result.createdSessionIds.push(activeSessionId);
    result.activeSessionId = activeSessionId;

    for (let i = 1; i <= BACKGROUND_SESSION_COUNT; i += 1) {
      const sessionId = await createSession(`perf-hidden-bg-${i}`);
      result.createdSessionIds.push(sessionId);
      result.backgroundSessionIds.push(sessionId);
    }

    result.step = "open-all-terminals";
    for (const sessionId of result.createdSessionIds) {
      result.switchDurationsMs.push(await switchToSession(sessionId));
    }

    result.step = "focus-active";
    result.switchDurationsMs.push(await switchToSession(activeSessionId));
    await sleep(800);
    result.terminalsBeforeStress = collectCreatedTerminalState(
      result.createdSessionIds,
    );

    result.step = "stress-hidden-output";
    const longTaskStartIndex = getProfilerLongTaskCount();
    const sampler = startStressFrameSampler();
    const stressStartedAt = performance.now();

    await Promise.all(
      result.backgroundSessionIds.map((sessionId, index) =>
        sendTextInput(
          sessionId,
          buildBackgroundCommand(index + 1),
          true,
          20000,
        ),
      ),
    );
    result.backgroundCommands = result.backgroundSessionIds.length;
    result.foregroundTyping = await sendForegroundTyping(
      activeSessionId,
      STRESS_DURATION_MS,
    );
    await sleep(1200);

    const frameSummary = sampler.stop();
    const stressEndedAt = performance.now();
    result.stressFrameStats = {
      ...frameSummary.frameStats,
      durationMs: stressEndedAt - stressStartedAt,
    };
    result.stressLongTasks = summarizeLongTasks(
      getProfilerLongTasksSince(longTaskStartIndex),
    );
    result.terminalsAfterStress = collectCreatedTerminalState(
      result.createdSessionIds,
    );

    result.step = "verify-background-output";
    result.backgroundTailChecks = await Promise.all(
      result.backgroundSessionIds.map(async (sessionId, index) => {
        try {
          const tail = await readTail(sessionId);
          return {
            sessionId,
            doneSeen: tail.includes(`bg-${index + 1}-done`),
            tailLength: tail.length,
          };
        } catch (error) {
          return {
            sessionId,
            doneSeen: false,
            error: String(error.message || error),
          };
        }
      }),
    );

    result.step = "complete";
    result.completedAt = new Date().toISOString();
    return result;
  } finally {
    result.step = `cleanup-after-${result.step}`;
    for (const sessionId of result.createdSessionIds) {
      try {
        const response = await fetch(
          `/api/sessions/${encodeURIComponent(sessionId)}`,
          {
            method: "DELETE",
          },
        );
        if (response.ok) result.cleanupDeleted += 1;
      } catch {
        // Best-effort cleanup. The scenario result records how many sessions were deleted.
      }
    }
    await sleep(1000);
    result.finalDomCounts = collectDomCounts();
    if (window.__codexChromePerf) {
      window.__codexChromePerf.scenario = result;
    }
  }
}

return await Promise.race([
  main(),
  new Promise((_, reject) =>
    setTimeout(
      () =>
        reject(new Error("MidTerm background output jank scenario timeout")),
      90000,
    ),
  ),
]);
