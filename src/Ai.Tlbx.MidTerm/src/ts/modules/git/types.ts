/**
 * Git Module Types
 *
 * Mirrors the C# Git DTOs for the frontend.
 */

export interface GitStatusResponse {
  readState?: 'ready' | 'no-repo' | 'error' | 'stale';
  errorCode?: string | null;
  errorDetail?: string | null;
  readAt?: string | null;
  label?: string | undefined;
  role?: string | undefined;
  source?: string | undefined;
  isPrimary?: boolean | undefined;
  branch: string;
  headHash?: string;
  indexVersion?: string;
  ahead: number;
  behind: number;
  staged: GitFileEntry[];
  modified: GitFileEntry[];
  untracked: GitFileEntry[];
  conflicted: GitFileEntry[];
  recentCommits: GitLogEntry[];
  stashCount: number;
  repoRoot: string;
  totalAdditions: number;
  totalDeletions: number;
  filesOmitted?: boolean;
  stagedCount?: number | null;
  modifiedCount?: number | null;
  untrackedCount?: number | null;
  conflictedCount?: number | null;
  recentCommitsCount?: number | null;
}

export type GitStatusReadResult =
  { ok: true; status: GitStatusResponse } | { ok: false; httpStatus: number; detail: string };

export interface GitOperationRequest {
  sessionId: string;
  repoRoot: string;
  operationId: string;
  action:
    'stage' | 'unstage' | 'commit' | 'stage-selection' | 'unstage-selection' | GitWorkspaceAction;
  paths: string[];
  all: boolean;
  message?: string;
  expectedIndexVersion?: string;
  expectedHead?: string;
  expectedBranch?: string;
  expectedPatchVersion?: string;
  selectedHunks?: number[];
  selectedLines?: Array<{ hunk: number; line: number }>;
  branch?: string;
  remote?: string;
  remoteBranch?: string;
  stashHash?: string;
  resolution?: string;
  expectedFileVersion?: string;
}

export interface GitOperationResponse {
  operationId: string;
  repoRoot: string;
  action: string;
  state: string;
  errorCode?: string | null;
  detail?: string | null;
  commitHash?: string | null;
  status?: GitStatusResponse | null;
  workspace?: GitWorkspaceResponse | null;
}

export type GitWorkspaceAction =
  | 'branch-switch'
  | 'branch-create'
  | 'fetch'
  | 'pull'
  | 'push'
  | 'merge'
  | 'merge-abort'
  | 'stash-create'
  | 'stash-apply'
  | 'stash-pop'
  | 'stash-drop'
  | 'resolve-conflict';
export interface GitWorkspaceResponse {
  repoRoot: string;
  currentBranch: string;
  headHash: string;
  indexVersion: string;
  branches: Array<{
    name: string;
    ref: string;
    hash: string;
    isCurrent: boolean;
    isRemote: boolean;
  }>;
  remotes: Array<{ name: string }>;
  stashes: Array<{ ref: string; hash: string; message: string; date: string }>;
  mergeInProgress: boolean;
  mergeMessage: string;
}
export interface GitConflictView {
  path: string;
  contentVersion: string;
  isBinary: boolean;
  hasMarkers: boolean;
  hasOurs: boolean;
  hasTheirs: boolean;
  isTruncated: boolean;
  base: string;
  ours: string;
  theirs: string;
}

export interface GitRepoBinding {
  repoRoot: string;
  label: string;
  role: string;
  source: string;
  isPrimary: boolean;
  status?: GitStatusResponse | null | undefined;
}

export interface GitRepoListResponse {
  repos: GitRepoBinding[];
}

export interface GitFileEntry {
  path: string;
  status: string;
  originalPath?: string | undefined;
  additions: number;
  deletions: number;
}

export interface GitLogEntry {
  hash: string;
  shortHash: string;
  message: string;
  author: string;
  date: string;
}

export interface GitDiffLine {
  kind: string;
  text: string;
  oldLine?: number | null;
  newLine?: number | null;
  visualRow?: number;
  segments?: Array<{ text: string; changed: boolean }>;
}

export interface GitDiffHunk {
  header: string;
  oldStart?: number;
  newStart?: number;
  lines: GitDiffLine[];
}

export interface GitDiffFileView {
  path: string;
  originalPath?: string | undefined;
  status: string;
  additions: number;
  deletions: number;
  isBinary: boolean;
  isTruncated: boolean;
  hunks: GitDiffHunk[];
  isNewFile?: boolean;
  hasLineStats?: boolean;
  sizeBytes?: number | null;
  mimeType?: string | null;
  encoding?: string | null;
  contentVersion?: string | null;
}

export interface GitDiffViewResponse {
  scope: string;
  title: string;
  isTruncated: boolean;
  patchVersion?: string;
  indexVersion?: string;
  canSelectLines?: boolean;
  files: GitDiffFileView[];
}

export interface GitCommitDetailsResponse {
  hash: string;
  shortHash: string;
  subject: string;
  body: string;
  author: string;
  authoredDate: string;
  committedDate: string;
  parentHashes: string[];
  comparedParentIndex?: number | null;
  comparedParentHash?: string | null;
  isRootCommit?: boolean;
  totalAdditions: number;
  totalDeletions: number;
  isTruncated: boolean;
  files: GitDiffFileView[];
}

export interface GitWsMessage {
  type: string;
  sessionId: string;
  sessionIds?: string[];
  status?: GitStatusResponse;
  repos?: GitRepoBinding[];
  diff?: string;
  error?: string;
}
