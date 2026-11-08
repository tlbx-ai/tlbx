import test from "node:test";
import assert from "node:assert/strict";
import { waitForResolution, releasePendingToolApprovals, SettledDecisions, resolveScopedCallback } from "./pendingPermissions.mjs";

const scope = { threadId: "own", turnId: "turn", toolName: "Bash" };
const command = (decision = "decline") => ({ requestId: "request", expectedThreadId: "own", expectedTurnId: "turn", decision });
function fixture(kind = "permission", signal) {
  const pending = new Map(); const settled = new SettledDecisions();
  const promise = waitForResolution(pending, "request", signal, kind === "permission" ? "cancel" : null,
    scope, undefined, (value) => settled.remember(kind, "request", scope, value));
  return { pending, settled, promise, resolve: (value, current = scope) => resolveScopedCallback(pending, settled, kind, value, current) };
}

test("late explicit decline after Auto returns actual accept, without invoking the callback twice", async () => {
  const f = fixture(); const events = [];
  releasePendingToolApprovals(f.pending, scope, () => scope, (event) => events.push(event));
  assert.equal(await f.promise, "accept");
  const receipt = f.resolve(command("decline"));
  assert.equal(receipt.status, "alreadySettled"); assert.equal(receipt.value, "accept");
  assert.equal(f.pending.size, 0); assert.equal(events.length, 1);
  assert.equal(f.resolve(command("accept")).value, "accept");
});

test("manual decline wins once over Auto and later accept", async () => {
  const f = fixture(); const events = [];
  assert.equal(f.resolve(command("decline")).status, "applied");
  releasePendingToolApprovals(f.pending, scope, () => scope, (event) => events.push(event));
  assert.equal(await f.promise, "decline");
  assert.equal(f.resolve(command("accept")).value, "decline"); assert.deepEqual(events, []);
});

test("abort is remembered as actual cancellation, never changed to late accept", async () => {
  const signal = new AbortController(); const f = fixture("permission", signal.signal);
  signal.abort(); assert.equal(await f.promise, "cancel");
  assert.equal(f.resolve(command("accept")).status, "alreadySettled");
  assert.equal(f.resolve(command("accept")).value, "cancel");
});

test("unknown and foreign scopes reject without touching a live callback; settled old turn is scoped", async () => {
  const f = fixture();
  assert.equal(f.resolve({ ...command(), requestId: "unknown" }).status, "unconfirmed");
  assert.equal(f.resolve({ ...command(), expectedTurnId: "other" }).status, "unconfirmed");
  assert.equal(f.resolve(command(), { threadId: "other", turnId: "turn" }).status, "unconfirmed");
  assert.equal(f.pending.size, 1);
  f.resolve(command()); await f.promise;
  assert.equal(f.resolve(command(), { threadId: "own", turnId: "new" }).status, "alreadySettled");
  assert.equal(f.resolve({ ...command(), expectedThreadId: "other" }).status, "unconfirmed");
});

test("question retries return original answers; aborted question retains null cancellation", async () => {
  const f = fixture("user_input");
  f.resolve({ ...command(), answers: { q: ["actual"] } }); await f.promise;
  const repeated = f.resolve({ ...command(), answers: { q: ["different"] } });
  assert.equal(repeated.status, "alreadySettled"); assert.deepEqual(repeated.value, { q: ["actual"] });
  repeated.value.q[0] = "mutation";
  assert.deepEqual(f.resolve(command()).value, { q: ["actual"] });
  const signal = new AbortController(); const aborted = fixture("user_input", signal.signal);
  signal.abort(); assert.equal(await aborted.promise, null);
  assert.equal(aborted.resolve(command()).status, "alreadySettled"); assert.equal(aborted.resolve(command()).value, null);
});

test("decision retention is bounded by count and bytes; eviction reports unknown", () => {
  const settled = new SettledDecisions(2, 512);
  for (const id of ["a", "b", "c"]) settled.remember("permission", id, scope, "accept");
  assert.equal(settled.lookup("permission", "a", scope), undefined);
  assert.equal(settled.entries.size, 2);
  settled.remember("user_input", "large", scope, { q: ["x".repeat(1024)] });
  assert.equal(settled.lookup("user_input", "large", scope), undefined);
  assert.ok(settled.bytes <= 512); assert.ok(settled.entries.size <= 2);
});
