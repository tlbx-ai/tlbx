import { t } from '../i18n';
import { commandPreview } from './commandHighlight';
import { isHistoryError } from './historyVisibility';
import type { AppServerControlHistoryEntry } from './types';

export type ToolRowBadgeTone = 'command' | 'mcp' | 'read' | 'edit' | 'search' | 'tool';

export interface ParsedToolRowSummary {
  badgeTone: ToolRowBadgeTone;
  badgeText: string;
  serverName: string | null;
  primaryText: string;
  secondaryPath: string | null;
  secondaryPathRaw: string | null;
  statusKind: 'done' | 'error' | 'running';
  statusText: string;
}

const GENERIC_COMMAND_TOOL_NAMES = /^(?:exec_command|shell|command|ran command|run_command)$/i;

function activityText(key: string, fallback: string): string {
  const value = t(`appServerControl.activity.${key}`);
  return value && value !== `appServerControl.activity.${key}` ? value : fallback;
}

export function normalizeDisplayPath(rawPath: string): string {
  const trimmed = rawPath.trim();
  if (/^\/[A-Za-z]:\//.test(trimmed)) {
    return trimmed.slice(1).replace(/\//g, '\\');
  }
  return trimmed;
}

export function splitPathForDisplay(normalizedPath: string): {
  fileName: string;
  directory: string;
} {
  const cleaned = normalizedPath.replace(/[\\/]+$/, '');
  const lastSep = Math.max(cleaned.lastIndexOf('\\'), cleaned.lastIndexOf('/'));
  if (lastSep < 0) {
    return { fileName: cleaned, directory: '' };
  }
  return {
    fileName: cleaned.slice(lastSep + 1) || cleaned,
    directory: cleaned.slice(0, lastSep),
  };
}

export function resolveToolCommandScript(entry: AppServerControlHistoryEntry): string {
  const direct = entry.commandText?.trim();
  if (direct) return direct;
  const presentation = entry.toolPresentation;
  const subject = presentation?.subject?.trim();
  if (subject) return subject;
  if (presentation?.category === 'command') {
    const rawToolName = presentation.toolName?.trim();
    if (rawToolName && !GENERIC_COMMAND_TOOL_NAMES.test(rawToolName)) {
      return rawToolName;
    }
  }
  return '';
}

export function resolveCommandRunnerLabel(command: string): string {
  const preview = commandPreview(command).replace(/^["']/, '');
  if (/^gh(?:\.exe)?(?:\s|$)/i.test(preview)) return 'gh CLI';
  if (
    /^(?:"[^"\n]*[\\/])?(?:pwsh|powershell)(?:\.exe)?["\s]/i.test(command) ||
    /^(?:foreach|get-|set-|select-|write-output|\$[a-z_])/i.test(preview)
  ) {
    return 'PowerShell';
  }
  if (/^(?:"[^"\n]*[\\/])?bash(?:\.exe)?["\s]/i.test(command)) return 'Bash';
  if (/^git(?:\.exe)?(?:\s|$)/i.test(preview)) return 'git';
  if (/^(?:dotnet|npm|npx|node|python|py)(?:\.exe)?(?:\s|$)/i.test(preview)) {
    return preview.split(/\s+/)[0] ?? activityText('badgeShell', 'Shell');
  }
  return activityText('badgeShell', 'Shell');
}

function parseMcpToolNamePattern(
  rawName: string,
): { serverName: string | null; toolName: string } | null {
  const cleaned = rawName.replace(/^Run\s+/i, '').trim();
  const match = cleaned.match(/^mcp__([^_]+)__(.+)$/i) ?? cleaned.match(/^mcp_([a-z0-9-]+)_(.+)$/i);
  if (!match) return null;
  return { serverName: match[1] ?? null, toolName: match[2] ?? cleaned };
}

export function parseMcpToolIdentity(
  entry: AppServerControlHistoryEntry,
): { serverName: string | null; toolName: string } | null {
  const presentation = entry.toolPresentation;
  if (presentation?.mcp) {
    return {
      serverName: presentation.mcp.serverName?.trim() || null,
      toolName: presentation.toolName?.trim() || presentation.label.trim() || 'MCP',
    };
  }
  return parseMcpToolNamePattern(presentation?.toolName?.trim() ?? '');
}

export function resolveToolStatus(entry: AppServerControlHistoryEntry): {
  statusKind: 'done' | 'error' | 'running';
  statusText: string;
} {
  if (isHistoryError(entry)) {
    const exitCode = entry.toolPresentation?.exitCode;
    const outcome =
      entry.toolPresentation?.outcome ||
      (exitCode != null
        ? activityText('exitCode', 'exit {code}').replace('{code}', String(exitCode))
        : activityText('statusFailed', 'Failed'));
    return { statusKind: 'error', statusText: outcome };
  }
  const status = (entry.sourceStatus ?? '').trim().toLowerCase();
  if (status === 'running' || status === 'in_progress' || status === 'pending' || entry.pending) {
    return { statusKind: 'running', statusText: activityText('statusRunning', 'Running') };
  }
  return { statusKind: 'done', statusText: activityText('statusDone', 'Done') };
}

const BADGE_BY_CATEGORY: Record<string, { tone: ToolRowBadgeTone; key: string; fallback: string }> =
  {
    command: { tone: 'command', key: 'badgeShell', fallback: 'Shell' },
    read: { tone: 'read', key: 'badgeRead', fallback: 'Read' },
    edit: { tone: 'edit', key: 'badgeEdit', fallback: 'Edit' },
    search: { tone: 'search', key: 'badgeSearch', fallback: 'Search' },
  };

function resolveBadge(category: string | undefined): {
  tone: ToolRowBadgeTone;
  text: string;
} {
  const spec = category ? BADGE_BY_CATEGORY[category] : undefined;
  if (!spec) {
    return { tone: 'tool', text: activityText('badgeTool', 'Tool') };
  }
  return { tone: spec.tone, text: activityText(spec.key, spec.fallback) };
}

function buildReadToolRow(
  entry: AppServerControlHistoryEntry,
  rawToolName: string,
  firstPath: string | null,
  status: { statusKind: 'done' | 'error' | 'running'; statusText: string },
): ParsedToolRowSummary {
  const rawPath =
    entry.toolPresentation?.subject?.trim() ||
    firstPath ||
    rawToolName.replace(/^File\s+/i, '').trim() ||
    entry.title;
  const normalized = normalizeDisplayPath(rawPath);
  const { fileName, directory } = splitPathForDisplay(normalized);
  return {
    badgeTone: 'read',
    badgeText: activityText('badgeRead', 'Read'),
    serverName: null,
    primaryText: fileName || normalized,
    secondaryPath: directory || null,
    secondaryPathRaw: normalized || null,
    ...status,
  };
}

function buildDefaultToolRow(
  entry: AppServerControlHistoryEntry,
  rawToolName: string,
  firstPath: string | null,
  status: { statusKind: 'done' | 'error' | 'running'; statusText: string },
): ParsedToolRowSummary {
  const presentation = entry.toolPresentation;
  const badge = resolveBadge(presentation?.category);
  const cleanedName = rawToolName.replace(/^Run\s+/i, '').trim();
  const primaryText =
    cleanedName || presentation?.subject?.trim() || entry.title.trim() || badge.text;
  return {
    badgeTone: badge.tone,
    badgeText: badge.text,
    serverName: null,
    primaryText,
    secondaryPath: firstPath && firstPath !== primaryText ? firstPath : null,
    secondaryPathRaw: firstPath,
    ...status,
  };
}

export function parseCompactToolRow(entry: AppServerControlHistoryEntry): ParsedToolRowSummary {
  const presentation = entry.toolPresentation;
  const status = resolveToolStatus(entry);
  const firstPath = presentation?.paths[0] ? normalizeDisplayPath(presentation.paths[0]) : null;
  const mcp = parseMcpToolIdentity(entry);
  if (mcp) {
    return {
      badgeTone: 'mcp',
      badgeText: activityText('badgeMcp', 'MCP'),
      serverName: mcp.serverName,
      primaryText: mcp.toolName,
      secondaryPath: firstPath,
      secondaryPathRaw: firstPath,
      ...status,
    };
  }

  const rawToolName = presentation?.toolName?.trim() ?? '';
  if (presentation?.category === 'read' || /^File\s+/i.test(rawToolName)) {
    return buildReadToolRow(entry, rawToolName, firstPath, status);
  }

  return buildDefaultToolRow(entry, rawToolName, firstPath, status);
}

const SUMMARY_CATEGORY_KEYS: Record<string, [string, string]> = {
  read: ['read', 'File read'],
  edit: ['edit', 'File edit'],
  search: ['search', 'Search'],
};

function resolveGroupBucketLabel(item: AppServerControlHistoryEntry): string {
  if (item.toolPresentation?.category === 'command' || item.commandText) {
    return resolveCommandRunnerLabel(resolveToolCommandScript(item));
  }
  const mcp = parseMcpToolIdentity(item);
  if (mcp) {
    return mcp.serverName ? `MCP (${mcp.serverName})` : activityText('badgeMcp', 'MCP');
  }
  const category = item.toolPresentation?.category;
  const mapped = category ? SUMMARY_CATEGORY_KEYS[category] : undefined;
  return mapped ? activityText(mapped[0], mapped[1]) : activityText('tools', 'Tools');
}

export function summarizeToolActivityGroup(
  entries: readonly AppServerControlHistoryEntry[],
): string {
  const counts = new Map<string, number>();
  for (const item of entries) {
    const label = resolveGroupBucketLabel(item);
    counts.set(label, (counts.get(label) ?? 0) + 1);
  }
  const parts = [...counts].map(([name, count]) => (count > 1 ? `${name} ×${count}` : name));
  return parts.slice(0, 4).join(' · ') + (parts.length > 4 ? ` · +${parts.length - 4}` : '');
}
