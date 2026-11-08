import { NativeGoalCommands, assertGoalRecoverySession } from "./nativeGoals.mjs";
import { createSettingsCommandDispatcher } from "./commandDispatch.mjs";
import { waitForResolution, releasePendingToolApprovals, SettledDecisions, resolveScopedCallback } from "./pendingPermissions.mjs";
import { configureRuntime } from "./runtimeSettings.mjs";
import { query, getSessionMessages } from "@anthropic-ai/claude-agent-sdk";
import { readFile } from "node:fs/promises";
import { createInterface } from "node:readline";
import { randomUUID } from "node:crypto";

class AsyncMessageQueue {
  #values = [];
  #waiters = [];
  #closed = false;

  push(value) {
    if (this.#closed) throw new Error("Claude prompt queue is closed.");
    const waiter = this.#waiters.shift();
    if (waiter) waiter({ value, done: false });
    else this.#values.push(value);
  }

  close() {
    this.#closed = true;
    for (const waiter of this.#waiters.splice(0))
      waiter({ value: undefined, done: true });
  }

  [Symbol.asyncIterator]() {
    return {
      next: () => {
        const value = this.#values.shift();
        if (value !== undefined) return Promise.resolve({ value, done: false });
        if (this.#closed)
          return Promise.resolve({ value: undefined, done: true });
        return new Promise((resolve) => this.#waiters.push(resolve));
      },
    };
  }
}

const prompts = new AsyncMessageQueue();
const pendingPermissions = new Map();
const pendingUserInputs = new Map();
const settledDecisions = new SettledDecisions();
let configuration;
let runtime;
let runtimeTask;
let currentSessionId;
let activeTurnId;
let steeringPending = false;
const goals = new NativeGoalCommands({
  interrupt: (turnId) => { dispatch.cancelTurn(turnId); return runtime.interrupt(); },
  isIntermediateResult,
  runtime: () => runtime, push: (value) => prompts.push(value),
  sessionId: () => currentSessionId ?? configuration?.resume ?? configuration?.sessionId,
  emit: (event) => {
    if (event.type === "bridge.goal.turn.started") { activeTurnId = event.turnId; steeringPending = false; }
    forwardProviderMessage(event);
  } });
const confirmedSettings = { permissionMode: "manual", planMode: "off" };

function emit(value) {
  process.stdout.write(`${JSON.stringify(value)}\n`);
}

function isIntermediateResult(message) {
  return Number(message.queued_turn_count ?? 0) > 0 ||
    (steeringPending && !goals.hasPendingStop(activeTurnId) &&
      ["aborted_streaming", "aborted_tools"].includes(message.terminal_reason));
}

function forwardProviderMessage(message) {
  if (message.type === "result" && !isIntermediateResult(message)) {
    activeTurnId = null;
    steeringPending = false;
  }
  emit(message);
}

function fail(message, detail, context = {}) {
  emit({
    type: "bridge.error",
    message,
    detail: detail instanceof Error ? detail.stack : String(detail ?? ""),
    ...context,
  });
}

function permissionMode(value, planMode) {
  if (planMode === "plan") return "plan";
  return value === "auto" ? "bypassPermissions" : "default";
}

const supportedImageMimeTypes = new Set([
  "image/gif",
  "image/jpeg",
  "image/png",
  "image/webp",
]);

async function makeUserMessage(command) {
  const content = [];
  if (typeof command.prompt === "string" && command.prompt.length > 0) {
    content.push({ type: "text", text: command.prompt });
  }
  for (const attachment of command.attachments ?? []) {
    if (attachment.kind !== "image") continue;
    if (!supportedImageMimeTypes.has(attachment.mimeType)) {
      throw new Error(
        `Unsupported Claude image attachment type '${attachment.mimeType}'.`,
      );
    }
    const bytes = await readFile(attachment.path);
    content.push({
      type: "image",
      source: {
        type: "base64",
        media_type: attachment.mimeType,
        data: bytes.toString("base64"),
      },
    });
  }
  return {
    type: "user",
    uuid: randomUUID(),
    session_id: currentSessionId ?? configuration.sessionId,
    parent_tool_use_id: null,
    ...(command.type === "turn.steer" ? { priority: "now" } : {}),
    message: { role: "user", content },
  };
}

async function canUseTool(toolName, toolInput, options) {
  if (toolName === "ExitPlanMode") {
    const input = toolInput?.type === "record" ? toolInput.value : toolInput;
    if (typeof input?.plan === "string" && input.plan.trim()) {
      emit({
        type: "bridge.proposed_plan",
        turnId: activeTurnId,
        toolUseId: options.toolUseID,
        markdown: input.plan,
      });
    }
    return {
      behavior: "deny",
      message:
        "Stop here and wait for the user's feedback or implementation request in a later turn.",
      toolUseID: options.toolUseID,
    };
  }
  const requestId = `claude-request-${randomUUID()}`;
  const scope = { threadId: currentSessionId ?? configuration.resume ?? configuration.sessionId,
    turnId: activeTurnId, toolName };
  if (toolName === "AskUserQuestion") {
    const questions = Array.isArray(toolInput.questions)
      ? toolInput.questions.map((question, index) => ({
          id:
            typeof question.question === "string" && question.question
              ? question.question
              : `q-${index}`,
          header:
            typeof question.header === "string"
              ? question.header
              : `Question ${index + 1}`,
          question:
            typeof question.question === "string" ? question.question : "",
          options: Array.isArray(question.options)
            ? question.options.map((option) => ({
                label: typeof option.label === "string" ? option.label : "",
                description:
                  typeof option.description === "string"
                    ? option.description
                    : "",
              }))
            : [],
          multiSelect: question.multiSelect === true,
        }))
      : [];
    emit({
      type: "bridge.user_input_request",
      requestId,
      turnId: activeTurnId,
      questions,
    });
    const answers = await waitForResolution(
      pendingUserInputs,
      requestId,
      options.signal,
      null,
      scope,
      () => emit({ type: "bridge.user_input_cancelled", session_id: scope.threadId,
        turnId: scope.turnId, requestId, decision: "cancel" }),
      (value) => settledDecisions.remember("user_input", requestId, scope, value),
    );
    if (answers === null || options.signal.aborted)
      return { behavior: "deny", message: "User cancelled the question." };
    const sdkAnswers = Object.fromEntries(
      Object.entries(answers).map(([question, values]) => [
        question,
        values.join(", "),
      ]),
    );
    return {
      behavior: "allow",
      updatedInput: { ...toolInput, answers: sdkAnswers },
    };
  }

  emit({
    type: "bridge.permission_request",
    requestId,
    turnId: activeTurnId,
    toolName,
    toolUseId: options.toolUseID,
    input: toolInput,
  });
  const decision = await waitForResolution(
    pendingPermissions,
    requestId,
    options.signal,
    "cancel",
    scope,
    () => emit({ type: "bridge.permission_cancelled", session_id: scope.threadId,
      turnId: scope.turnId, requestId, decision: "cancel" }),
    (value) => settledDecisions.remember("permission", requestId, scope, value),
  );
  if (decision === "accept" && !options.signal.aborted)
    return { behavior: "allow", updatedInput: toolInput };
  return {
    behavior: "deny",
    message:
      decision === "cancel" || options.signal.aborted
        ? "User cancelled tool execution."
        : "User declined tool execution.",
  };
}

async function startRuntime(command) {
  if (runtime) return;
  confirmedSettings.permissionMode = command.permissionMode ?? "manual";
  confirmedSettings.planMode = command.planMode === "plan" || command.planMode === "on" ? "on" : "off";
  const mode = permissionMode(confirmedSettings.permissionMode, confirmedSettings.planMode === "on" ? "plan" : "off");
  runtime = query({
    prompt: prompts,
    options: {
      cwd: configuration.cwd,
      pathToClaudeCodeExecutable: configuration.executablePath,
      systemPrompt: { type: "preset", preset: "claude_code" },
      settingSources: ["user", "project", "local"],
      includePartialMessages: true,
      env: process.env,
      ...(configuration.mcpServers ? { mcpServers: configuration.mcpServers } : {}),
      canUseTool,
      permissionMode: mode,
      allowDangerouslySkipPermissions: true,
      ...(configuration.resume
        ? { resume: configuration.resume }
        : { sessionId: configuration.sessionId }),
      ...(command.model ? { model: command.model } : {}),
      ...(command.effort ? { effort: command.effort } : {}),
      ...(Array.isArray(command.additionalDirectories) &&
      command.additionalDirectories.length
        ? { additionalDirectories: command.additionalDirectories }
        : {}),
      stderr: (data) => emit({ type: "bridge.stderr", message: data }),
    },
  });
  runtimeTask = (async () => {
    try {
      for await (const message of runtime) {
        assertGoalRecoverySession(configuration, message.session_id);
        if (typeof message.session_id === "string" && message.session_id)
          currentSessionId = message.session_id;
        goals.observeSession(currentSessionId);
        if (!goals.consume(message)) forwardProviderMessage(message);
      }
      goals.close();
      emit({ type: "bridge.closed" });
    } catch (error) {
      goals.publish({ confirmation: "unconfirmed", pendingOperation: null, detail: String(error.message) });
      goals.close();
      runtime.close();
      fail("Claude Agent SDK stream failed.", error);
    }
  })();
}

async function handle(command, isCancelled = () => false, signal) {
  switch (command.type) {
    case "initialize":
      if (configuration)
        throw new Error("Claude bridge is already initialized.");
      configuration = command;
      if (command.resume && command.loadHistory) {
        const messages = await getSessionMessages(command.resume, {
          dir: command.cwd,
        });
        emit({ type: "bridge.history", messages });
      }
      await startRuntime(command);
      const initialized = await runtime.initializationResult();
      if (configuration.mcpServers?.tlbx) {
        const deadline = Date.now() + 30_000;
        let connected = false;
        while (Date.now() < deadline) {
          const servers = await runtime.mcpServerStatus();
          const tlbx = servers.find((server) => server.name === "tlbx");
          if (tlbx?.status === "connected") { connected = true; break; }
          if (tlbx && tlbx.status !== "pending") break;
          await new Promise((resolve) => setTimeout(resolve, 100));
        }
        if (!connected)
          throw new Error("TLBX_MCP_PROVIDER_DISCOVERY: Claude did not connect its native tlbx MCP server; no model turn was started.");
      }
      emit({
        type: "bridge.ready",
        sdk: "@anthropic-ai/claude-agent-sdk",
        sdkVersion: __CLAUDE_AGENT_SDK_VERSION__,
        models: initialized.models ?? [],
        commands: initialized.commands ?? [],
      });
      goals.setSupported((initialized.commands ?? []).some((command) =>
        (typeof command === "string" ? command : command.name)?.replace(/^\//, "") === "goal"));
      return;
    case "thread.goal":
      goals.control(command);
      return;
    case "turn.start":
    case "turn.steer": {
      if (!configuration) throw new Error("Claude bridge is not initialized.");
      if (isCancelled()) return;
      await startRuntime(command);
      if (command.type === "turn.start") await goals.beforeInput(signal);
      if (isCancelled()) return;
      const stopped = () => {
        if (!isCancelled()) return false;
        if (activeTurnId === command.turnId) { activeTurnId = null; steeringPending = false; }
        return true;
      };
      activeTurnId = command.turnId;
      steeringPending = command.type === "turn.steer";
      try {
        await runtime.setModel(command.model ?? undefined);
        if (stopped()) return;
        await runtime.applyFlagSettings({ effortLevel: command.effort ?? null });
        if (stopped()) return;
        await runtime.setPermissionMode(
          permissionMode(command.permissionMode, command.planMode),
        );
        if (stopped()) return;
        confirmedSettings.permissionMode = command.permissionMode ?? "manual";
        confirmedSettings.planMode = command.planMode === "plan" || command.planMode === "on" ? "on" : "off";
        const message = await makeUserMessage(command);
        if (stopped()) return;
        goals.noteInput(message, command.prompt ?? "", command.turnId);
        prompts.push(message);
      } catch (error) {
        if (stopped()) return;
        if (activeTurnId === command.turnId) {
          activeTurnId = null;
          steeringPending = false;
        }
        throw error;
      }
      return;
    }
    case "runtime.configure": {
      if (!runtime) throw new Error("Claude bridge is not initialized.");
      if (command.expectedThreadId !== (currentSessionId ?? configuration.resume ?? configuration.sessionId) ||
          command.expectedTurnId !== (activeTurnId ?? null)) {
        emit({ type: "bridge.configured", requestId: command.requestId, status: "targetUnavailable",
          confirmed: {}, errors: {} });
        return;
      }
      const expected = { threadId: command.expectedThreadId, turnId: command.expectedTurnId };
      const currentScope = () => ({ threadId: currentSessionId ?? configuration.resume ?? configuration.sessionId,
        turnId: activeTurnId ?? null });
      const receipt = await configureRuntime(runtime, command.settings ?? {}, confirmedSettings, () => {
        releasePendingToolApprovals(pendingPermissions, expected, currentScope, emit);
      });
      emit({ type: "bridge.configured", requestId: command.requestId, ...receipt,
        effective: {}, scope: "subsequentResponses" });
      return;
    }
    case "turn.interrupt": {
      steeringPending = false;
      const receipt = runtime ? await runtime.interrupt() : undefined;
      emit({
        type: "bridge.interrupted",
        turnId: command.turnId,
        hasQueuedWork: (receipt?.still_queued?.length ?? 0) > 0,
      });
      return;
    }
    case "permission.resolve":
    case "user_input.resolve": {
      const kind = command.type === "permission.resolve" ? "permission" : "user_input";
      const pending = kind === "permission" ? pendingPermissions : pendingUserInputs;
      const receipt = resolveScopedCallback(pending, settledDecisions, kind, command, {
        threadId: currentSessionId ?? configuration.resume ?? configuration.sessionId,
        turnId: activeTurnId ?? null,
      });
      const decision = kind === "permission" ? receipt.value : receipt.value === null ? "cancel" : "answered";
      emit({ type: kind === "permission" ? "bridge.permission_resolved" : "bridge.user_input_resolved",
        requestId: command.requestId, session_id: receipt.threadId, turnId: receipt.turnId,
        status: receipt.status, ...(receipt.status === "unconfirmed" ? {} : { decision }),
        ...(kind === "user_input" && receipt.value !== null && receipt.value !== undefined ? { answers: receipt.value } : {}) });
      return;
    }
    case "shutdown":
      goals.close();
      prompts.close();
      if (runtime) runtime.close();
      await runtimeTask;
      input.close();
      process.exit(0);
      return;
    default:
      throw new Error(`Unsupported Claude bridge command '${command.type}'.`);
  }
}

const dispatch = createSettingsCommandDispatcher(handle);
const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  if (!line.trim()) return;
  let command;
  void Promise.resolve()
    .then(() => {
      command = JSON.parse(line);
      return dispatch(command);
    })
    .catch((error) => {
      if (error.code === "goalInputBlocked") {
        emit({ type: "bridge.input_rejected", commandType: command?.type,
          turnId: command?.turnId, session_id: currentSessionId ?? configuration?.resume ?? configuration?.sessionId,
          message: error.message, reason: error.reason });
        return;
      }
      fail("Claude bridge command failed.", error, {
        commandType: command?.type,
        requestId: command?.requestId ?? command?.turnId,
      });
    },
    );
});
input.on("close", () => {
  goals.close();
  prompts.close();
  if (runtime) runtime.close();
});
