import test from "node:test";
import assert from "node:assert/strict";
import { NativeGoalCommands, classifyGoalCommand, parseGoalReadback } from "./nativeGoals.mjs";

const emptyGoal = "No goal set. Usage: `/goal <condition>`";
const flush = async () => { await Promise.resolve(); await Promise.resolve(); };
const assistant = (text) => ({ type: "assistant", message: { content: [{ type: "text", text }] } });
const result = (text = "", extra = {}) => ({ type: "result", subtype: "success", is_error: false, result: text, ...extra });

async function fixture(t, interrupt = async () => ({})) {
  const inputs = []; const events = [];
  const manager = new NativeGoalCommands({ runtime: () => ({ interrupt }), push: (input) => inputs.push(input),
    sessionId: () => "own-thread", emit: (event) => events.push(event) });
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
