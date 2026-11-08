import { randomUUID } from "node:crypto";

const clearWords = new Set(["clear", "stop", "off", "reset", "none", "cancel"]);

export function classifyGoalCommand(text) {
  const match = /^\/goal(?:\s+([\s\S]*))?$/.exec(text.trim());
  if (!match) return null;
  const objective = (match[1] ?? "").trim();
  if (!objective) return { operation: "get" };
  return clearWords.has(objective.toLowerCase()) ? { operation: "clear" } : { operation: "set", objective };
}

export function parseGoalReadback(text, expectedObjective) {
  if (text === "No goal set. Usage: `/goal <condition>`") return { goal: null };
  const prefix = "Goal active: ";
  if (!text.startsWith(prefix)) return null;
  const marker = / \((not yet evaluated|[0-9]+ turns?)\)(?=\nLast check:|$)/g;
  const all = [...text.matchAll(marker)];
  const exact = expectedObjective ? all.filter((match) => text.slice(prefix.length, match.index) === expectedObjective) : [];
  const candidates = exact.length ? exact : all;
  if (candidates.length !== 1) return null;
  const match = candidates[0];
  const rest = text.slice(match.index + match[0].length);
  return { goal: { objective: text.slice(prefix.length, match.index), status: "active",
    iterations: match[1] === "not yet evaluated" ? 0 : Number.parseInt(match[1], 10),
    lastReason: rest.startsWith("\nLast check: ") ? rest.slice(13) : null } };
}

function assistantText(message) {
  return (message.message?.content ?? []).filter((part) => part.type === "text").map((part) => part.text).join("\n");
}

// Only our UUID-correlated native local commands are parsed. Normal model text
// never establishes goal state. No control RPC, remote-mode flag or TUI scraping.
export class NativeGoalCommands {
  constructor({ runtime, push, sessionId, emit, interrupt, isIntermediateResult = (message) => Number(message.queued_turn_count ?? 0) > 0 }) {
    this.interrupt = interrupt ?? (() => runtime().interrupt());
    this.isIntermediateResult = isIntermediateResult;
    this.runtime = runtime; this.push = push; this.sessionId = sessionId; this.emit = emit;
    this.supported = false; this.closed = false; this.frames = new Map(); this.queue = []; this.current = null; this.busy = false;
    this.turnId = null; this.waiters = []; this.stopping = null;
    this.state = { threadId: "", confirmation: "unknown", scope: "thread", goal: null, pendingOperation: null };
  }

  observeSession(threadId) {
    if (!threadId || !this.state.threadId || this.state.threadId === threadId) return;
    this.clearFrames(); this.current = null; this.queue = []; this.busy = false; this.turnId = null;
    this.publish({ goal: null, confirmation: "unknown", pendingOperation: null, detail: "targetUnavailable" });
    this.refresh();
  }

  publish(patch) {
    this.state = { ...this.state, ...patch, threadId: this.sessionId() };
    this.emit({ type: "bridge.goal.changed", session_id: this.sessionId(), state: this.state });
  }

  setSupported(value) {
    this.supported = value;
    if (value) this.refresh();
    else this.publish({ confirmation: "unconfirmed", detail: "Native /goal was not advertised by this CLI." });
  }

  refresh(preserveError = false) {
    if (!this.supported || this.closed) return;
    const waiting = this.queue.find((entry) => entry.operation === "get");
    if (waiting) { waiting.preserveError ||= preserveError; return; }
    this.queue.push({ operation: "get", automatic: true, preserveError });
    this.schedule();
  }

  control(command) {
    if (!["set", "get", "clear"].includes(command.operation)) throw new Error("Unknown goal operation.");
    if (!this.supported) throw new Error("Native /goal was not advertised by this CLI.");
    if (command.expectedThreadId !== this.sessionId() || command.expectedTurnId !== this.turnId) {
      this.emit({ type: "bridge.goal.receipt", requestId: command.requestId, status: "targetUnavailable" });
      return;
    }
    if (command.operation === "set" && (!command.objective?.trim() || command.objective.length > 4000 || clearWords.has(command.objective.trim().toLowerCase())))
      throw new Error("Goal condition is empty, too long, or a reserved native clear command.");
    if (command.operation === "clear") this.discardWaitingMutations();
    const entry = { operation: command.operation, objective: command.objective, requestId: command.requestId, threadId: command.expectedThreadId, interruptActiveTurn: command.interruptActiveTurn };
    if (command.operation === "clear") this.queue.unshift(entry); else this.queue.push(entry);
    this.publish({ pendingOperation: command.operation, detail: this.busy ? "waitingForTurn" : null });
    this.emit({ type: "bridge.goal.receipt", requestId: command.requestId, status: "pending", state: this.state });
    this.interruptPendingClear();
    this.schedule();
  }

  discardWaitingMutations() {
    this.queue = this.queue.filter((entry) => entry.operation === "get");
  }

  hasPendingStop(turnId) {
    return !!turnId && this.queue.some((entry) => entry.operation === "clear" &&
      entry.interruptActiveTurn && entry.interruptedTurnId === turnId);
  }

  currentGoalWorkId() {
    return this.current?.operation === "set" && this.current.work ? this.current.uuid : null;
  }

  interruptPendingClear() {
    const entry = this.queue.find((value) => value.operation === "clear" && value.interruptActiveTurn);
    if (!entry || !this.busy || this.stopping) return;
    if (entry.interruptedTurnId !== this.turnId || entry.interruptedWorkId !== this.currentGoalWorkId())
      this.interruptForClear(entry);
  }

  interruptForClear(entry) {
    if (this.stopping) return;
    this.stopping = entry;
    const turnId = this.turnId;
    entry.interruptedTurnId = turnId;
    entry.interruptedWorkId = this.currentGoalWorkId();
    this.emit({ type: "bridge.goal.interrupting", turnId: this.turnId });
    void this.interrupt(turnId).then(() => {
      if (this.stopping !== entry) return;
      this.stopping = null;
      this.interruptPendingClear();
      this.schedule();
    }).catch((error) => {
      this.stopping = null;
      this.emit({ type: "bridge.goal.interrupt.failed", turnId });
      this.queue = this.queue.filter((value) => value !== entry);
      this.publish({ pendingOperation: null, confirmation: "unconfirmed", detail: String(error.message) });
      this.schedule();
    });
  }

  async beforeInput() {
    if (this.closed) throw new Error("Claude goal adapter is closed.");
    if (!this.current?.internal && !this.queue.length) return;
    await new Promise((resolve) => this.waiters.push(resolve));
    if (this.closed) throw new Error("Claude goal adapter is closed.");
  }

  noteInput(message, prompt, turnId) {
    const goal = classifyGoalCommand(prompt);
    this.frames.set(message.uuid, { ...goal, internal: false, uuid: message.uuid, turnId });
    this.busy = true; this.turnId = turnId;
  }

  schedule() { queueMicrotask(() => this.drain()); }

  drain() {
    if (this.closed || this.busy || this.current || this.stopping) return;
    const entry = this.queue.shift();
    if (!entry) {
      for (const resolve of this.waiters.splice(0)) resolve();
      return;
    }
    if (entry.threadId && entry.threadId !== this.sessionId()) {
      this.publish({ confirmation: "unconfirmed", pendingOperation: null, goal: null, detail: "targetUnavailable" });
      this.schedule(); return;
    }
    const uuid = randomUUID();
    const frame = { ...entry, threadId: this.sessionId(), internal: true, uuid, started: false, ack: false, text: "", work: false };
    this.frames.set(uuid, frame); this.current = frame; this.busy = true;
    frame.timer = setTimeout(() => {
      this.publish({ confirmation: "unconfirmed", pendingOperation: null, detail: "timeout" });
      // A late native result can still arrive. Do not replay, retarget, or declare idle.
    }, 30_000);
    frame.timer.unref?.();
    this.publish({ pendingOperation: entry.operation, detail: null });
    const content = entry.operation === "get" ? "/goal" : entry.operation === "clear" ? "/goal clear" : `/goal ${entry.objective}`;
    this.push({ type: "user", uuid, session_id: this.sessionId(), parent_tool_use_id: null, message: { role: "user", content } });
  }

  consume(message) {
    if (message.type === "command_lifecycle") {
      if (message.state === "started") {
        const frame = this.frames.get(message.command_uuid);
        if (frame) { this.current = frame; frame.started = true; }
      }
      return !!this.frames.get(message.command_uuid)?.operation;
    }
    const frame = this.current;
    if (message.type === "system" && message.subtype === "init") return false;
    if (!frame?.operation) return this.normalMessage(message);
    if (!frame.started) {
      // Without the observed UUID lifecycle, local command text proves nothing.
      if (message.type === "result") {
        this.publish({ confirmation: "unconfirmed", pendingOperation: null, detail: "Native goal lifecycle missing." });
        clearTimeout(frame.timer); this.frames.delete(frame.uuid); this.current = null;
        this.busy = Number(message.queued_turn_count ?? 0) > 0;
        if (!this.busy) this.turnId = null;
        this.schedule();
      }
      return frame.internal;
    }
    if (message.type === "assistant") return this.goalAssistant(frame, message);
    if (message.type === "result") return this.goalResult(frame, message);
    if (message.type === "stream_event" && frame.operation === "set") {
      if (message.event?.type === "message_start") this.startGoalWork(frame);
      return !frame.work;
    }
    return frame.operation !== "set" || !frame.work;
  }

  startGoalWork(frame) {
    if (frame.work) return;
    frame.work = true; clearTimeout(frame.timer);
    if (frame.internal) {
      frame.turnId = `goal-${frame.uuid}`; this.turnId = frame.turnId;
      this.emit({ type: "bridge.goal.turn.started", turnId: frame.turnId });
    }
    // A delivered Set can start model work after Stop first interrupted an idle
    // input queue. The same explicit Stop covers that newly observed owned turn.
    this.interruptPendingClear();
  }

  goalAssistant(frame, message) {
    const text = assistantText(message);
    if (frame.operation === "set" && !frame.work && text === `Goal set: ${frame.objective}`) {
      frame.ack = true;
      this.publish({ confirmation: "acknowledged", goal: { objective: frame.objective, status: "active" }, detail: null });
      this.startGoalWork(frame);
      return true;
    }
    if (frame.operation !== "set" || !frame.work) { frame.text = text; return true; }
    return false;
  }

  goalResult(frame, message) {
    if (frame.work && this.isIntermediateResult(message)) return false;
    clearTimeout(frame.timer);
    if (frame.operation === "get") this.finishReadback(frame, message);
    else if (frame.operation === "clear") this.finishClear(frame, message);
    else if (!frame.ack) this.publish({ confirmation: "unconfirmed", pendingOperation: null, detail: frame.text || "Native goal acknowledgement missing." });
    this.frames.delete(frame.uuid); this.current = null;
    const queued = Number(message.queued_turn_count ?? 0) > 0;
    this.busy = queued; if (!queued) { this.turnId = null; this.clearFrames(); }
    if (frame.operation === "set") this.refresh(!frame.ack);
    this.schedule();
    if (!frame.internal && (frame.operation !== "set" || !frame.work)) {
      this.emit({ ...message, result: "" }); return true;
    }
    return !(frame.operation === "set" && frame.work);
  }

  finishReadback(frame, message) {
    const parsed = message.is_error ? null : parseGoalReadback(frame.text || message.result || "", this.state.goal?.objective);
    if (!parsed) {
      this.publish({ confirmation: "unconfirmed", pendingOperation: null, detail: frame.text || message.result || "Native goal readback failed." });
      return;
    }
    const clearFailed = frame.afterClear && parsed.goal !== null;
    this.publish({ ...parsed, confirmation: "confirmed", pendingOperation: this.queue.find((entry) => !entry.automatic)?.operation ?? null,
      detail: clearFailed ? "clearReadbackMismatch" : frame.preserveError ? this.state.detail : null });
  }

  finishClear(frame, message) {
    const text = frame.text || message.result || "";
    if (message.is_error || !(text.startsWith("Goal cleared: ") || text === "No goal set")) {
      this.publish({ confirmation: "unconfirmed", pendingOperation: null, detail: text || "Native clear acknowledgement missing." });
      return;
    }
    this.queue = this.queue.filter((entry) => entry.operation !== "get");
    this.queue.unshift({ operation: "get", automatic: true, afterClear: true });
  }

  clearFrames() {
    for (const frame of this.frames.values()) clearTimeout(frame.timer);
    this.frames.clear();
  }

  close() {
    this.closed = true; this.clearFrames(); this.queue = [];
    for (const resolve of this.waiters.splice(0)) resolve();
  }

  normalMessage(message) {
    if (!this.busy && this.state.goal && message.type === "stream_event" && message.event?.type === "message_start") {
      this.busy = true; this.turnId = `goal-native-${randomUUID()}`;
      this.emit({ type: "bridge.goal.turn.started", turnId: this.turnId });
      this.interruptPendingClear();
    }
    if (message.type !== "result") return false;
    if (this.isIntermediateResult(message)) return false;
    this.clearFrames();
    this.busy = false; this.current = null; this.turnId = null;
    this.refresh();
    return false;
  }
}
