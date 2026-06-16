import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { GitRepoBinding, GitStatusResponse } from '../git/types';
import { syncSpacesTreeSidebarSessionProcessInfoElement } from './spacesTreeSidebarProcessInfo';

const mocks = vi.hoisted(() => ({
  repos: [] as GitRepoBinding[],
}));

class TestElement {
  className = '';
  dataset: Record<string, string> = {};
  title = '';
  innerHTML = '';
  attributes: Record<string, string> = {};
  private ownTextContent = '';
  private readonly children: TestElement[] = [];

  get textContent(): string {
    return this.ownTextContent + this.children.map((child) => child.textContent).join('');
  }

  set textContent(value: string) {
    this.ownTextContent = value;
  }

  append(...children: TestElement[]): void {
    this.children.push(...children);
  }

  replaceChildren(...children: TestElement[]): void {
    this.children.length = 0;
    this.children.push(...children);
  }

  setAttribute(name: string, value: string): void {
    this.attributes[name] = value;
  }

  querySelector(selector: string): TestElement | null {
    const className = selector.startsWith('.') ? selector.slice(1) : selector;
    return this.findByClass(className);
  }

  querySelectorAll(selector: string): TestElement[] {
    const className = selector.startsWith('.') ? selector.slice(1) : selector;
    const matches: TestElement[] = [];
    this.collectByClass(className, matches);
    return matches;
  }

  private findByClass(className: string): TestElement | null {
    if (this.className.split(/\s+/).includes(className)) {
      return this;
    }

    for (const child of this.children) {
      const match = child.findByClass(className);
      if (match) {
        return match;
      }
    }

    return null;
  }

  private collectByClass(className: string, matches: TestElement[]): void {
    if (this.className.split(/\s+/).includes(className)) {
      matches.push(this);
    }

    for (const child of this.children) {
      child.collectByClass(className, matches);
    }
  }
}

vi.mock('../git', () => ({
  getCachedGitReposForSession: () => mocks.repos,
}));

vi.mock('../process', () => ({
  getForegroundInfo: () => ({
    cwd: 'Q:/repos/Jpa',
    commandLine: 'codex --yolo',
    name: 'codex',
    displayName: 'codex --yolo',
  }),
}));

vi.mock('./sessionList', () => ({
  createForegroundIndicator: () => {
    const element = document.createElement('div') as unknown as TestElement;
    element.className = 'session-foreground';
    element.textContent = 'Q:/repos/Jpa > codex --yolo';
    return element;
  },
}));

function makeStatus(overrides: Partial<GitStatusResponse> = {}): GitStatusResponse {
  return {
    branch: 'dev',
    ahead: 0,
    behind: 0,
    staged: [],
    modified: [],
    untracked: [],
    conflicted: [],
    recentCommits: [],
    stashCount: 0,
    repoRoot: 'Q:\\repos\\MidTermWorkspace4',
    totalAdditions: 3,
    totalDeletions: 1,
    ...overrides,
  };
}

describe('spaces tree sidebar process info', () => {
  beforeEach(() => {
    mocks.repos.length = 0;
    vi.stubGlobal('document', {
      createElement: () => new TestElement(),
    });
  });

  it('shows monitored repositories by full directory path', () => {
    mocks.repos.push({
      repoRoot: 'Q:\\repos\\MidTermWorkspace4',
      label: 'MidTerm',
      role: 'target',
      source: 'manual',
      isPrimary: false,
      status: makeStatus(),
    });

    const processInfo = document.createElement('div') as unknown as HTMLElement;
    syncSpacesTreeSidebarSessionProcessInfoElement(processInfo, {
      id: 's1',
      session: {
        currentDirectory: 'Q:/repos/Jpa',
        workspacePath: 'Q:/repos/Jpa',
        shellType: 'pwsh',
      },
    });

    const repo = processInfo.querySelector<HTMLElement>('.session-extra-git-repo');
    expect(repo?.textContent).toBe('Q:\\repos\\MidTermWorkspace4');
  });

  it('renders each monitored repository as workdir branch and changes on one row', () => {
    mocks.repos.push({
      repoRoot: 'C:\\repos\\messengerSpecific',
      label: 'messengerSpecific',
      role: 'target',
      source: 'manual',
      isPrimary: false,
      status: makeStatus({
        repoRoot: 'C:\\repos\\messengerSpecific',
        branch: 'main',
        totalAdditions: 214,
        totalDeletions: 24,
      }),
    });

    const processInfo = document.createElement('div') as unknown as HTMLElement;
    syncSpacesTreeSidebarSessionProcessInfoElement(processInfo, {
      id: 's1',
      session: {
        currentDirectory: 'Q:/repos/Jpa',
        workspacePath: 'Q:/repos/Jpa',
        shellType: 'pwsh',
      },
    });

    const line = processInfo.querySelector<HTMLElement>('.session-extra-git');
    const icon = line?.querySelector<HTMLElement>('.session-extra-git-icon') as
      | (HTMLElement & TestElement)
      | null
      | undefined;
    const details = line?.querySelector<HTMLElement>('.session-extra-git-details');
    const separators = Array.from(
      line?.querySelectorAll<HTMLElement>('.session-extra-git-separator') ?? [],
    );

    const repo = details?.querySelector<HTMLElement>('.session-extra-git-repo');
    expect(repo?.textContent).toBe('C:\\repos\\messengerSpecific');
    expect(repo?.querySelector<HTMLElement>('.session-extra-git-path-root')?.textContent).toBe(
      'C:\\',
    );
    expect(repo?.querySelector<HTMLElement>('.session-extra-git-path-middle')?.textContent).toBe(
      'repos\\',
    );
    expect(repo?.querySelector<HTMLElement>('.session-extra-git-path-tail')?.textContent).toBe(
      'messengerSpecific',
    );
    expect(details?.querySelector<HTMLElement>('.session-extra-git-branch')?.textContent).toBe(
      'main',
    );
    expect(line?.querySelector<HTMLElement>('.session-extra-git-stats')?.textContent).toBe(
      '+214-24',
    );
    expect(
      line?.querySelector<HTMLElement>('.session-extra-git-stat-additions')?.textContent,
    ).toBe('+214');
    expect(
      line?.querySelector<HTMLElement>('.session-extra-git-stat-deletions')?.textContent,
    ).toBe('-24');
    expect(icon?.attributes['aria-hidden']).toBe('true');
    expect(icon?.innerHTML).toContain('<circle cx="7" cy="6" r="1.75"></circle>');
    expect(line?.textContent).toBe('C:\\repos\\messengerSpecific-main+214-24');
    expect(details?.textContent).toBe('C:\\repos\\messengerSpecific-main');
    expect(separators.map((separator) => separator.textContent)).toEqual(['-']);
  });

  it('keeps the sidebar repo list synchronized with primary and extra top-bar repos', () => {
    mocks.repos.push(
      {
        repoRoot: 'Q:\\repos\\Jpa',
        label: 'Jpa',
        role: 'cwd',
        source: 'auto',
        isPrimary: true,
        status: makeStatus({
          repoRoot: 'Q:\\repos\\Jpa',
          branch: 'main',
          totalAdditions: 0,
          totalDeletions: 0,
        }),
      },
      {
        repoRoot: 'Q:\\repos\\MidTerm',
        label: 'MidTerm',
        role: 'target',
        source: 'manual',
        isPrimary: false,
        status: makeStatus({
          repoRoot: 'Q:\\repos\\MidTerm',
          branch: 'dev',
          totalAdditions: 5,
          totalDeletions: 2,
        }),
      },
    );

    const processInfo = document.createElement('div') as unknown as HTMLElement;
    syncSpacesTreeSidebarSessionProcessInfoElement(processInfo, {
      id: 's1',
      session: {
        currentDirectory: 'Q:/repos/Jpa',
        workspacePath: 'Q:/repos/Jpa',
        shellType: 'pwsh',
      },
    });

    const repos = Array.from(
      processInfo.querySelectorAll<HTMLElement>('.session-extra-git-repo'),
    ).map((repo) => repo.textContent);
    const branches = Array.from(
      processInfo.querySelectorAll<HTMLElement>('.session-extra-git-branch'),
    ).map((branch) => branch.textContent);

    expect(repos).toEqual(['Q:\\repos\\Jpa', 'Q:\\repos\\MidTerm']);
    expect(branches).toEqual(['main', 'dev']);
  });
});
