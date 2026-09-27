import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

class FakeClassList {
  private readonly owner: FakeElement;

  public constructor(owner: FakeElement) {
    this.owner = owner;
  }

  public add(...tokens: string[]): void {
    const classes = new Set(this.owner.className.split(/\s+/).filter(Boolean));
    for (const token of tokens) {
      classes.add(token);
    }
    this.owner.className = Array.from(classes).join(' ');
  }

  public toggle(token: string, force?: boolean): boolean {
    const classes = new Set(this.owner.className.split(/\s+/).filter(Boolean));
    const shouldAdd = force ?? !classes.has(token);
    if (shouldAdd) {
      classes.add(token);
    } else {
      classes.delete(token);
    }
    this.owner.className = Array.from(classes).join(' ');
    return shouldAdd;
  }
}

class FakeElement {
  public readonly tagName: string;
  public className = '';
  public textContent = '';
  public title = '';
  public innerHTML = '';
  public hidden = false;
  public readonly dataset: Record<string, string> = {};
  public readonly children: FakeElement[] = [];
  public readonly classList: FakeClassList;
  public readonly style: Record<string, string> = {};
  private readonly listeners = new Map<string, Array<() => void>>();

  public constructor(tagName: string) {
    this.tagName = tagName.toUpperCase();
    this.classList = new FakeClassList(this);
  }

  public appendChild<T extends FakeElement>(child: T): T {
    this.children.push(child);
    return child;
  }

  public addEventListener(type: string, handler: () => void): void {
    const handlers = this.listeners.get(type) ?? [];
    handlers.push(handler);
    this.listeners.set(type, handlers);
  }

  public click(): void {
    const handlers = this.listeners.get('click') ?? [];
    for (const handler of handlers) {
      handler();
    }
  }

  public setAttribute(name: string, value: string): void {
    if (name === 'title') {
      this.title = value;
    }
  }

  public querySelector(selector: string): FakeElement | null {
    return findMatchingElement(this.children, selector);
  }
}

function findMatchingElement(elements: FakeElement[], selector: string): FakeElement | null {
  for (const element of elements) {
    if (matchesSelector(element, selector)) {
      return element;
    }

    const nestedMatch = findMatchingElement(element.children, selector);
    if (nestedMatch) {
      return nestedMatch;
    }
  }

  return null;
}

function matchesSelector(element: FakeElement, selector: string): boolean {
  const dataTabMatch = selector.match(/^\.([^\[]+)\[data-tab="([^"]+)"\]$/);
  if (dataTabMatch) {
    return (
      element.className.split(/\s+/).includes(dataTabMatch[1] ?? '') &&
      element.dataset.tab === dataTabMatch[2]
    );
  }

  const dataActionMatch = selector.match(/^\[data-action="([^"]+)"\]$/);
  if (dataActionMatch) {
    return element.dataset.action === dataActionMatch[1];
  }

  if (selector.startsWith('.')) {
    const className = selector.slice(1);
    return element.className.split(/\s+/).includes(className);
  }

  return false;
}

const translations: Record<string, string> = {
  'common.beta': 'Beta',
  'session.terminal': 'Terminal',
  'sessionTabs.agent': 'Agent',
  'sessionTabs.files': 'Files',
  'sessionTabs.git': 'Git',
  'sidebar.inputHistory': 'History',
  'sessionTabs.share': 'Share',
  'sessionTabs.web': 'Browser',
  'sessionTabs.webShort': 'Browser',
  'git.noRepoShort': 'No repo',
  'git.cleanShort': 'Clean',
};

const originalDocument = globalThis.document;

vi.mock('../i18n', () => ({
  t: (key: string) => translations[key] ?? key,
}));

describe('tabBar', () => {
  beforeAll(() => {
    Object.assign(globalThis, {
      document: {
        createElement: (tagName: string) => new FakeElement(tagName),
      },
    });
  });

  afterAll(() => {
    Object.assign(globalThis, {
      document: originalDocument,
    });
  });

  beforeEach(() => {
    vi.resetModules();
  });

  it('renders every tracked repository before layout measurement decides overflow', async () => {
    const { createTabBar, updateGitIndicator } = await import('./tabBar');

    const bar = createTabBar('session-1', vi.fn()) as unknown as FakeElement;
    const repos = Array.from({ length: 6 }, (_, index) => ({
      repoRoot: `/repo/${index}`,
      label: `repo-${index}`,
      role: index === 0 ? 'cwd' : 'target',
      source: index === 0 ? 'auto' : 'manual',
      isPrimary: index === 0,
      status: {
        branch: `branch-${index}`,
        ahead: 0,
        behind: 0,
        staged: [],
        modified: [],
        untracked: [],
        conflicted: [],
        recentCommits: [],
        stashCount: 0,
        repoRoot: `/repo/${index}`,
        label: `repo-${index}`,
        role: index === 0 ? 'cwd' : 'target',
        source: index === 0 ? 'auto' : 'manual',
        isPrimary: index === 0,
        totalAdditions: 0,
        totalDeletions: 0,
      },
    }));

    updateGitIndicator(bar as unknown as HTMLDivElement, repos as any);

    const repoChips = bar
      .querySelector('.git-indicator-strip')
      ?.children.filter((child) => child.className.split(/\s+/).includes('git-repo-chip'));

    expect(repoChips).toHaveLength(6);
    expect(repoChips?.[5]?.querySelector('.git-indicator-branch')?.textContent).toBe('repo-5');
    expect(repoChips?.[5]?.querySelector('.git-indicator-label')?.textContent).toBe('branch-5');
  });

  it('forces hidden tabs out of layout even when tab CSS uses display flex', async () => {
    const { createTabBar, setTabVisible } = await import('./tabBar');

    const bar = createTabBar('session-1', vi.fn()) as unknown as FakeElement;
    const agentButton = bar.children.find(
      (child) =>
        child.className.split(/\s+/).includes('session-tab') && child.dataset.tab === 'agent',
    );

    expect(agentButton).toBeDefined();
    if (!agentButton) {
      throw new Error('Expected agent tab button');
    }

    setTabVisible(bar as unknown as HTMLDivElement, 'agent', false);

    expect(agentButton.hidden).toBe(true);
    expect(agentButton.style.display).toBe('none');

    setTabVisible(bar as unknown as HTMLDivElement, 'agent', true);

    expect(agentButton.hidden).toBe(false);
    expect(agentButton.style.display).toBe('');
  });
});
