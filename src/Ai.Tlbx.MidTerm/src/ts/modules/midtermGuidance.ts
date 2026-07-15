/**
 * tlbx Guidance Helpers
 *
 * Resolves which guidance file and prompt text a terminal should receive
 * after tlbx creates the local .midterm/ directory.
 */

function normalizeExecutableName(value: string | null | undefined): string {
  const trimmed = value?.trim() ?? '';
  if (!trimmed) {
    return '';
  }

  let candidate = trimmed;
  const firstChar = candidate[0] ?? '';
  if (firstChar === '"' || firstChar === "'") {
    const closingQuote = candidate.indexOf(firstChar, 1);
    if (closingQuote > 1) {
      candidate = candidate.slice(1, closingQuote);
    }
  }

  const basename = candidate.replace(/\\/g, '/').split('/').pop() ?? candidate;
  const token = basename.trim().split(/\s+/)[0] ?? basename.trim();
  return token.replace(/\.exe$/i, '').toLowerCase();
}

/**
 * Return the agent guidance file that matches the foreground process.
 */
export function getAgentGuidanceFile(processName: string | null | undefined): string {
  return normalizeExecutableName(processName) === 'claude'
    ? '.midterm/CLAUDE.md'
    : '.midterm/AGENTS.md';
}

/**
 * Return the i18n key for the prompt pasted after guidance injection.
 */
export function getInjectGuidancePromptKey(processName: string | null | undefined): string {
  return getAgentGuidanceFile(processName) === '.midterm/CLAUDE.md'
    ? 'session.injectGuidancePrompt.claude'
    : 'session.injectGuidancePrompt.default';
}
