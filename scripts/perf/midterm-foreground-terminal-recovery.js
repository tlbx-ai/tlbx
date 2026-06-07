const perf = window.__codexChromePerf;
if (!perf) {
  throw new Error("Chrome perf observer was not initialized.");
}

perf.scenario = {
  name: "midterm-foreground-terminal-recovery",
  sessionId: null,
  terminalCountBefore: 0,
  refreshCallsBeforeFreeze: 0,
  refreshCallsAfterResume: 0,
  xtermsAfterOpen: 0,
};

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitFor(predicate, label, timeoutMs = 10000) {
  const deadline = performance.now() + timeoutMs;
  do {
    const value = predicate();
    if (value) return value;
    await sleep(100);
  } while (performance.now() < deadline);
  throw new Error(`Timed out waiting for ${label}`);
}

const created = await fetch("/api/sessions", {
  method: "POST",
  headers: { "content-type": "application/json" },
  body: JSON.stringify({ cols: 96, rows: 28, shell: "Pwsh" }),
}).then((response) => {
  if (!response.ok) {
    throw new Error(`Session create failed: ${response.status}`);
  }
  return response.json();
});

perf.scenario.sessionId = created.id;

const state = await waitFor(() => {
  const terminalState = window.mmDebug?.terminals?.get(created.id);
  return terminalState?.opened ? terminalState : null;
}, "opened terminal");

perf.scenario.terminalCountBefore = window.mmDebug?.terminals?.size ?? 0;
perf.scenario.xtermsAfterOpen = document.querySelectorAll(".xterm").length;

const originalRefresh = state.terminal.refresh.bind(state.terminal);
state.terminal.refresh = (...args) => {
  perf.scenario.refreshCallsAfterResume += 1;
  return originalRefresh(...args);
};

perf.scenario.refreshCallsBeforeFreeze = perf.scenario.refreshCallsAfterResume;
