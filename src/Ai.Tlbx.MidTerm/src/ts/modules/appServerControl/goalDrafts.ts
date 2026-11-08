// Independent from composer/session input: never submitted or replayed automatically.
export interface GoalDraft {
  revision?: string;
  objective: string;
  status: string;
  budget: string;
}
const prefix = 'tlbx.agentGoalDraft.v1:';
const memory = new Map<string, GoalDraft>();
function key(sessionId: string, threadId: string): string {
  return `${prefix}${encodeURIComponent(sessionId)}:${encodeURIComponent(threadId)}`;
}
export function readGoalDraft(sessionId: string, threadId: string): GoalDraft | null {
  if (!threadId || threadId === sessionId) return null;
  const id = key(sessionId, threadId);
  const cached = memory.get(id);
  if (cached) return { ...cached };
  try {
    if (typeof localStorage === 'undefined') return null;
    const value: unknown = JSON.parse(localStorage.getItem(id) ?? 'null');
    if (!value || typeof value !== 'object') return null;
    const draft = value as GoalDraft;
    if ([draft.objective, draft.status, draft.budget].some((part) => typeof part !== 'string'))
      return null;
    memory.set(id, draft);
    return { ...draft };
  } catch {
    return null;
  }
}
export function writeGoalDraft(sessionId: string, threadId: string, draft: GoalDraft): void {
  if (!threadId || threadId === sessionId) return;
  const id = key(sessionId, threadId);
  const versioned = { ...draft, revision: crypto.randomUUID() };
  memory.set(id, versioned);
  try {
    if (typeof localStorage !== 'undefined') localStorage.setItem(id, JSON.stringify(versioned));
  } catch {
    /* Keep the session draft when storage is unavailable or full. */
  }
}
export function clearGoalDraft(
  sessionId: string,
  threadId: string,
  sent: GoalDraft | null,
): boolean {
  if (!sent || JSON.stringify(readGoalDraft(sessionId, threadId)) !== JSON.stringify(sent))
    return false;
  const id = key(sessionId, threadId);
  memory.delete(id);
  try {
    if (typeof localStorage !== 'undefined') localStorage.removeItem(id);
  } catch {
    /* Provider confirmation still stands; storage failures never replay a goal. */
  }
  return true;
}
