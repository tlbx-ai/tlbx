import { renderMarkdownFragment } from '../../utils/markdown';
import { buildAssistantEnrichedHtml } from './assistantEnrichment';
import { usesMarkdownPresentation } from './historyContent';
import type { AppServerControlHistoryEntry, HistoryMarkdownCacheEntry } from './types';

function renderHistoryMarkdown(
  entry: AppServerControlHistoryEntry,
  fileMentions: NonNullable<AppServerControlHistoryEntry['fileMentions']>,
): string {
  const html = renderMarkdownFragment(entry.body);
  return entry.kind === 'user' ? html : buildAssistantEnrichedHtml(html, fileMentions).html;
}

export function getCachedHistoryMarkdown(
  cache: Map<string, HistoryMarkdownCacheEntry> | undefined,
  entry: AppServerControlHistoryEntry,
): HistoryMarkdownCacheEntry {
  const fileMentions = (entry.fileMentions ?? []).filter((mention) => mention.field === 'body');
  const fileMentionToken = fileMentions
    .map((mention) =>
      [
        mention.displayText,
        mention.path,
        mention.pathKind,
        mention.resolvedPath ?? '',
        mention.exists ? '1' : '0',
        mention.isDirectory ? '1' : '0',
      ].join(':'),
    )
    .join('|');
  const existing = cache?.get(entry.id);
  if (
    existing &&
    existing.kind === entry.kind &&
    existing.sourceItemType === entry.sourceItemType &&
    existing.body === entry.body &&
    existing.fileMentionToken === fileMentionToken
  ) {
    return existing;
  }

  const enriched = renderHistoryMarkdown(entry, fileMentions);
  const next: HistoryMarkdownCacheEntry = {
    kind: entry.kind,
    sourceItemType: entry.sourceItemType,
    body: entry.body,
    html: enriched,
    fileMentionToken,
  };
  cache?.set(entry.id, next);
  return next;
}

export function pruneHistoryMarkdownCache(
  cache: Map<string, HistoryMarkdownCacheEntry>,
  entries: readonly AppServerControlHistoryEntry[],
): void {
  if (cache.size === 0) {
    return;
  }

  const activeMarkdownIds = new Set(
    entries.filter(usesMarkdownPresentation).map((entry) => entry.id),
  );

  for (const cacheKey of cache.keys()) {
    if (!activeMarkdownIds.has(cacheKey)) {
      cache.delete(cacheKey);
    }
  }
}
