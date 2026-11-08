import test from "node:test";
import assert from "node:assert/strict";
import { createSettingsCommandDispatcher } from "./commandDispatch.mjs";
import { configureRuntime } from "./runtimeSettings.mjs";

function deferred() {
  let resolve; let reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const tick = async () => { await Promise.resolve(); await Promise.resolve(); };

test("deferred turn settings finish before new configure acknowledgement, never overwrite it afterwards", async () => {
  const gate = deferred(); const calls = []; const sdk = {}; const state = { planMode: "off", permissionMode: "manual" };
  const runtime = {
    async setModel(value) { calls.push(`model:${value}`); if (value === "old") await gate.promise; sdk.model = value; },
    async applyFlagSettings(value) { sdk.effort = value.effortLevel; calls.push(`effort:${value.effortLevel}`); },
    async setPermissionMode(value) { sdk.permission = value; calls.push(`permission:${value}`); },
  };
  const dispatch = createSettingsCommandDispatcher(async (command) => {
    if (command.type === "turn.start" || command.type === "turn.steer") {
      await runtime.setModel("old"); await runtime.applyFlagSettings({ effortLevel: "low" });
      await runtime.setPermissionMode("default"); calls.push("old-complete"); return;
    }
    if (command.type === "runtime.configure") {
      await configureRuntime(runtime, { model: "new", effort: "high", permissionMode: "auto" }, state);
      calls.push("new-ack"); return;
    }
    calls.push(command.type);
  });
  const start = dispatch({ type: "turn.start" }); await tick();
  const configure = dispatch({ type: "runtime.configure" });
  for (const type of ["permission.resolve", "user_input.resolve", "turn.interrupt", "shutdown"])
    await dispatch({ type });
  assert.equal(calls.includes("model:new"), false);
  assert.equal(calls.includes("permission.resolve"), true);
  assert.equal(calls.includes("shutdown"), true);
  gate.resolve(); await Promise.all([start, configure]);
  assert.ok(calls.indexOf("old-complete") < calls.indexOf("new-ack"));
  assert.deepEqual(sdk, { model: "new", effort: "high", permission: "bypassPermissions" });
  await tick(); assert.equal(calls.at(-1), "new-ack");
});

test("failed settings command rejects its caller but does not poison its successor", async () => {
  const gate = deferred(); const calls = [];
  const dispatch = createSettingsCommandDispatcher(async (command) => {
    calls.push(command.type);
    if (command.type === "turn.steer") await gate.promise;
  });
  const failing = dispatch({ type: "turn.steer" });
  const rejected = assert.rejects(failing, /SDK rejected/);
  const next = dispatch({ type: "runtime.configure" });
  await tick(); assert.deepEqual(calls, ["turn.steer"]);
  gate.reject(new Error("SDK rejected")); await rejected; await next;
  assert.deepEqual(calls, ["turn.steer", "runtime.configure"]);
});

test("start, steer and configure share input order", async () => {
  const calls = [];
  const dispatch = createSettingsCommandDispatcher(async (command) => { await tick(); calls.push(command.type); });
  await Promise.all(["turn.start", "turn.steer", "runtime.configure"].map((type) => dispatch({ type })));
  assert.deepEqual(calls, ["turn.start", "turn.steer", "runtime.configure"]);
});

for (const type of ["turn.start", "turn.steer"]) {
  test(`interrupt discards queued ${type} behind configure; later fresh turn still runs`, async () => {
    const configure = deferred(); const prompts = []; let interrupts = 0;
    const dispatch = createSettingsCommandDispatcher(async (command) => {
      if (command.type === "runtime.configure") await configure.promise;
      else if (command.type === "turn.interrupt") interrupts++;
      else prompts.push(command.turnId);
    });
    const applying = dispatch({ type: "runtime.configure" }); await tick();
    const old = dispatch({ type, turnId: "old" });
    await dispatch({ type: "turn.interrupt", turnId: "old" });
    assert.equal(interrupts, 1); assert.deepEqual(prompts, []);
    const fresh = dispatch({ type: "turn.start", turnId: "fresh" });
    configure.resolve(); await Promise.all([applying, old, fresh]);
    assert.deepEqual(prompts, ["fresh"]);
  });

  test(`interrupt prevents late prompt after ${type} SDK await, without cancelling future turns`, async () => {
    const control = deferred(); const prompts = []; let interrupts = 0;
    const dispatch = createSettingsCommandDispatcher(async (command, isCancelled) => {
      if (command.type === "turn.interrupt") { interrupts++; return; }
      if (command.turnId === "old") await control.promise;
      if (isCancelled()) return;
      prompts.push(command.turnId);
    });
    const old = dispatch({ type, turnId: "old" }); await tick();
    await dispatch({ type: "turn.interrupt", turnId: "old" });
    assert.equal(interrupts, 1); assert.deepEqual(prompts, []);
    const fresh = dispatch({ type: "turn.start", turnId: "fresh" });
    control.resolve(); await Promise.all([old, fresh]);
    assert.deepEqual(prompts, ["fresh"]);
  });
}

test("interrupting another turn does not cancel current queued input", async () => {
  const control = deferred(); const prompts = [];
  const dispatch = createSettingsCommandDispatcher(async (command, isCancelled) => {
    if (command.type === "turn.interrupt") return;
    await control.promise;
    if (!isCancelled()) prompts.push(command.turnId);
  });
  const current = dispatch({ type: "turn.start", turnId: "current" }); await tick();
  await dispatch({ type: "turn.interrupt", turnId: "other" });
  control.resolve(); await current;
  assert.deepEqual(prompts, ["current"]);
});

test("SDK rejection after scoped Stop cannot fail the following fresh turn", async () => {
  const control = deferred(); const prompts = [];
  const dispatch = createSettingsCommandDispatcher(async (command) => {
    if (command.type === "turn.interrupt") return;
    if (command.turnId === "old") await control.promise;
    prompts.push(command.turnId);
  });
  const old = dispatch({ type: "turn.start", turnId: "old" }); await tick();
  await dispatch({ type: "turn.interrupt", turnId: "old" });
  const fresh = dispatch({ type: "turn.start", turnId: "fresh" });
  control.reject(new Error("SDK control cancelled after Stop"));
  await Promise.all([old, fresh]);
  assert.deepEqual(prompts, ["fresh"]);
});
