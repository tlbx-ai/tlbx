import { getSessionMessages, importSessionToStore, InMemorySessionStore } from "@anthropic-ai/claude-agent-sdk";

// The SDK's completed-local flag omits /goal when it also starts model work.
// Retain the native system commandRun -> parent UUID proof during the same
// SDK history load. XML-looking user text alone never establishes ownership.
class NativeCommandHistory extends InMemorySessionStore {
  commands = new Set();
  async append(key, entries) {
    for (const entry of entries) {
      if (entry.type === "system" && entry.subtype === "local_command" &&
          entry.level === "info" && typeof entry.commandRun?.command === "string" &&
          typeof entry.parentUuid === "string") this.commands.add(entry.parentUuid);
    }
    // Wire request/response blobs have no conversation UUID and are not history.
    return super.append(key, entries.filter(entry =>
      entry.type !== "api-request-blob" && entry.type !== "api-response-blob"));
  }
}

export async function loadNativeHistory(sessionId, cwd) {
  const store = new NativeCommandHistory();
  await importSessionToStore(sessionId, store, { dir: cwd });
  const messages = await getSessionMessages(sessionId, { dir: cwd, sessionStore: store });
  return messages.map(message => store.commands.has(message.uuid)
    ? { ...message, isNativeLocalCommand: true } : message);
}
