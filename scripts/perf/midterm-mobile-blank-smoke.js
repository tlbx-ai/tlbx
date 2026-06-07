const perf = window.__codexChromePerf;
if (!perf) {
  throw new Error("Chrome perf observer was not initialized.");
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitFor(predicate, label, timeoutMs = 15000) {
  const deadline = performance.now() + timeoutMs;
  do {
    const value = predicate();
    if (value) return value;
    await sleep(100);
  } while (performance.now() < deadline);
  throw new Error(`Timed out waiting for ${label}`);
}

await waitFor(() => document.readyState === "complete", "document ready");
await sleep(1000);

if (document.querySelectorAll(".terminal-container").length === 0) {
  await fetch("/api/sessions", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ cols: 96, rows: 28, shell: "Pwsh" }),
  }).then((response) => {
    if (!response.ok) {
      throw new Error(`Session create failed: ${response.status}`);
    }
    return response.json();
  });
}

await waitFor(
  () => document.querySelector(".terminal-container"),
  "terminal container",
  10000,
);
await sleep(1000);

const terminalArea = document.getElementById("terminal-area");
const sidebar = document.getElementById("sidebar");
const topbar = document.getElementById("mobile-topbar");
const scaleOverlay = document.querySelector(
  ".terminal-container.scaled .scaled-overlay",
);
const login = document.querySelector(
  "#login-form, .login-form, input[type=password]",
);
const appShell = document.querySelector(
  ".app-shell, #app, .main-content, .terminal-container",
);
const visibleContainers = Array.from(
  document.querySelectorAll(".terminal-container"),
).filter((element) => {
  const rect = element.getBoundingClientRect();
  const style = getComputedStyle(element);
  return (
    rect.width > 0 &&
    rect.height > 0 &&
    style.display !== "none" &&
    style.visibility !== "hidden" &&
    style.opacity !== "0"
  );
});

perf.scenario = {
  name: "midterm-mobile-blank-smoke",
  href: location.href,
  title: document.title,
  viewport: {
    innerWidth,
    innerHeight,
    visualWidth: window.visualViewport?.width ?? null,
    visualHeight: window.visualViewport?.height ?? null,
  },
  bodyTextLength: document.body.innerText.length,
  bodyTextPreview: document.body.innerText.slice(0, 500),
  loginVisible: Boolean(login),
  appShellVisible: Boolean(appShell),
  terminalArea: terminalArea
    ? {
        rect: terminalArea.getBoundingClientRect().toJSON(),
        display: getComputedStyle(terminalArea).display,
        visibility: getComputedStyle(terminalArea).visibility,
      }
    : null,
  mobileTopbar: topbar
    ? {
        rect: topbar.getBoundingClientRect().toJSON(),
        display: getComputedStyle(topbar).display,
      }
    : null,
  sidebar: sidebar
    ? {
        rect: sidebar.getBoundingClientRect().toJSON(),
        display: getComputedStyle(sidebar).display,
        transform: getComputedStyle(sidebar).transform,
      }
    : null,
  scaleOverlay: scaleOverlay
    ? {
        text: scaleOverlay.textContent,
        rect: scaleOverlay.getBoundingClientRect().toJSON(),
        display: getComputedStyle(scaleOverlay).display,
      }
    : null,
  visibleTerminalContainers: visibleContainers.length,
  scriptCount: document.scripts.length,
};

if (perf.scenario.bodyTextLength === 0 || !perf.scenario.appShellVisible) {
  throw new Error(`Mobile app appears blank: ${JSON.stringify(perf.scenario)}`);
}
