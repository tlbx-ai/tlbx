import { describe, expect, it, vi } from 'vitest';
import { renderMarkdown, renderMarkdownFragment } from '../../utils/markdown';
import {
  draftFromRichDocument,
  getRichComposerDocument,
  richComposerSchema,
  richNode,
} from '../smartInput/richComposerDocument';
import { prepareSmartInputOutboundPrompt } from '../smartInput/smartInputOutboundReferences';
import { resolveHistoryBodyPresentation } from './historyContent';
import { getCachedHistoryMarkdown, pruneHistoryMarkdownCache } from './historyMarkdown';
import { buildAssistantEnrichedHtml } from './assistantEnrichment';
import type { AppServerControlHistoryEntry, HistoryMarkdownCacheEntry } from './types';

vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('./assistantEnrichment', () => ({
  buildAssistantEnrichedHtml: vi.fn((html: string) => ({ html: `<aside>${html}</aside>` })),
}));

function entry(
  kind: AppServerControlHistoryEntry['kind'],
  body: string,
): AppServerControlHistoryEntry {
  return { id: 'turn', kind, order: 1, tone: 'info', label: '', title: '', body, meta: '' };
}

async function outbound(doc: ReturnType<typeof getRichComposerDocument>) {
  return prepareSmartInputOutboundPrompt({
    sessionId: 'own-session',
    draft: draftFromRichDocument({ parts: [], nextOrdinalByKind: {} }, doc),
    attachments: [],
    uploadFailureMessage: 'Upload failed',
    attachmentReadFailureMessage: 'Read failed',
    uploadFile: async () => {
      throw new Error('Text projection must not upload');
    },
  });
}

function paragraph(text: string, code = false) {
  return richNode('paragraph').create(
    null,
    richComposerSchema.text(text, code ? [richComposerSchema.marks.code!.create()] : []),
  );
}

describe('rich user input to history', () => {
  it.each(['paragraphs', 'hard-break', 'hard-break-after-path'])(
    'keeps a genuine table-cell line break through outbound history and replay: %s',
    async (shape) => {
      const first = shape === 'hard-break-after-path' ? 'C:\\temp\\' : 'eins';
      const content =
        shape === 'paragraphs'
          ? [paragraph(first), paragraph('zwei')]
          : [
              richNode('paragraph').create(null, [
                richComposerSchema.text(first),
                richNode('hard_break').create(),
                richComposerSchema.text('zwei'),
              ]),
            ];
      const doc = richNode('doc').create(
        null,
        richNode('table').create(null, [
          richNode('table_row').create(
            null,
            richNode('table_header').create(null, paragraph('Breaks')),
          ),
          richNode('table_row').create(null, richNode('table_cell').create(null, content)),
        ]),
      );
      const result = await outbound(doc);
      const html = getCachedHistoryMarkdown(undefined, entry('user', result.text)).html;
      expect(html).toContain(`>${first}<br>zwei</td>`);
      const restored = getRichComposerDocument({
        parts: [{ kind: 'text', text: result.text }],
        nextOrdinalByKind: {},
      });
      const cell = restored.firstChild!.child(1).firstChild!;
      let breaks = 0;
      cell.descendants((node) => {
        if (node.type.name === 'hard_break') breaks += 1;
      });
      expect(breaks).toBe(1);
      expect(cell.textContent).toBe(first + 'zwei');
    },
  );

  it.each([false, true])(
    'keeps literal br tags literal in a table, including code: %s',
    async (code) => {
      const doc = richNode('doc').create(
        null,
        richNode('table').create(null, [
          richNode('table_row').create(
            null,
            richNode('table_header').create(null, paragraph('Literal')),
          ),
          richNode('table_row').create(
            null,
            richNode('table_cell').create(null, paragraph('<br>', code)),
          ),
        ]),
      );
      const result = await outbound(doc);
      const html = getCachedHistoryMarkdown(undefined, entry('user', result.text)).html;
      expect(html).toContain(code ? '<code>&lt;br&gt;</code>' : '>&lt;br&gt;</td>');
      const restored = getRichComposerDocument({
        parts: [{ kind: 'text', text: result.text }],
        nextOrdinalByKind: {},
      });
      expect(restored.firstChild!.child(1).firstChild!.textContent).toBe('<br>');
    },
  );

  it('accepts only simple native br markup inside tables, never attributes, escaped tags or code', () => {
    const html = renderMarkdown(
      '| Value |\n| --- |\n| eins<BR />zwei |\n| \\<br\\> |\n| `<br>` |\n| <br onclick="evil()"> |',
    );
    expect(html).toContain('>eins<br>zwei</td>');
    expect(html).toContain('>&lt;br&gt;</td>');
    expect(html).toContain('<code>&lt;br&gt;</code>');
    expect(html).not.toContain('<br onclick');
    expect(renderMarkdown('literal <br> text')).toContain('literal &lt;br&gt; text');
  });

  it.each(['> literal', String.raw`\<br>`])(
    'retains existing literal cell escaping: %s',
    async (text) => {
      const doc = richNode('doc').create(
        null,
        richNode('table').create(null, [
          richNode('table_row').create(
            null,
            richNode('table_header').create(null, paragraph('Literal')),
          ),
          richNode('table_row').create(null, richNode('table_cell').create(null, paragraph(text))),
        ]),
      );
      const result = await outbound(doc);
      const restored = getRichComposerDocument({
        parts: [{ kind: 'text', text: result.text }],
        nextOrdinalByKind: {},
      });
      expect(restored.firstChild!.child(1).firstChild!.textContent).toBe(text);
    },
  );

  it('preserves the native pasted in-word bold and italic paragraph', async () => {
    const doc = richNode('doc').create(
      null,
      richNode('paragraph').create(null, [
        richComposerSchema.text('prefix'),
        richComposerSchema.text('FETT', [richComposerSchema.marks.strong!.create()]),
        richComposerSchema.text('suffix und vor'),
        richComposerSchema.text('kursiv', [richComposerSchema.marks.em!.create()]),
        richComposerSchema.text('nach.'),
      ]),
    );
    const result = await outbound(doc);
    expect(getCachedHistoryMarkdown(undefined, entry('user', result.text)).html).toBe(
      'prefix<strong>FETT</strong>suffix und vor<em>kursiv</em>nach.',
    );
  });

  it.each(['strong', 'em', 'both', 'strong-around-em', 'em-around-strong'])(
    'preserves genuine in-word and nested rich marks through projection: %s',
    async (shape) => {
      const strong = richComposerSchema.marks.strong!.create();
      const em = richComposerSchema.marks.em!.create();
      const marked =
        shape === 'strong'
          ? [richComposerSchema.text('FETT', [strong])]
          : shape === 'em'
            ? [richComposerSchema.text('kursiv', [em])]
            : shape === 'both'
              ? [richComposerSchema.text('beides', [em, strong])]
              : shape === 'strong-around-em'
                ? [
                    richComposerSchema.text('a', [strong]),
                    richComposerSchema.text('b', [em, strong]),
                    richComposerSchema.text('c', [strong]),
                  ]
                : [
                    richComposerSchema.text('a', [em]),
                    richComposerSchema.text('b', [em, strong]),
                    richComposerSchema.text('c', [em]),
                  ];
      const content = richNode('paragraph').create(null, [
        richComposerSchema.text('prefix'),
        ...marked,
        richComposerSchema.text('suffix'),
      ]);
      const expected =
        shape === 'strong'
          ? ['prefix<strong>FETT</strong>suffix']
          : shape === 'em'
            ? ['prefix<em>kursiv</em>suffix']
            : shape === 'both'
              ? [
                  'prefix<em><strong>beides</strong></em>suffix',
                  'prefix<strong><em>beides</em></strong>suffix',
                ]
              : shape === 'strong-around-em'
                ? ['prefix<strong>a<em>b</em>c</strong>suffix']
                : ['prefix<em>a<strong>b</strong>c</em>suffix'];
      for (const table of [false, true]) {
        const doc = richNode('doc').create(
          null,
          table
            ? richNode('table').create(null, [
                richNode('table_row').create(
                  null,
                  richNode('table_header').create(null, paragraph('Marks')),
                ),
                richNode('table_row').create(null, richNode('table_cell').create(null, content)),
              ])
            : content,
        );
        const result = await outbound(doc);
        expect(
          getRichComposerDocument({
            parts: [{ kind: 'text', text: result.text }],
            nextOrdinalByKind: {},
          }).toJSON(),
        ).toEqual(doc.toJSON());
        const html = getCachedHistoryMarkdown(undefined, entry('user', result.text)).html;
        const rendered = table ? [...html.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)][0]?.[1] : html;
        expect(expected).toContain(rendered);
      }
    },
  );

  it('preserves recognized legacy raw Windows paths only for user prose and cells', () => {
    const path = String.raw`Q:\repos\Jpa\.tlbx\uploads\__init__.py`;
    const body = `${path} **bold** and \\*literal\\*`;
    expect(getCachedHistoryMarkdown(undefined, entry('user', body)).html).toBe(
      `${path} <strong>bold</strong> and *literal*`,
    );
    const table = getCachedHistoryMarkdown(
      undefined,
      entry('user', `| Path |\n| --- |\n| ${path} |`),
    ).html;
    expect([...table.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([path]);
    expect(getCachedHistoryMarkdown(undefined, entry('user', '`' + path + '`')).html).toBe(
      `<code>${path}</code>`,
    );
    expect(renderMarkdownFragment(String.raw`Q:\repos\Jpa\.tlbx`)).toBe(
      String.raw`Q:\repos\Jpa.tlbx`,
    );
  });

  it.each([
    String.raw`C:\temp\__init__.py`,
    '/tmp/__init__.py',
    '__init__',
    '_name_',
    'file_name',
    '___triple___',
  ])('retains literal underscore boundaries next to real marks: %s', async (text) => {
    const content = richNode('paragraph').create(null, [
      richComposerSchema.text(text + ' '),
      richComposerSchema.text('bold', [richComposerSchema.marks.strong!.create()]),
      richComposerSchema.text(' '),
      richComposerSchema.text('emphasis', [richComposerSchema.marks.em!.create()]),
    ]);
    for (const table of [false, true]) {
      const doc = richNode('doc').create(
        null,
        table
          ? richNode('table').create(null, [
              richNode('table_row').create(
                null,
                richNode('table_header').create(null, paragraph('Path')),
              ),
              richNode('table_row').create(null, richNode('table_cell').create(null, content)),
            ])
          : content,
      );
      const result = await outbound(doc);
      // The provider projection is valid Markdown; restoring it retains both
      // literal path text and the real adjacent marks. History must do the same.
      const restored = getRichComposerDocument({
        parts: [{ kind: 'text', text: result.text }],
        nextOrdinalByKind: {},
      });
      expect(restored.textContent).toBe(doc.textContent);
      const html = getCachedHistoryMarkdown(undefined, entry('user', result.text)).html;
      const rendered = table ? [...html.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)][0]?.[1] : html;
      expect(rendered).toBe(`${text} <strong>bold</strong> <em>emphasis</em>`);
    }
  });

  it('keeps real emphasis beside escaped punctuation without introducing word boundaries', () => {
    expect(renderMarkdownFragment('\\**real*')).toBe('*<em>real</em>');
    expect(renderMarkdownFragment('*real*\\*')).toBe('<em>real</em>*');
    expect(renderMarkdownFragment('__bold__ _emphasis_')).toBe(
      '<strong>bold</strong> <em>emphasis</em>',
    );
  });

  it('preserves the already-stored partial underscore escapes in paragraphs and table cells', () => {
    const wire = String.raw`C:\\temp\\\__init_\_.py **bold** *em*`;
    const expected = String.raw`C:\temp\__init__.py <strong>bold</strong> <em>em</em>`;
    expect(renderMarkdownFragment(wire)).toBe(expected);
    const table = renderMarkdownFragment(`| Path |\n| --- |\n| ${wire} |`);
    expect([...table.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([
      expected,
    ]);
  });

  it.each(['|', '||', '| |', '| : |', '| -- |'])(
    'does not promote a partial pipe separator to a table: %s',
    (separator) => {
      const html = renderMarkdownFragment(`a|b\n${separator}`);
      expect(html).toBe(`a|b<br>${separator}`);
    },
  );

  it.each([1, 2, 17, 129])(
    'matches exact backtick run lengths without formatting code: %s',
    (length) => {
      const delimiter = '`'.repeat(length);
      const content = `a${'`'.repeat(length + 1)} **literal** [link](https://example.test/)`;
      expect(renderMarkdownFragment(`${delimiter}${content}${delimiter} *outside*`)).toBe(
        `<code>${content}</code> <em>outside</em>`,
      );
    },
  );

  it('preserves escaped openers and treats backslashes literally inside code', () => {
    expect(renderMarkdownFragment('\\``a`')).toBe('`<code>a</code>');
    expect(renderMarkdownFragment('\\`literal and ``code`inside``')).toBe(
      '`literal and <code>code`inside</code>',
    );
    expect(renderMarkdownFragment('`a\\`')).toBe('<code>a\\</code>');
    expect(renderMarkdownFragment('` code ` and `  `')).toBe(
      '<code>code</code> and <code>  </code>',
    );
  });

  it('leaves a bounded sequence of distinct unmatched delimiter runs as literal text', () => {
    const input = Array.from({ length: 24 }, (_, index) => `${'`'.repeat(index + 1)}x`).join(' ');
    expect(renderMarkdownFragment(input)).toBe(input);
  });

  it.each(['# literal', '- literal', '> literal', '1. literal'])(
    'does not promote a literal paragraph to block markup: %s',
    async (text) => {
      const doc = richNode('doc').create(null, paragraph(text));
      const html = renderMarkdownFragment((await outbound(doc)).text);
      expect(html).toBe(text.replaceAll('>', '&gt;'));
    },
  );
  it.each([
    'a|b',
    String.raw`a\|b`,
    String.raw`a\\|b`,
    String.raw`C:\temp\file`,
    '*literal*',
    '<script>alert("x")</script>',
  ])('retains a literal one-column cell through send and history: %s', async (text) => {
    const doc = richNode('doc').create(
      null,
      richNode('table').create(null, [
        richNode('table_row').create(
          null,
          richNode('table_header').create(null, paragraph('Header')),
        ),
        richNode('table_row').create(null, richNode('table_cell').create(null, paragraph(text))),
      ]),
    );
    const result = await outbound(doc);
    const user = entry('user', result.text);
    expect(resolveHistoryBodyPresentation(user).mode).toBe('markdown');
    const html = getCachedHistoryMarkdown(undefined, user).html;
    const escaped = text
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;');
    expect([...html.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([
      escaped,
    ]);
    expect(html).not.toContain('<script>');
    expect(result.attachments).toEqual([]);
  });

  it.each([
    'a|b',
    String.raw`a\|b`,
    String.raw`a\\|b`,
    '**literal**',
    String.raw`C:\temp\__init__.py`,
    '`tick`',
    '<img src=x onerror=alert(1)>',
  ])('preserves code semantics inside a projected table cell: %s', async (text) => {
    const doc = richNode('doc').create(
      null,
      richNode('table').create(null, [
        richNode('table_row').create(
          null,
          richNode('table_header').create(null, paragraph('Code')),
        ),
        richNode('table_row').create(
          null,
          richNode('table_cell').create(null, paragraph(text, true)),
        ),
      ]),
    );
    const result = await outbound(doc);
    const html = renderMarkdownFragment(result.text);
    const escaped = text.replaceAll('<', '&lt;').replaceAll('>', '&gt;');
    expect([...html.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([
      `<code>${escaped}</code>`,
    ]);
  });

  it('keeps marks, safe links and fenced code without interpreting literal HTML or code marks', async () => {
    const doc = getRichComposerDocument({
      parts: [
        {
          kind: 'text',
          text: '**bold** and *em* [link](https://example.test/)\n\n```js\nconst x = "<tag> **literal**";\n```\n\n<script>alert(1)</script>',
        },
      ],
      nextOrdinalByKind: {},
    });
    const result = await outbound(doc);
    const html = getCachedHistoryMarkdown(undefined, entry('user', result.text)).html;
    expect(html).toContain('<strong>bold</strong>');
    expect(html).toContain('<em>em</em>');
    expect(html).toContain('href="https://example.test/"');
    expect(html).toContain('const x = &quot;&lt;tag&gt; **literal**&quot;;');
    expect(html).toContain('&lt;script&gt;alert(1)&lt;/script&gt;');
    expect(html).not.toContain('<script>');
    expect(renderMarkdown('[unsafe](javascript:alert)')).not.toContain('href=');
  });

  it('retains multiple columns, alignment and the existing CSV projection', () => {
    const html = renderMarkdown('| A | B |\n| :--- | ---: |\n| x\\|y | z |');
    expect([...html.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([
      'x|y',
      'z',
    ]);
    expect(html).toContain('data-align="right"');
    const csv = renderMarkdown('```csv\nName,Value\n"a|b",2\n```');
    expect(csv).toContain('data-table-source="csv"');
    expect([...csv.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([
      'a|b',
      '2',
    ]);
  });

  it('projects marked cells and safe links without promoting literal header HTML', async () => {
    const doc = getRichComposerDocument({
      parts: [
        {
          kind: 'text',
          text: '| <img src=x onerror=alert(1)> |\n| --- |\n| **bold** and *em* [safe](https://example.test/) |',
        },
      ],
      nextOrdinalByKind: {},
    });
    const html = getCachedHistoryMarkdown(
      undefined,
      entry('user', (await outbound(doc)).text),
    ).html;
    expect(html).toContain('<strong>bold</strong>');
    expect(html).toContain('<em>em</em>');
    expect(html).toContain('href="https://example.test/"');
    expect(html).toContain('&lt;img src=x onerror=alert(1)&gt;');
    expect(html).not.toContain('<img');
  });
});

describe('role-scoped bounded history Markdown cache', () => {
  it('cannot reuse assistant decorations after a row changes to user', () => {
    const cache = new Map<string, HistoryMarkdownCacheEntry>();
    const assistant = entry('assistant', '**same**');
    expect(getCachedHistoryMarkdown(cache, assistant).html).toContain('<aside>');
    const calls = vi.mocked(buildAssistantEnrichedHtml).mock.calls.length;
    const user = entry('user', assistant.body);
    expect(getCachedHistoryMarkdown(cache, user).html).toBe('<strong>same</strong>');
    expect(buildAssistantEnrichedHtml).toHaveBeenCalledTimes(calls);
    expect(getCachedHistoryMarkdown(cache, user)).toBe(cache.get(user.id));
    expect(getCachedHistoryMarkdown(cache, { ...user, body: '*changed*' }).html).toBe(
      '<em>changed</em>',
    );
  });

  it('invalidates source kind and prunes every role against the retained history window', () => {
    const cache = new Map<string, HistoryMarkdownCacheEntry>();
    const user = entry('user', 'user');
    const assistant = { ...entry('assistant', 'assistant'), id: 'assistant' };
    const plan = { ...entry('plan', 'plan'), id: 'plan', sourceItemType: 'proposed_plan' };
    const first = getCachedHistoryMarkdown(cache, user);
    expect(getCachedHistoryMarkdown(cache, { ...user, sourceItemType: 'user_message' })).not.toBe(
      first,
    );
    getCachedHistoryMarkdown(cache, assistant);
    getCachedHistoryMarkdown(cache, plan);
    pruneHistoryMarkdownCache(cache, [user, plan]);
    expect([...cache.keys()]).toEqual(['turn', 'plan']);
    pruneHistoryMarkdownCache(cache, [entry('tool', 'replaced')]);
    expect([...cache.keys()]).toEqual([]);
  });
});

describe('restored image attachment transport in reading history', () => {
  const imagePath = 'C:\\qa\\Pen_19.png';
  const secondImagePath = 'C:\\qa\\Bild_zwei.png';
  const preview = (path = imagePath) => ({
    displayPath: path,
    resolvedPath: path,
    mimeType: 'image/png',
  });
  const footer = ['Attached resource:', '- [image] ' + imagePath, '[Image attachment]'].join('\n');
  const withImage = (body: string) => ({
    ...entry('user', body),
    imagePreviews: [preview()],
  });

  it('hides only a repeated trailing image footer, preserving the canonical text and preview', () => {
    const raw = '**Betrachte das Bild.**\r\n\r\n' + footer.replace(/\n/g, '\r\n');
    const message = Object.freeze(withImage(raw));
    const rendered = getCachedHistoryMarkdown(undefined, message).html;
    expect(rendered).toBe('<strong>Betrachte das Bild.</strong>');
    expect(message.body).toBe(raw);
    expect(message.imagePreviews).toEqual([preview()]);
  });

  it('keeps image-only messages represented by their existing image previews', () => {
    const body = [
      'Attached resources (2):',
      '- [image] ' + imagePath,
      '- [image] ' + secondImagePath,
      '[Image attachment]',
      '[Image attachment]',
    ].join('\n');
    const message = {
      ...entry('user', body),
      imagePreviews: [preview(), preview(secondImagePath)],
    };
    expect(getCachedHistoryMarkdown(undefined, message).html).toBe('');
    expect(message.body).toBe(body);
    expect(message.imagePreviews).toHaveLength(2);
  });

  it.each([
    String.fromCharCode(96).repeat(3) + 'text\n',
    String.fromCharCode(96).repeat(4) + 'text\n',
    '~~~text\n',
  ])('preserves attachment-shaped text inside unclosed code: %s', (prefix) => {
    expect(getCachedHistoryMarkdown(undefined, withImage(prefix + footer)).html).toContain(
      'Attached resource:',
    );
  });

  it('preserves a complete fenced code sample', () => {
    const fence = String.fromCharCode(96).repeat(3);
    expect(
      getCachedHistoryMarkdown(undefined, withImage(fence + '\n' + footer + '\n' + fence)).html,
    ).toContain('Attached resource:');
  });

  it.each([
    footer + '\nWichtiger Nachsatz',
    'Attached resources (2):\n- [image] ' + imagePath,
    'Attached resource:\n- [file] ' + imagePath,
    'Attached resource:\n- [image] C:\\qa\\other.png',
    '> Attached resource:\n> - [image] ' + imagePath,
    '- Attached resource:\n  - [image] ' + imagePath,
  ])('preserves unmatched references, files, quotes, lists and user prose: %s', (raw) => {
    expect(getCachedHistoryMarkdown(undefined, withImage(raw)).html).toContain('Attached resource');
  });

  it('retains the footer until all referenced images are actually represented', () => {
    const raw = 'Bild prüfen.\n\n' + footer;
    const cache = new Map<string, HistoryMarkdownCacheEntry>();
    const withoutPreview = entry('user', raw);
    expect(getCachedHistoryMarkdown(cache, withoutPreview).html).toContain('Attached resource:');
    expect(getCachedHistoryMarkdown(cache, withImage(raw)).html).not.toContain(
      'Attached resource:',
    );
  });

  it('restores raw details when requested and invalidates the rendered cache on both toggles', () => {
    const message = withImage('Bild prüfen.\n\n' + footer);
    const cache = new Map<string, HistoryMarkdownCacheEntry>();
    expect(getCachedHistoryMarkdown(cache, message, false).html).toBe('Bild prüfen.');
    expect(getCachedHistoryMarkdown(cache, message, true).html).toContain('Attached resource:');
    expect(getCachedHistoryMarkdown(cache, message, false).html).toBe('Bild prüfen.');
    expect(message.body).toContain(footer);
  });
});
