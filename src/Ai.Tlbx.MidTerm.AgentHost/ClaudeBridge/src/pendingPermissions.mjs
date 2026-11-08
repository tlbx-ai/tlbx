// Keep the existing resolve-function maps, with scoped metadata only for tool approvals.
export function waitForResolution(map, requestId, signal, fallback, scope, onAbort, onSettled) {
  return new Promise((resolve) => {
    let settled = false;
    const complete = (value) => {
      if (settled) return false;
      settled = true;
      if (map.get(requestId) === complete) map.delete(requestId);
      signal?.removeEventListener("abort", abort);
      onSettled?.(value);
      resolve(value);
      return true;
    };
    const abort = () => { if (complete(fallback)) onAbort?.(); };
    complete.scope = scope;
    map.set(requestId, complete);
    if (signal?.aborted) abort();
    else signal?.addEventListener("abort", abort, { once: true });
  });
}

export function releasePendingToolApprovals(map, expected, currentScope, emit) {
  const current = currentScope();
  if (current.threadId !== expected.threadId || current.turnId !== expected.turnId) return;
  for (const [requestId, complete] of map) {
    const scope = complete.scope;
    if (!scope || scope.threadId !== expected.threadId || scope.turnId !== expected.turnId) continue;
    if (!scope.turnId || !scope.toolName || scope.toolName === "AskUserQuestion" || scope.toolName === "ExitPlanMode") continue;
    if (!complete("accept")) continue;
    emit({ type: "bridge.permission_auto_resolved", session_id: scope.threadId,
      turnId: scope.turnId, requestId, decision: "accept" });
  }
}

// Only recent callback decisions are retained. Large answers are not cached: a
// later retry is unconfirmed rather than an invented answer or an unbounded log.
export class SettledDecisions {
  constructor(maxEntries = 128, maxBytes = 256 * 1024) {
    this.entries = new Map(); this.bytes = 0;
    this.maxEntries = maxEntries; this.maxBytes = maxBytes;
  }
  remember(kind, requestId, scope, value) {
    const key = `${kind}:${requestId}`;
    const previous = this.entries.get(key);
    if (previous) { this.bytes -= previous.bytes; this.entries.delete(key); }
    const entry = { scope: { ...scope }, value };
    const bytes = Buffer.byteLength(JSON.stringify(entry), "utf8") + Buffer.byteLength(key, "utf8");
    if (bytes > this.maxBytes) return;
    this.entries.set(key, { scope: entry.scope, value: structuredClone(value), bytes }); this.bytes += bytes;
    while (this.entries.size > this.maxEntries || this.bytes > this.maxBytes) {
      const oldest = this.entries.keys().next().value;
      this.bytes -= this.entries.get(oldest).bytes; this.entries.delete(oldest);
    }
  }
  lookup(kind, requestId, scope) {
    const entry = this.entries.get(`${kind}:${requestId}`);
    return entry && sameScope(entry.scope, scope) ? structuredClone(entry.value) : undefined;
  }
}

function sameScope(left, right) {
  return left?.threadId === right?.threadId && left?.turnId === right?.turnId;
}

export function resolveScopedCallback(map, settled, kind, command, currentScope) {
  const expected = { threadId: command.expectedThreadId, turnId: command.expectedTurnId };
  if (!expected.threadId || !expected.turnId) return { status: "unconfirmed", ...expected };
  if (kind === "permission" && !["accept", "decline", "cancel"].includes(command.decision))
    return { status: "unconfirmed", ...expected };
  const complete = map.get(command.requestId);
  if (complete && sameScope(complete.scope, expected) && sameScope(currentScope, expected)) {
    const value = kind === "permission" ? command.decision : command.answers ?? {};
    if (complete(value)) return { status: "applied", value, ...expected };
  }
  const actual = settled.lookup(kind, command.requestId, expected);
  if (actual !== undefined && currentScope.threadId === expected.threadId)
    return { status: "alreadySettled", value: actual, ...expected };
  return { status: "unconfirmed", ...expected };
}
