import {
  Fragment,
  Schema,
  type Node as ProseMirrorNode,
  type NodeType,
  type Slice,
} from 'prosemirror-model';
import {
  defaultMarkdownParser,
  defaultMarkdownSerializer,
  schema as markdownSchema,
  MarkdownParser,
  MarkdownSerializer,
  type MarkdownSerializerState,
} from 'prosemirror-markdown';
import MarkdownIt from 'markdown-it';
import { TableMap, tableNodes } from 'prosemirror-tables';
import type { SmartInputComposerDraft, SmartInputComposerPart } from './smartInputComposerDraft';

export interface SmartInputRichDocument {
  version: 1;
  doc: Record<string, unknown>;
  selection?: Record<string, unknown>;
}

export function isSafeComposerLink(value: unknown): value is string {
  if (
    typeof value !== 'string' ||
    value.includes('\\') ||
    Array.from(value).some((char) => char.charCodeAt(0) <= 0x20) ||
    value.startsWith('//')
  )
    return false;
  const scheme = /^([a-z][a-z0-9+.-]*):/i.exec(value)?.[1];
  return !scheme || ['http', 'https', 'mailto'].includes(scheme.toLowerCase());
}

const attachmentSpec = {
  inline: true,
  group: 'inline',
  atom: true,
  draggable: true,
  attrs: { referenceId: { validate: 'string' } },
  toDOM: (node: ProseMirrorNode): [string, Record<string, string>, string] => [
    'span',
    { 'data-staging-ref': String(node.attrs.referenceId) },
    '\uFFFC',
  ],
  parseDOM: [
    {
      tag: 'span[data-staging-ref]',
      getAttrs: (node: HTMLElement) => ({
        referenceId: node.getAttribute('data-staging-ref') ?? '',
      }),
    },
  ],
};
const linkSpec = {
  ...markdownSchema.spec.marks.get('link'),
  attrs: {
    href: {
      validate: (href: unknown) => {
        if (!isSafeComposerLink(href)) throw new RangeError('Invalid composer link');
      },
    },
    title: { default: null },
  },
  parseDOM: [
    {
      tag: 'a[href]',
      getAttrs: (node: HTMLElement) =>
        isSafeComposerLink(node.getAttribute('href')) ? { href: node.getAttribute('href') } : false,
    },
  ],
};
export const richComposerSchema = new Schema({
  nodes: markdownSchema.spec.nodes
    .remove('image')
    .update('heading', {
      ...markdownSchema.spec.nodes.get('heading'),
      content: '(text | attachment)*',
    })
    .addBefore('text', 'attachment', attachmentSpec)
    .append(tableNodes({ tableGroup: 'block', cellContent: 'block+', cellAttributes: {} })),
  marks: markdownSchema.spec.marks.update('link', linkSpec),
});
const tokenizer = new MarkdownIt('commonmark', { html: false }).enable('table');
// Markdown table cells expose inline tokens; the ProseMirror table schema requires
// a paragraph inside every cell. Normalize tokens through the published core API.
tokenizer.core.ruler.after('block', 'tlbx_table_cell_paragraphs', (state) => {
  state.tokens = state.tokens.flatMap((token, index, tokens) => {
    if (token.type !== 'inline' || !['th_open', 'td_open'].includes(tokens[index - 1]?.type ?? ''))
      return [token];
    return [
      new state.Token('paragraph_open', 'p', 1),
      token,
      new state.Token('paragraph_close', 'p', -1),
    ];
  });
});
// An unstaged Markdown URL is text, not permission to fetch an external image.
// Preserve it through migration rather than silently dropping the image token.
tokenizer.core.ruler.after('inline', 'tlbx_unstaged_image_text', (state) => {
  for (const token of state.tokens) {
    if (!token.children) continue;
    token.children = token.children.map((child) => {
      if (child.type !== 'image') return child;
      const text = new state.Token('text', '', 0);
      text.content = `![${child.content}](${child.attrGet('src') ?? ''})`;
      return text;
    });
  }
});
const markdown = new MarkdownParser(richComposerSchema, tokenizer, {
  ...defaultMarkdownParser.tokens,
  image: { ignore: true, noCloseToken: true },
  table: { block: 'table' },
  thead: { ignore: true },
  tbody: { ignore: true },
  tr: { block: 'table_row' },
  th: { block: 'table_header' },
  td: { block: 'table_cell' },
});

export function richNode(name: string): NodeType {
  const type = richComposerSchema.nodes[name];
  if (!type) throw new RangeError(`Unknown composer node ${name}`);
  return type;
}

export function richDocumentFromClipboardSlice(slice: Slice): ProseMirrorNode {
  // A partial native CellSelection contains table rows, not their table.
  // Restore that parent for the plain-text projection without changing its cells.
  const content =
    slice.content.firstChild?.type.name === 'table_row'
      ? richNode('table').create(null, slice.content)
      : slice.content;
  return richNode('doc').create(null, content);
}

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function parsePersistedRichDocument(value: unknown): SmartInputRichDocument | null {
  if (!record(value) || value.version !== 1 || !record(value.doc)) return null;
  try {
    const doc = richComposerSchema.nodeFromJSON(value.doc);
    doc.check();
    if (doc.type !== richComposerSchema.topNodeType) return null;
    return {
      version: 1,
      doc: doc.toJSON() as Record<string, unknown>,
      ...(record(value.selection) ? { selection: value.selection } : {}),
    };
  } catch {
    return null;
  }
}

function placeholderPrefix(text: string): string {
  let prefix = '\uE000tlbx:';
  while (text.includes(prefix)) prefix += ':';
  return prefix;
}

function replacePlaceholderText(
  node: ProseMirrorNode,
  prefix: string,
  ids: readonly string[],
): ProseMirrorNode[] {
  if (!node.isText) {
    const children: ProseMirrorNode[] = [];
    node.forEach((child) => children.push(...replacePlaceholderText(child, prefix, ids)));
    if (node.type.name === 'code_block' && children.some((child) => !child.isText))
      return [richNode('paragraph').create(null, children)];
    return [node.copy(Fragment.fromArray(children))];
  }
  const text = node.text ?? '';
  const pattern = new RegExp(`${prefix}(\\d+)\uE001`, 'g');
  const result: ProseMirrorNode[] = [];
  let offset = 0;
  for (const match of text.matchAll(pattern)) {
    const id = ids[Number(match[1])];
    if (!id) continue;
    if (match.index > offset)
      result.push(richComposerSchema.text(text.slice(offset, match.index), node.marks));
    result.push(richNode('attachment').create({ referenceId: id }));
    offset = match.index + match[0].length;
  }
  if (offset < text.length) result.push(richComposerSchema.text(text.slice(offset), node.marks));
  return result;
}

function plainLegacyDocument(parts: readonly SmartInputComposerPart[]): ProseMirrorNode {
  const paragraphs: ProseMirrorNode[] = [];
  let inline: ProseMirrorNode[] = [];
  for (const part of parts) {
    if (part.kind === 'reference') {
      inline.push(richNode('attachment').create({ referenceId: part.referenceId }));
      continue;
    }
    part.text.split(/\r\n?|\n/).forEach((line, index) => {
      if (index > 0) {
        paragraphs.push(richNode('paragraph').create(null, inline));
        inline = [];
      }
      if (line) inline.push(richComposerSchema.text(line));
    });
  }
  paragraphs.push(richNode('paragraph').create(null, inline));
  return richNode('doc').create(null, paragraphs);
}

export function getRichComposerDocument(draft: SmartInputComposerDraft): ProseMirrorNode {
  const persisted = parsePersistedRichDocument(draft.richDoc);
  if (persisted) return richComposerSchema.nodeFromJSON(persisted.doc);
  const prefix = placeholderPrefix(
    draft.parts
      .filter((part) => part.kind === 'text')
      .map((part) => part.text)
      .join(''),
  );
  const ids: string[] = [];
  const text = draft.parts
    .map((part) => {
      if (part.kind === 'text') return part.text;
      ids.push(part.referenceId);
      return `${prefix}${ids.length - 1}\uE001`;
    })
    .join('');
  const parsed = markdown.parse(text);
  const result = replacePlaceholderText(parsed, prefix, ids)[0] ?? plainLegacyDocument(draft.parts);
  const refs: string[] = [];
  result.descendants((node) => {
    if (node.type.name === 'attachment') refs.push(String(node.attrs.referenceId));
  });
  // Markdown can consume a placeholder inside a link destination. Preserve the
  // original text and atom order if the parsed form cannot carry every reference.
  return refs.length === ids.length ? result : plainLegacyDocument(draft.parts);
}

function tableMarkdown(
  node: ProseMirrorNode,
  renderCell: (cell: ProseMirrorNode) => string,
): string {
  const map = TableMap.get(node);
  const seen = new Set<number>();
  const rows: string[] = [];
  for (let row = 0; row < map.height; row += 1) {
    const cells: string[] = [];
    for (let column = 0; column < map.width; column += 1) {
      const position = map.map[row * map.width + column];
      const cell = position === undefined ? null : node.nodeAt(position);
      cells.push(cell && position !== undefined && !seen.has(position) ? renderCell(cell) : '');
      if (position !== undefined) seen.add(position);
    }
    rows.push(`| ${cells.join(' | ')} |`);
    if (row === 0) rows.push(`| ${Array.from({ length: map.width }, () => '---').join(' | ')} |`);
  }
  return rows.join('\n');
}

export function richDocumentToParts(doc: ProseMirrorNode): SmartInputComposerPart[] {
  const prefix = placeholderPrefix(doc.textContent);
  const ids: string[] = [];
  const serializer = new MarkdownSerializer(
    {
      ...defaultMarkdownSerializer.nodes,
      attachment: (state: MarkdownSerializerState, node: ProseMirrorNode) => {
        ids.push(String(node.attrs.referenceId));
        state.write(`${prefix}${ids.length - 1}\uE001`);
      },
      table: (state: MarkdownSerializerState, node: ProseMirrorNode) => {
        state.write(
          tableMarkdown(node, (cell) =>
            serializer
              .serialize(richNode('doc').create(null, cell.content))
              // Backslashes have already been escaped by the Markdown serializer.
              // Retain them while adding the table delimiter's extra escape.
              .replace(/(\\*)\|/g, '$1\\|')
              .replace(/\n+/g, '<br>'),
          ),
        );
        state.closeBlock(node);
      },
    },
    defaultMarkdownSerializer.marks,
    // Default esc() leaves intraword underscores bare, which can expose an
    // inner emphasis pair after escaping only the ends of __init__. Escape
    // those remaining underscores too; code and mark delimiters bypass esc().
    { escapeExtraCharacters: /(?<!\\)_/g },
  );
  const text = serializer.serialize(doc);
  const parts: SmartInputComposerPart[] = [];
  const pattern = new RegExp(`${prefix}(\\d+)\uE001`, 'g');
  let offset = 0;
  for (const match of text.matchAll(pattern)) {
    if (match.index > offset) parts.push({ kind: 'text', text: text.slice(offset, match.index) });
    const id = ids[Number(match[1])];
    if (id) parts.push({ kind: 'reference', referenceId: id });
    offset = match.index + match[0].length;
  }
  if (offset < text.length) parts.push({ kind: 'text', text: text.slice(offset) });
  return parts;
}

export function draftFromRichDocument(
  draft: SmartInputComposerDraft,
  doc: ProseMirrorNode,
  selection?: Record<string, unknown>,
): SmartInputComposerDraft {
  return {
    nextOrdinalByKind: { ...draft.nextOrdinalByKind },
    parts: richDocumentToParts(doc),
    richDoc: {
      version: 1,
      doc: structuredClone(doc.toJSON() as Record<string, unknown>),
      ...(selection ? { selection: structuredClone(selection) } : {}),
    },
  };
}

function pruneRichNode(node: ProseMirrorNode, ids: ReadonlySet<string>): ProseMirrorNode[] {
  if (node.type.name === 'attachment') return ids.has(String(node.attrs.referenceId)) ? [node] : [];
  if (node.isText) return [node];
  const children: ProseMirrorNode[] = [];
  node.forEach((child) => children.push(...pruneRichNode(child, ids)));
  return [node.copy(Fragment.fromArray(children))];
}

export function pruneRichComposerDraft(
  draft: SmartInputComposerDraft,
  ids: ReadonlySet<string>,
): SmartInputComposerDraft {
  const doc = getRichComposerDocument(draft);
  return draftFromRichDocument(draft, pruneRichNode(doc, ids)[0] ?? doc, draft.richDoc?.selection);
}
