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
  'sidebar.inputHistory': 'Prompt & Paste',
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

  it('renders IDE actions in the requested order with visible labels', async () => {
    const { createTabBar } = await import('./tabBar');

    const bar = createTabBar('session-1', vi.fn()) as unknown as FakeElement;
    const tabButtons = bar.children.filter((child) =>
      child.className.split(/\s+/).includes('session-tab'),
    );
    const actions = bar.querySelector('.ide-bar-actions');
    expect(actions).not.toBeNull();
    if (!actions) {
      throw new Error('Expected IDE actions container');
    }

    expect(tabButtons.map((button) => button.dataset.tab)).toEqual(['terminal', 'agent', 'files']);
    expect(tabButtons[1]?.querySelector('.feature-beta-badge')?.textContent).toBe('Beta');

    const buttons = actions.children;

    expect(buttons.map((button) => button.dataset.action)).toEqual([
      'inputHistory',
      'web',
      'share',
      'git',
    ]);
    expect(buttons.slice(0, 3).map((button) => button.children[1]?.textContent)).toEqual([
      'Prompt & Paste',
      'Browser',
      'Share',
    ]);
    expect(buttons[3]?.querySelector('.git-indicator-branch')?.textContent).toBe('No repo');
    expect(buttons[3]?.querySelector('.git-indicator-stats')?.innerHTML).toContain('+0');
    expect(buttons[3]?.querySelector('.git-indicator-stats')?.innerHTML).toContain('-0');
  });

  it('uses the registered share handler and updates git stats', async () => {
    const shareClick = vi.fn();
    const { createTabBar, setShareClickHandler, updateGitIndicator } = await import('./tabBar');

    setShareClickHandler(shareClick);

    const bar = createTabBar('session-1', vi.fn()) as unknown as FakeElement;
    const actions = bar.querySelector('.ide-bar-actions');
    expect(actions).not.toBeNull();
    if (!actions) {
      throw new Error('Expected IDE actions container');
    }
    const shareButton = actions.children[2];
    const gitButton = actions.children[3];

    shareButton.click();
    expect(shareClick).toHaveBeenCalledTimes(1);

    updateGitIndicator(
      bar as unknown as HTMLDivElement,
      {
        branch: 'feature/git-chip',
        ahead: 0,
        behind: 0,
        staged: [],
        modified: [{ path: 'a.ts', status: 'modified', additions: 7, deletions: 3 }],
        untracked: [],
        conflicted: [],
        recentCommits: [],
        stashCount: 0,
        repoRoot: '/repo',
        label: 'repo',
        totalAdditions: 7,
        totalDeletions: 3,
      } as any,
    );

    expect(gitButton.querySelector('.git-indicator-stats')?.innerHTML).toContain('+7');
    expect(gitButton.querySelector('.git-indicator-stats')?.innerHTML).toContain('-3');
    expect(gitButton.querySelector('.git-indicator-branch')?.textContent).toBe('feature/git-chip');
    expect(gitButton.querySelector('.git-indicator-status')?.textContent).toBe('~1');
    expect(gitButton.querySelector('.git-indicator-label')?.textContent).toBe('repo');
  });

  it('shows target repository labels before branch names', async () => {
    const { createTabBar, updateGitIndicator } = await import('./tabBar');

    const bar = createTabBar('session-1', vi.fn()) as unknown as FakeElement;

    updateGitIndicator(
      bar as unknown as HTMLDivElement,
      [
        {
          repoRoot: '/repo/jpa',
          label: 'Jpa',
          role: 'cwd',
          source: 'auto',
          isPrimary: true,
          status: {
            branch: 'main',
            ahead: 0,
            behind: 0,
            staged: [],
            modified: [],
            untracked: [],
            conflicted: [],
            recentCommits: [],
            stashCount: 0,
            repoRoot: '/repo/jpa',
            label: 'Jpa',
            role: 'cwd',
            source: 'auto',
            isPrimary: true,
            totalAdditions: 0,
            totalDeletions: 0,
          },
        },
        {
          repoRoot: '/repo/midterm',
          label: 'MidTerm',
          role: 'target',
          source: 'manual',
          isPrimary: false,
          status: {
            branch: 'dev',
            ahead: 0,
            behind: 0,
            staged: [],
            modified: [],
            untracked: [],
            conflicted: [],
            recentCommits: [],
            stashCount: 0,
            repoRoot: '/repo/midterm',
            label: '',
            role: '',
            source: '',
            isPrimary: false,
            totalAdditions: 0,
            totalDeletions: 0,
          },
        },
      ] as any,
    );

    const actions = bar.querySelector('.ide-bar-actions');
    expect(actions).not.toBeNull();
    const repoChips = actions
      ?.querySelector('.git-indicator-strip')
      ?.children.filter((child) => child.className.split(/\s+/).includes('git-repo-chip'));

    expect(repoChips?.[0]?.querySelector('.git-indicator-branch')?.textContent).toBe('main');
    expect(repoChips?.[0]?.querySelector('.git-indicator-label')?.textContent).toBe('Jpa');
    expect(repoChips?.[1]?.querySelector('.git-indicator-branch')?.textContent).toBe('MidTerm');
    expect(repoChips?.[1]?.querySelector('.git-indicator-label')?.textContent).toBe('dev');
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
