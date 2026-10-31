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
let configuration;
let runtime;
let runtimeTask;
let currentSessionId;
let activeTurnId;

function emit(value) {
  process.stdout.write(`${JSON.stringify(value)}\n`);
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
    session_id: currentSessionId ?? configuration.sessionId,
    parent_tool_use_id: null,
    ...(command.type === "turn.steer" ? { priority: "now" } : {}),
    message: { role: "user", content },
  };
}

function waitForResolution(map, requestId, signal, fallback) {
  return new Promise((resolve) => {
    const complete = (value) => {
      map.delete(requestId);
      signal?.removeEventListener("abort", abort);
      resolve(value);
    };
    const abort = () => complete(fallback);
    map.set(requestId, complete);
    if (signal?.aborted) abort();
    else signal?.addEventListener("abort", abort, { once: true });
  });
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
    );
    if (answers === null)
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
  );
  if (decision === "accept")
    return { behavior: "allow", updatedInput: toolInput };
  return {
    behavior: "deny",
    message:
      decision === "cancel"
        ? "User cancelled tool execution."
        : "User declined tool execution.",
  };
}

async function startRuntime(command) {
  if (runtime) return;
  const mode = permissionMode(command.permissionMode, command.planMode);
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
        if (typeof message.session_id === "string" && message.session_id)
          currentSessionId = message.session_id;
        emit(message);
      }
      emit({ type: "bridge.closed" });
    } catch (error) {
      fail("Claude Agent SDK stream failed.", error);
    }
  })();
}

async function handle(command) {
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
      return;
    case "turn.start":
    case "turn.steer": {
      if (!configuration) throw new Error("Claude bridge is not initialized.");
      await startRuntime(command);
      activeTurnId = command.turnId;
      await runtime.setModel(command.model ?? undefined);
      await runtime.applyFlagSettings({ effortLevel: command.effort ?? null });
      await runtime.setPermissionMode(
        permissionMode(command.permissionMode, command.planMode),
      );
      prompts.push(await makeUserMessage(command));
      return;
    }
    case "turn.interrupt": {
      const receipt = runtime ? await runtime.interrupt() : undefined;
      emit({
        type: "bridge.interrupted",
        turnId: command.turnId,
        hasQueuedWork: (receipt?.still_queued?.length ?? 0) > 0,
      });
      return;
    }
    case "permission.resolve": {
      const resolve = pendingPermissions.get(command.requestId);
      if (!resolve)
        throw new Error(
          `Claude permission request was not found: ${command.requestId}`,
        );
      resolve(command.decision);
      emit({
        type: "bridge.permission_resolved",
        requestId: command.requestId,
        decision: command.decision,
      });
      return;
    }
    case "user_input.resolve": {
      const resolve = pendingUserInputs.get(command.requestId);
      if (!resolve)
        throw new Error(
          `Claude user input request was not found: ${command.requestId}`,
        );
      resolve(command.answers ?? {});
      emit({
        type: "bridge.user_input_resolved",
        requestId: command.requestId,
      });
      return;
    }
    case "shutdown":
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

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  if (!line.trim()) return;
  let command;
  void Promise.resolve()
    .then(() => {
      command = JSON.parse(line);
      return handle(command);
    })
    .catch((error) =>
      fail("Claude bridge command failed.", error, {
        commandType: command?.type,
        requestId: command?.requestId ?? command?.turnId,
      }),
    );
});
input.on("close", () => {
  prompts.close();
  if (runtime) runtime.close();
});
