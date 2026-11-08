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
