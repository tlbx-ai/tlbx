interface ListFrame {
  tag: 'ul' | 'ol';
  indent: number;
}

interface ParsedListLine {
  tag: 'ul' | 'ol';
  indent: number;
  content: string;
}

function parseListLine(line: string): ParsedListLine | null {
  const ulMatch = line.match(/^([ \t]*)- (.+)$/);
  if (ulMatch) {
    return {
      tag: 'ul',
      indent: (ulMatch[1] ?? '').replace(/\t/g, '  ').length,
      content: ulMatch[2] ?? '',
    };
  }
  const olMatch = line.match(/^([ \t]*)\d+\. (.+)$/);
  if (olMatch) {
    return {
      tag: 'ol',
      indent: (olMatch[1] ?? '').replace(/\t/g, '  ').length,
      content: olMatch[2] ?? '',
    };
  }
  return null;
}

function popFrames(
  stack: ListFrame[],
  parts: string[],
  shouldPop: (frame: ListFrame) => boolean,
): void {
  while (stack.length > 0) {
    const top = stack[stack.length - 1];
    if (!top || !shouldPop(top)) break;
    parts.push(`</li></${top.tag}>`);
    stack.pop();
  }
}

export function renderMarkdownLists(html: string): string {
  const output: string[] = [];
  const stack: ListFrame[] = [];
  let listParts: string[] = [];

  const flushList = () => {
    if (stack.length === 0) return;
    popFrames(stack, listParts, () => true);
    output.push(listParts.join(''));
    listParts = [];
  };

  for (const line of html.split('\n')) {
    const parsed = parseListLine(line);
    if (!parsed) {
      flushList();
      output.push(line);
      continue;
    }

    const current = stack[stack.length - 1];
    if (!current || parsed.indent > current.indent) {
      stack.push({ tag: parsed.tag, indent: parsed.indent });
      listParts.push(`<${parsed.tag}><li>${parsed.content}`);
      continue;
    }

    popFrames(
      stack,
      listParts,
      (frame) =>
        parsed.indent < frame.indent ||
        (parsed.indent === frame.indent && frame.tag !== parsed.tag),
    );
    const afterClose = stack[stack.length - 1];
    if (afterClose && afterClose.tag === parsed.tag && afterClose.indent === parsed.indent) {
      listParts.push(`</li><li>${parsed.content}`);
    } else {
      stack.push({ tag: parsed.tag, indent: parsed.indent });
      listParts.push(`<${parsed.tag}><li>${parsed.content}`);
    }
  }

  flushList();
  return output.join('\n');
}
