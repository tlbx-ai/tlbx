const result = {
  name: "midterm-background-live-output-smoke",
  sessionId: null,
  bufferRequests: [],
  serverTailDoneSeen: false,
  serverTailSample: "",
  hiddenTextHasDone: false,
  finalBaseY: null,
  finalBufferLength: null,
  finalTextHasDone: false,
  error: null,
};

window.__midtermBackgroundLiveOutputSmoke = result;

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitFor(predicate, label, timeoutMs = 15000) {
  const deadline = performance.now() + timeoutMs;
  do {
    const value = await predicate();
    if (value) return value;
    await sleep(100);
  } while (performance.now() < deadline);
  throw new Error(`Timed out waiting for ${label}`);
}

async function requestJson(url, options = {}) {
  const response = await fetch(url, {
    ...options,
    headers: {
      "content-type": "application/json",
      ...(options.headers ?? {}),
    },
  });
  if (!response.ok) {
    throw new Error(
      `${options.method ?? "GET"} ${url} failed: ${response.status}`,
    );
  }
  const text = await response.text();
  return text ? JSON.parse(text) : {};
}

function decodeSessionId(frame) {
  let sessionId = "";
  for (let index = 1; index < 9; index += 1) {
    const value = frame[index];
    if (value) sessionId += String.fromCharCode(value);
  }
  return sessionId;
}

function getTerminalState(sessionId) {
  return window.mmDebug?.terminals?.get(sessionId) ?? null;
}

function getTerminalText(sessionId) {
  const state = getTerminalState(sessionId);
  const buffer = state?.terminal?.buffer?.active;
  if (!buffer) return "";
  const lines = [];
  for (let index = 0; index < buffer.length; index += 1) {
    lines.push(buffer.getLine(index)?.translateToString(true) ?? "");
  }
  return lines.join("\n");
}

function setDocumentVisibility(hidden) {
  Object.defineProperty(document, "hidden", {
    value: hidden,
    configurable: true,
  });
  Object.defineProperty(document, "visibilityState", {
    value: hidden ? "hidden" : "visible",
    configurable: true,
  });
  document.dispatchEvent(new Event("visibilitychange"));
}

const originalHiddenDescriptor = Object.getOwnPropertyDescriptor(
  document,
  "hidden",
);
const originalVisibilityDescriptor = Object.getOwnPropertyDescriptor(
  document,
  "visibilityState",
);
const originalSend = WebSocket.prototype.send;

WebSocket.prototype.send = function patchedSend(data) {
  const frame =
    data instanceof Uint8Array
      ? data
      : data instanceof ArrayBuffer
        ? new Uint8Array(data)
        : null;
  if (frame && frame[0] === 0x06) {
    result.bufferRequests.push({
      sessionId: decodeSessionId(frame),
      mode: frame[9] ?? null,
      byteLength: frame.byteLength,
    });
  }
  return originalSend.apply(this, arguments);
};

try {
  await waitFor(
    () => window.mmDebug && document.querySelector(".terminal-page"),
    "MidTerm UI",
  );

  const created = await requestJson("/api/sessions", {
    method: "POST",
    body: JSON.stringify({ cols: 96, rows: 28, shell: "Pwsh" }),
  });
  const sessionId = created.id;
  result.sessionId = sessionId;

  const row = await waitFor(
    () => document.querySelector(`[data-session-id="${sessionId}"]`),
    "created session row",
  );
  row.click();
  await waitFor(
    () => window.mmDebug?.activeId === sessionId,
    "created session active",
  );
  await waitFor(
    () => getTerminalState(sessionId)?.opened,
    "created terminal opened",
  );

  const marker = `hidden-resume-${Date.now()}`;
  setDocumentVisibility(true);
  await requestJson(
    `/api/sessions/${encodeURIComponent(sessionId)}/input/text`,
    {
      method: "POST",
      body: JSON.stringify({
        appendNewline: true,
        text: [
          "$ProgressPreference='SilentlyContinue'",
          `$marker='${marker}'`,
          "for ($i = 1; $i -le 180; $i++) { Write-Output ($marker + ' line-' + $i + ' ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789') }",
          "Write-Output ($marker + ' done')",
        ].join("; "),
      }),
    },
  );

  await waitFor(
    async () => {
      const text = await fetch(
        `/api/sessions/${encodeURIComponent(sessionId)}/buffer/tail?lines=40&stripAnsi=true`,
      ).then((response) => response.text());
      result.serverTailSample = text.slice(-1000);
      result.serverTailDoneSeen = text.includes(`${marker} done`);
      return result.serverTailDoneSeen;
    },
    "server tail to contain hidden output",
    30000,
  );

  await waitFor(
    () => getTerminalText(sessionId).includes(`${marker} done`),
    "hidden live terminal output",
  );
  result.hiddenTextHasDone = getTerminalText(sessionId).includes(
    `${marker} done`,
  );

  setDocumentVisibility(false);
  window.dispatchEvent(new Event("focus"));

  await sleep(500);

  const foregroundReplayRequest = result.bufferRequests.find(
    (request) =>
      request.sessionId === sessionId &&
      request.mode === 0 &&
      request.byteLength === 10,
  );
  if (foregroundReplayRequest) {
    throw new Error(
      "Foreground resume requested full replay even though hidden output stayed live",
    );
  }

  const state = getTerminalState(sessionId);
  result.finalBaseY = state.terminal.buffer.active.baseY;
  result.finalBufferLength = state.terminal.buffer.active.length;
  result.finalTextHasDone = getTerminalText(sessionId).includes(
    `${marker} done`,
  );

  return result;
} catch (error) {
  result.error = String(error?.message || error);
  return result;
} finally {
  WebSocket.prototype.send = originalSend;
  if (originalHiddenDescriptor) {
    Object.defineProperty(document, "hidden", originalHiddenDescriptor);
  } else {
    delete document.hidden;
  }
  if (originalVisibilityDescriptor) {
    Object.defineProperty(
      document,
      "visibilityState",
      originalVisibilityDescriptor,
    );
  } else {
    delete document.visibilityState;
  }
  setDocumentVisibility(false);

  if (result.sessionId) {
    await fetch(`/api/sessions/${encodeURIComponent(result.sessionId)}`, {
      method: "DELETE",
    }).catch(() => {});
  }
}
