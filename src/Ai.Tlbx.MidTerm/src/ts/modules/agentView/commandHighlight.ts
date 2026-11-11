/** A quiet shell preview; the expanded script always retains the exact source. */
export function commandPreview(source: string): string {
  const unwrapped = source
    .trim()
    .replace(
      /^(?:"[^"]*[\\/]pwsh(?:\.exe)?"|pwsh(?:\.exe)?|powershell(?:\.exe)?|bash|sh)\s+(?:-NoProfile\s+)?(?:-Command|-c|-lc)\s+/i,
      '',
    );
  const lines = unwrapped.replace(/\r\n?/g, '\n').split('\n');
  const meaningful = lines.filter((line) => !/^\s*["']?@['"]?\s*$/.test(line));
  const preview = meaningful.join(' ').replace(/\s+/g, ' ').trim() || source.trim();
  return preview.length > 600 ? `${preview.slice(0, 599)}…` : preview;
}

export interface CommandToken {
  text: string;
  kind?: 'string' | 'comment' | 'variable' | 'keyword' | 'number' | 'operator';
}

/** Lightweight shell accents, never HTML. Bound work even for very large scripts. */
export function tokenizeCommand(source: string): CommandToken[] {
  const limit = 32_768;
  const head = source.slice(0, limit);
  const pattern =
    /#[^\r\n]*|'(?:''|[^'])*'|"(?:`.|\\.|[^"\\`])*"|\$(?:\{[^}\r\n]*\}|[\w?:]+)|\b(?:if|then|else|elif|fi|for|foreach|while|do|done|function|return|try|catch|finally|throw|param|in|exit|const|let|await|async)\b|\b\d+(?:\.\d+)?\b|(?:\|\||&&|[|;=<>])/g;
  const tokens: CommandToken[] = [];
  let offset = 0;
  for (const match of head.matchAll(pattern)) {
    if (match.index > offset) tokens.push({ text: head.slice(offset, match.index) });
    const text = match[0];
    const kind = text.startsWith('#')
      ? 'comment'
      : /^["']/.test(text)
        ? 'string'
        : text.startsWith('$')
          ? 'variable'
          : /^\d/.test(text)
            ? 'number'
            : /^[|;=&<>]/.test(text)
              ? 'operator'
              : 'keyword';
    tokens.push({ text, kind });
    offset = match.index + text.length;
  }
  tokens.push({ text: source.slice(offset) });
  return tokens;
}

export function appendHighlightedCommand(target: HTMLElement, source: string): void {
  for (const token of tokenizeCommand(source)) {
    if (!token.kind) {
      target.appendChild(document.createTextNode(token.text));
      continue;
    }
    const span = document.createElement('span');
    span.className = `agent-command-${token.kind}`;
    span.textContent = token.text;
    target.appendChild(span);
  }
}
