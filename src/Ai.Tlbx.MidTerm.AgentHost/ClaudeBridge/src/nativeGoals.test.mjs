import test from "node:test";
import assert from "node:assert/strict";
import { createSettingsCommandDispatcher } from "./commandDispatch.mjs";
import { NativeGoalCommands, assertGoalRecoverySession, classifyGoalCommand, parseGoalReadback } from "./nativeGoals.mjs";

const emptyGoal = "No goal set. Usage: `/goal <condition>`";
const flush = async () => { await Promise.resolve(); await Promise.resolve(); };
const assistant = (text) => ({ type: "assistant", message: { content: [{ type: "text", text }] } });
const result = (text = "", extra = {}) => ({ type: "result", subtype: "success", is_error: false, result: text, ...extra });

async function fixture(t, interrupt = async () => ({}), options = {}) {
  const inputs = []; const events = [];
  const manager = new NativeGoalCommands({ runtime: () => ({ interrupt }), push: (input) => inputs.push(input),
    sessionId: () => "own-thread", emit: (event) => events.push(event), ...options });
  t.after(() => manager.close());
  const start = (input = inputs.at(-1)) => manager.consume({ type: "command_lifecycle", state: "started", command_uuid: input.uuid });
  const finish = (text, extra) => { manager.consume(assistant(text)); return manager.consume(result(text, extra)); };
  const command = (operation, extra = {}) => manager.control({ operation, requestId: `request-${inputs.length}`,
    expectedThreadId: "own-thread", expectedTurnId: manager.turnId, ...extra });
  manager.setSupported(true); await flush(); start(); finish(emptyGoal); await flush();
  return { manager, inputs, events, start, finish, command };
}

test("native grammar preserves /goal get as an objective, never an invented get verb", () => {
  assert.deepEqual(classifyGoalCommand("/goal"), { operation: "get" });
  assert.deepEqual(classifyGoalCommand("/goal get"), { operation: "set", objective: "get" });
  assert.deepEqual(classifyGoalCommand("/goal CLEAR"), { operation: "clear" });
  assert.equal(classifyGoalCommand("explain /goal clear"), null);
  assert.deepEqual(parseGoalReadback("Goal active: Keep (1 turn)\nLast check: Still waiting", "Keep"),
    { goal: { objective: "Keep", status: "active", iterations: 1, lastReason: "Still waiting" } });
  assert.equal(parseGoalReadback("Goal active: X (1 turn)\nLast check: Y (2 turns)"), null);
});

test("compact replay retains an already consumed local assistant UUID without filtering identical model text", async (t) => {
  const f = await fixture(t);
  f.manager.refresh(); await flush(); f.start();
  const local = { ...assistant(emptyGoal), uuid: "local-readback" };
  assert.equal(f.manager.consume(local), true);
  assert.equal(f.manager.consume(result(emptyGoal)), true);
  await flush();
  f.manager.noteInput({ uuid: "compact-input" }, "/compact", "compact-turn");
  f.manager.consume({ type: "command_lifecycle", state: "started", command_uuid: "compact-input" });
  assert.equal(f.manager.consume(local), true);
  assert.equal(f.manager.consume({ ...assistant(emptyGoal), uuid: "real-model-reply" }), false);
  assert.equal(f.manager.consume(result("", { local_command: "compact" })), false);
});

test("queued and unrelated started UUIDs cannot confirm a Set or leak command text", async (t) => {
  const f = await fixture(t);
  f.command("set", { objective: "Keep waiting" }); await flush();
  const input = f.inputs.at(-1);
  f.manager.consume({ type: "command_lifecycle", command_uuid: input.uuid, state: "queued" });
  f.manager.consume({ type: "command_lifecycle", command_uuid: "sibling", state: "started" });
  assert.equal(f.manager.consume(assistant("Goal set: Keep waiting")), true);
  assert.equal(f.manager.state.goal, null);
  assert.equal(f.manager.state.pendingOperation, "set");
  f.start(); f.manager.consume(assistant("Goal set: Keep waiting"));
  assert.equal(f.manager.state.confirmation, "acknowledged");
  assert.equal(f.events.filter((event) => event.type === "bridge.goal.turn.started").length, 1);
});

test("native max-turn error does not invent Goal completion; local readback confirms actual condition", async (t) => {
  const f = await fixture(t);
  f.command("set", { objective: "Keep waiting" }); await flush(); f.start();
  f.manager.consume(assistant("Goal set: Keep waiting"));
  assert.equal(f.manager.consume(assistant("Waiting.")), false);
  assert.equal(f.manager.consume(result("", { subtype: "error_max_turns", is_error: true })), false);
  assert.equal(f.manager.state.goal.status, "active");
  await flush(); assert.equal(f.inputs.at(-1).message.content, "/goal");
  f.start(); assert.equal(f.finish("Goal active: Keep waiting (1 turn)\nLast check: Pending"), true);
  assert.equal(f.manager.state.confirmation, "confirmed");
  assert.equal(f.manager.state.goal.iterations, 1);
  assert.equal(f.manager.state.goal.lastReason, "Pending");
});

test("Clear acknowledgement alone cannot publish an empty Goal; failed readback retains the native goal", async (t) => {
  const f = await fixture(t);
  f.manager.publish({ goal: { objective: "Keep", status: "active" } });
  f.command("clear"); await flush(); f.start(); f.finish("Goal cleared: Keep");
  assert.equal(f.manager.state.goal.objective, "Keep");
  await flush(); f.start(); f.finish("Goal active: Keep (1 turn)\nLast check: Pending");
  assert.equal(f.manager.state.detail, "clearReadbackMismatch");
  f.command("clear"); await flush(); f.start(); f.finish("Goal cleared: Keep");
  await flush(); f.start(); f.finish(emptyGoal);
  assert.equal(f.manager.state.goal, null);
  assert.equal(f.manager.state.confirmation, "confirmed");
});

test("explicit busy Stop waits for interrupt response AND final result and supersedes an unsent Set", async (t) => {
  let resolveInterrupt; let interruptCount = 0;
  const f = await fixture(t, () => { interruptCount++; return new Promise((resolve) => { resolveInterrupt = resolve; }); });
  f.manager.noteInput({ uuid: "normal" }, "ordinary composer", "own-turn");
  f.start({ uuid: "normal" });
  f.command("set", { objective: "Never enqueue after Stop" });
  f.command("clear", { interruptActiveTurn: true }); await flush();
  assert.equal(interruptCount, 1); assert.equal(f.inputs.length, 1);
  f.manager.consume(result()); await flush();
  assert.equal(f.inputs.length, 1);
  resolveInterrupt({}); await flush();
  assert.equal(f.inputs.at(-1).message.content, "/goal clear");
  assert.equal(f.inputs.some((input) => input.message.content.includes("Never enqueue")), false);
  f.start(); f.finish("No goal set"); await flush(); f.start(); f.finish(emptyGoal);
  assert.equal(f.manager.state.goal, null);
});

test("interrupt rejection preserves the goal and permits repair, without closing or replacing the Query", async (t) => {
  const f = await fixture(t, async () => { throw new Error("interrupt failed"); });
  f.manager.publish({ goal: { objective: "Keep", status: "active" } });
  f.manager.noteInput({ uuid: "normal" }, "ordinary", "own-turn");
  f.command("clear", { interruptActiveTurn: true }); await flush();
  assert.equal(f.inputs.length, 1);
  assert.equal(f.manager.state.goal.objective, "Keep");
  assert.equal(f.manager.state.confirmation, "unconfirmed");
  assert.equal(f.events.some((event) => event.type === "bridge.goal.interrupt.failed"), true);
  assert.equal(f.manager.closed, false);
});

test("a stale turn or thread returns targetUnavailable without submitting another native input", async (t) => {
  const f = await fixture(t);
  f.command("set", { objective: "wrong", expectedThreadId: "sibling" });
  f.command("clear", { expectedTurnId: "ended-turn" }); await flush();
  assert.equal(f.inputs.length, 1);
  assert.equal(f.events.filter((event) => event.status === "targetUnavailable").length, 2);
});

test("a queued mutation is never retargeted after the actual provider session changes", async (t) => {
  const f = await fixture(t);
  f.manager.noteInput({ uuid: "normal" }, "ordinary", "own-turn");
  f.command("set", { objective: "Old thread only" });
  f.manager.sessionId = () => "replacement-thread";
  f.manager.observeSession("replacement-thread"); await flush();
  assert.equal(f.inputs.some((input) => input.message.content === "/goal Old thread only"), false);
  assert.equal(f.inputs.at(-1).session_id, "replacement-thread");
  assert.equal(f.manager.state.goal, null);
  assert.equal(f.manager.state.confirmation, "unknown");
});

test("Stop also interrupts its delivered Set when actual Goal work starts after the first interrupt", async (t) => {
  let interrupts = 0;
  const f = await fixture(t, async () => { interrupts++; return {}; });
  f.command("set", { objective: "Delivered but not started" }); await flush();
  const setInput = f.inputs.at(-1);
  f.command("clear", { interruptActiveTurn: true }); await flush();
  assert.equal(interrupts, 1);
  assert.equal(f.inputs.at(-1), setInput);
  f.start(setInput); f.manager.consume(assistant("Goal set: Delivered but not started")); await flush();
  assert.equal(interrupts, 2);
  await flush(); assert.equal(interrupts, 2);
  assert.equal(f.inputs.at(-1), setInput);
  f.manager.consume(result("", { terminal_reason: "aborted_streaming" })); await flush();
  assert.equal(f.inputs.at(-1).message.content, "/goal clear");
  f.start(); f.finish("Goal cleared: Delivered but not started");
  await flush(); f.start(); f.finish(emptyGoal);
  assert.equal(f.manager.state.goal, null);
});

test("the same Stop guards a manual /goal Set whose model work begins later in the same host turn", async (t) => {
  let interrupts = 0;
  const f = await fixture(t, async () => { interrupts++; return {}; });
  f.manager.noteInput({ uuid: "manual-goal" }, "/goal Keep", "manual-turn");
  f.command("clear", { interruptActiveTurn: true }); await flush();
  assert.equal(interrupts, 1);
  f.start({ uuid: "manual-goal" });
  f.manager.consume(assistant("Goal set: Keep")); await flush();
  assert.equal(f.manager.turnId, "manual-turn");
  assert.equal(interrupts, 2);
  await flush(); assert.equal(interrupts, 2);
});

test("intermediate steering result keeps Goal work and guard until the final native result", async (t) => {
  const f = await fixture(t, undefined, { isIntermediateResult: (message) =>
    Number(message.queued_turn_count ?? 0) > 0 || message.terminal_reason === "aborted_streaming" });
  f.command("set", { objective: "Keep" }); await flush(); f.start();
  f.manager.consume(assistant("Goal set: Keep"));
  const turnId = f.manager.turnId; const count = f.inputs.length;
  assert.equal(f.manager.consume(result("", { terminal_reason: "aborted_streaming" })), false);
  assert.equal(f.manager.turnId, turnId); assert.equal(f.manager.busy, true);
  await flush(); assert.equal(f.inputs.length, count);
  f.manager.consume(result()); await flush();
  assert.equal(f.inputs.at(-1).message.content, "/goal");
  f.start(); f.finish("Goal active: Keep (1 turn)\nLast check: Waiting");
  assert.equal(f.manager.state.goal.status, "active");
});

test("Goal Stop cancels queued steer through the Runtime dispatcher and still verifies Clear", async (t) => {
  let release; const prompts = []; let interrupts = 0;
  const dispatch = createSettingsCommandDispatcher(async (command) => {
    if (command.type === "runtime.configure") await new Promise((resolve) => { release = resolve; });
    else prompts.push(command.turnId);
  });
  let f;
  f = await fixture(t, undefined, { interrupt: async (turnId) => { dispatch.cancelTurn(turnId); interrupts++; },
    isIntermediateResult: (message) => Number(message.queued_turn_count ?? 0) > 0 ||
      (message.terminal_reason === "aborted_streaming" && !f.manager.hasPendingStop(f.manager.turnId)) });
  f.manager.publish({ goal: { objective: "Keep", status: "active" } });
  f.manager.noteInput({ uuid: "normal" }, "normal", "own-turn"); f.start({ uuid: "normal" });
  const configure = dispatch({ type: "runtime.configure" }); await flush();
  const steer = dispatch({ type: "turn.steer", turnId: "own-turn" });
  f.command("clear", { interruptActiveTurn: true }); await flush();
  assert.equal(interrupts, 1);
  release(); await Promise.all([configure, steer]); assert.deepEqual(prompts, []);
  f.manager.consume(result("", { terminal_reason: "aborted_streaming" })); await flush();
  assert.equal(f.inputs.at(-1).message.content, "/goal clear");
  f.start(); f.finish("Goal cleared: Keep"); await flush(); f.start(); f.finish(emptyGoal);
  assert.equal(f.manager.state.goal, null); assert.equal(f.manager.state.confirmation, "confirmed");
});

test("cancelled normal input waiting for Goal readback never submits a late prompt", async (t) => {
  const f = await fixture(t); const prompts = [];
  f.command("get"); await flush();
  const dispatch = createSettingsCommandDispatcher(async (command, cancelled) => {
    if (command.type === "turn.interrupt") return;
    await f.manager.beforeInput();
    if (!cancelled()) prompts.push(command.turnId);
  });
  const start = dispatch({ type: "turn.start", turnId: "waiting" }); await flush();
  await dispatch({ type: "turn.interrupt", turnId: "waiting" });
  f.start(); f.finish(emptyGoal); await start;
  assert.deepEqual(prompts, []);
  await dispatch({ type: "turn.start", turnId: "fresh" });
  assert.deepEqual(prompts, ["fresh"]);
});

test("model text matching the native Set phrase cannot substitute for its missing local acknowledgement", async (t) => {
  const f = await fixture(t);
  f.command("set", { objective: "Keep" }); await flush(); f.start();
  f.manager.consume({ type: "stream_event", event: { type: "message_start" } });
  assert.equal(f.manager.consume(assistant("Goal set: Keep")), false);
  assert.equal(f.manager.state.goal, null);
  assert.notEqual(f.manager.state.confirmation, "acknowledged");
  f.manager.consume(result());
  assert.equal(f.manager.state.confirmation, "unconfirmed");
});


test("lost local result rejects waiters, retains unknown outcome, and never replays a mutation", async (t) => {
  const f = await fixture(t, undefined, { commandTimeoutMs: 5, waitTimeoutMs: 100 });
  f.command("set", { objective: "Lost" }); await flush(); f.start();
  await assert.rejects(f.manager.beforeInput(), { code: "goalInputBlocked" });
  assert.equal(f.manager.state.confirmation, "unconfirmed");
  assert.equal(f.manager.busy, true); assert.ok(f.manager.current);
  await assert.rejects(f.manager.beforeInput(), { code: "goalInputBlocked" });
  assert.equal(f.inputs.filter((input) => input.message.content === "/goal Lost").length, 1);
  f.manager.consume(assistant("Goal set: Lost")); f.manager.consume(result()); await flush();
  f.start(); f.finish("Goal active: Lost (1 turn)\nLast check: Waiting");
  await flush(); await f.manager.beforeInput();
});

test("input waiting has its own bound even during ongoing native Goal inference", async (t) => {
  const f = await fixture(t, undefined, { waitTimeoutMs: 5 });
  f.command("set", { objective: "Working" }); await flush(); f.start();
  f.manager.consume(assistant("Goal set: Working"));
  await assert.rejects(f.manager.beforeInput(), { code: "goalInputBlocked", reason: "timeout" });
  assert.equal(f.manager.waiters.length, 0); assert.equal(f.manager.busy, true);
});

test("out-of-band cancellation releases a Goal wait before any result and unblocks settings", async (t) => {
  const f = await fixture(t); const calls = [];
  f.command("get"); await flush();
  const dispatch = createSettingsCommandDispatcher(async (command, cancelled, signal) => {
    if (command.type === "turn.start") { await f.manager.beforeInput(signal); if (!cancelled()) calls.push("prompt"); }
    else calls.push(command.type);
  });
  const input = dispatch({ type: "turn.start", turnId: "old" }); await flush();
  await dispatch({ type: "turn.interrupt", turnId: "old" }); await input;
  await dispatch({ type: "runtime.configure" });
  assert.deepEqual(calls, ["turn.interrupt", "runtime.configure"]);
  assert.equal(f.manager.waiters.length, 0);
  f.start(); f.finish(emptyGoal); await flush();
  await dispatch({ type: "turn.start", turnId: "fresh" });
  assert.equal(calls.at(-1), "prompt");
});

test("late lifecycle of another queued UUID and an unstarted result cannot take over the current Goal", async (t) => {
  const f = await fixture(t);
  f.command("set", { objective: "Current" }); await flush(); f.start();
  const current = f.manager.current;
  f.manager.noteInput({ uuid: "steer" }, "normal steer", "own-turn");
  f.start({ uuid: "steer" });
  assert.equal(f.manager.current, current);
  current.started = false;
  f.manager.consume(result());
  assert.equal(f.manager.current, current); assert.equal(f.manager.busy, true);
  assert.equal(f.manager.state.confirmation, "unconfirmed");
});

test("explicit recovery creates only a same-thread readback and cannot replay old Set or Clear", async (t) => {
  const f = await fixture(t);
  f.command("set", { objective: "Uncertain" }); await flush();
  f.command("clear", { interruptActiveTurn: true });
  f.manager.close();
  const recovered = await fixture(t);
  assert.equal(recovered.inputs.length, 1);
  assert.equal(recovered.inputs[0].session_id, "own-thread");
  assert.equal(recovered.inputs[0].message.content, "/goal");
  const oldId = f.inputs.at(-1).uuid;
  recovered.start({ uuid: oldId });
  assert.equal(recovered.manager.current, null);
});


test("recovery refuses a different native session instead of retargeting the readback", () => {
  assert.doesNotThrow(() => assertGoalRecoverySession({ recoverGoal: true, resume: "own-thread" }, "own-thread"));
  assert.throws(() => assertGoalRecoverySession({ recoverGoal: true, resume: "own-thread" }, "different"), /different native thread/);
  assert.doesNotThrow(() => assertGoalRecoverySession({ resume: "own-thread" }, "different"));
});


test("a late model result cannot finish a started local readback without its native assistant acknowledgement", async (t) => {
  const f = await fixture(t);
  f.command("get"); await flush(); f.start();
  const current = f.manager.current;
  f.manager.consume(result("old model result"));
  assert.equal(f.manager.current, current); assert.equal(f.manager.busy, true);
  assert.equal(f.manager.state.confirmation, "unconfirmed");
  f.finish(emptyGoal); await flush(); await f.manager.beforeInput();
  assert.equal(f.manager.state.confirmation, "confirmed");
});
