// Keep the existing resolve-function maps, with scoped metadata only for tool approvals.
export function waitForResolution(map, requestId, signal, fallback, scope, onAbort) {
  return new Promise((resolve) => {
    let settled = false;
    const complete = (value) => {
      if (settled) return false;
      settled = true;
      if (map.get(requestId) === complete) map.delete(requestId);
      signal?.removeEventListener("abort", abort);
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
