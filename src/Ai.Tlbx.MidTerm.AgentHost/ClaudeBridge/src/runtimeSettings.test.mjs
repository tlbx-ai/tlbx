import test from "node:test";
import assert from "node:assert/strict";
import { configureRuntime } from "./runtimeSettings.mjs";

function fixture() {
  const calls = [];
  const runtime = {
    async setModel(value) { calls.push(["model", value]); },
    async applyFlagSettings(value) { calls.push(["flags", value]); },
    async setPermissionMode(value) { calls.push(["permission", value]); },
  };
  return { runtime, calls, state: { permissionMode: "auto", planMode: "off" } };
}

test("model and effort reach SDK controls without a prompt or interrupt", async () => {
  const { runtime, calls, state } = fixture();
  const receipt = await configureRuntime(runtime, { model: "model-B", effort: "high" }, state);
  assert.deepEqual(calls, [["model", "model-B"], ["flags", { effortLevel: "high" }]]);
  assert.deepEqual(receipt, { confirmed: { model: "model-B", effort: "high" }, errors: {} });
  assert.equal(Object.hasOwn(receipt, "effective"), false);
});

test("explicit null resets only selected controls; omission makes no SDK call", async () => {
  const { runtime, calls, state } = fixture();
  await configureRuntime(runtime, {}, state);
  assert.deepEqual(calls, []);
  await configureRuntime(runtime, { model: null, effort: null }, state);
  assert.deepEqual(calls, [["model", undefined], ["flags", { effortLevel: null }]]);
});

test("plan keeps previous permission and restores it when leaving", async () => {
  const { runtime, calls, state } = fixture();
  await configureRuntime(runtime, { planMode: "on" }, state);
  assert.equal(state.permissionMode, "auto");
  await configureRuntime(runtime, { planMode: "off" }, state);
  assert.deepEqual(calls, [["permission", "plan"], ["permission", "bypassPermissions"]]);
});

test("changing permissions inside plan changes restore mode without disabling plan", async () => {
  const { runtime, calls, state } = fixture();
  await configureRuntime(runtime, { planMode: "on" }, state);
  await configureRuntime(runtime, { permissionMode: "manual" }, state);
  await configureRuntime(runtime, { planMode: "off" }, state);
  assert.deepEqual(calls, [["permission", "plan"], ["permission", "plan"], ["permission", "default"]]);
});

test("SDK rejection yields partial receipt and does not overwrite previous permission state", async () => {
  const { runtime, state } = fixture();
  runtime.setPermissionMode = async () => { throw new Error("denied by policy"); };
  runtime.applyFlagSettings = async () => { throw new Error("invalid effort"); };
  const receipt = await configureRuntime(runtime, { model: "B", effort: "bad", planMode: "on" }, state);
  assert.deepEqual(receipt.confirmed, { model: "B" });
  assert.equal(receipt.errors.effort, "invalid effort");
  assert.equal(receipt.errors.planMode, "denied by policy");
  assert.deepEqual(state, { permissionMode: "auto", planMode: "off" });
});

// All runtimes below are local fakes: no SDK process or provider is involved.
import { waitForResolution, releasePendingToolApprovals } from "./pendingPermissions.mjs";

function pendingFixture() {
  const f = fixture(); const pending = new Map(); const events = [];
  let current = { threadId: "own", turnId: "turn" };
  const add = (id, toolName = "Bash", signal, scope = current) =>
    waitForResolution(pending, id, signal, "cancel", { ...scope, toolName });
  const configure = (settings) => configureRuntime(f.runtime, settings, f.state, () =>
    releasePendingToolApprovals(pending, { threadId: "own", turnId: "turn" }, () => current, (event) => events.push(event)));
  return { ...f, pending, events, add, configure, setScope: (scope) => { current = scope; } };
}

test("Auto resolves existing scoped tool callbacks once, only after SDK acknowledgement", async () => {
  const f = pendingFixture(); let acknowledge;
  f.state.permissionMode = "manual";
  f.runtime.setPermissionMode = () => new Promise((resolve) => { acknowledge = resolve; });
  const decision = f.add("tool");
  const applying = f.configure({ permissionMode: "auto" });
  assert.equal(f.pending.size, 1); assert.deepEqual(f.events, []);
  acknowledge(); const receipt = await applying;
  assert.equal(await decision, "accept");
  assert.equal(receipt.confirmed.permissionMode, "auto");
  assert.equal(Object.hasOwn(receipt, "effective"), false);
  assert.deepEqual(f.events, [{ type: "bridge.permission_auto_resolved", session_id: "own", turnId: "turn", requestId: "tool", decision: "accept" }]);
  f.runtime.setPermissionMode = async () => {};
  await f.configure({ permissionMode: "auto" });
  assert.equal(f.events.length, 1);
});

test("questions, ExitPlanMode, foreign scopes and missing metadata are never auto-allowed", async () => {
  const f = pendingFixture();
  f.add("question", "AskUserQuestion"); f.add("plan", "ExitPlanMode");
  f.add("foreign-turn", "Bash", undefined, { threadId: "own", turnId: "other" });
  f.add("foreign-thread", "Bash", undefined, { threadId: "other", turnId: "turn" });
  waitForResolution(f.pending, "unscoped", undefined, "cancel");
  await f.configure({ permissionMode: "auto" });
  assert.equal(f.pending.size, 5); assert.deepEqual(f.events, []);
  for (const resolve of [...f.pending.values()]) resolve("cancel");
});

test("Plan on and failed mode changes leave callbacks waiting; Plan off restores Auto", async () => {
  const f = pendingFixture(); const decision = f.add("tool");
  await f.configure({ planMode: "on" });
  await f.configure({ permissionMode: "auto" });
  assert.equal(f.pending.size, 1); assert.deepEqual(f.events, []);
  const control = f.runtime.setPermissionMode;
  f.runtime.setPermissionMode = async () => { throw new Error("policy rejected"); };
  await f.configure({ planMode: "off" });
  assert.equal(f.state.planMode, "on"); assert.equal(f.pending.size, 1);
  f.runtime.setPermissionMode = control;
  await f.configure({ planMode: "off" });
  assert.equal(await decision, "accept"); assert.equal(f.events.length, 1);
});

test("abort and explicit manual resolution win interleavings without duplicate auto events", async () => {
  const f = pendingFixture(); let acknowledge;
  const controller = new AbortController();
  const aborted = f.add("abort", "Bash", controller.signal);
  const manual = f.add("manual"); const staleResolve = f.pending.get("manual");
  f.runtime.setPermissionMode = () => new Promise((resolve) => { acknowledge = resolve; });
  const applying = f.configure({ permissionMode: "auto" });
  controller.abort(); assert.equal(staleResolve("decline"), true);
  acknowledge(); await applying;
  assert.equal(await aborted, "cancel"); assert.equal(await manual, "decline");
  assert.equal(staleResolve("accept"), false);
  assert.deepEqual(f.events, []); assert.equal(f.pending.size, 0);
});

test("turn/thread changes while SDK control awaits never release a different target", async () => {
  for (const scope of [{ threadId: "own", turnId: null }, { threadId: "own", turnId: "new" }, { threadId: "new", turnId: "turn" }]) {
    const f = pendingFixture(); let acknowledge;
    f.add("old");
    f.runtime.setPermissionMode = () => new Promise((resolve) => { acknowledge = resolve; });
    const applying = f.configure({ permissionMode: "auto" });
    f.setScope(scope); acknowledge(); await applying;
    assert.equal(f.pending.size, 1); assert.deepEqual(f.events, []);
    f.pending.get("old")("cancel");
  }
});

test("Auto settlement wins once against a late explicit callback, and an already aborted signal cannot allow", async () => {
  const f = pendingFixture(); const decision = f.add("auto"); const stale = f.pending.get("auto");
  const aborted = new AbortController(); aborted.abort();
  const cancelled = f.add("already-aborted", "Bash", aborted.signal);
  await f.configure({ permissionMode: "auto" });
  assert.equal(await cancelled, "cancel"); assert.equal(await decision, "accept");
  assert.equal(stale("decline"), false); assert.equal(f.events.length, 1);
});

test("rejected Manual to Auto leaves the existing tool request pending", async () => {
  const f = pendingFixture(); f.state.permissionMode = "manual"; f.add("tool");
  f.runtime.setPermissionMode = async () => { throw new Error("permission denied"); };
  const receipt = await f.configure({ permissionMode: "auto" });
  assert.equal(receipt.errors.permissionMode, "permission denied");
  assert.equal(f.state.permissionMode, "manual");
  assert.equal(f.pending.size, 1); assert.deepEqual(f.events, []);
  f.pending.get("tool")("cancel");
});

test("abort reports canonical cancellation once and removes the pending callback before Auto can see it", async () => {
  const pending = new Map(); const controller = new AbortController(); const events = [];
  const scope = { threadId: "own", turnId: "turn", toolName: "Bash" };
  const decision = waitForResolution(pending, "tool", controller.signal, "cancel", scope,
    () => events.push("cancel"));
  controller.abort(); controller.abort();
  releasePendingToolApprovals(pending, scope, () => scope, () => events.push("accept"));
  assert.equal(await decision, "cancel"); assert.deepEqual(events, ["cancel"]);
});
