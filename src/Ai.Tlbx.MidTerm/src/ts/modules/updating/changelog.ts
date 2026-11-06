/**
 * Changelog Module
 *
 * Fetches and displays recent release notes from the public build catalog.
 */

import { escapeHtml } from '../../utils';
import { $currentSettings } from '../../stores';
import { updateSettings } from '../../api/client';
import { t } from '../i18n';
import { createLogger } from '../logging';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';

const log = createLogger('updating');
let releaseBackButtonLayer: (() => void) | null = null;

const BUILDS_RELEASES_BASE = 'https://builds.tlbx.ai/v1/channels';
const GITHUB_RELEASES_PAGE = 'https://github.com/tlbx-ai/tlbx/releases';

interface ChangelogRelease {
  tag_name: string;
  published_at?: string;
  body?: string;
}

let changelogRequest = 0;

/** Read each retained channel once; older history is available through HTML links. */
export async function fetchChangelogReleases(): Promise<ChangelogRelease[]> {
  const channels = await Promise.all(
    (['dev', 'main'] as const).map(async (channel) => {
      const response = await fetch(`${BUILDS_RELEASES_BASE}/${channel}/releases.json`, {
        credentials: 'omit',
        signal: AbortSignal.timeout(15_000),
      });
      if (!response.ok) throw new Error(`Build catalog HTTP ${response.status}`);
      const releases: unknown = await response.json();
      if (!Array.isArray(releases)) throw new Error('Invalid build catalog');
      return releases.slice(0, 3).map((release: unknown): ChangelogRelease => {
        if (!release || typeof release !== 'object') throw new Error('Invalid catalog release');
        const value = release as Record<string, unknown>;
        if (
          typeof value.tag_name !== 'string' ||
          !/^v\d+\.\d+\.\d+(-dev)?$/.test(value.tag_name) ||
          value.tag_name.endsWith('-dev') !== (channel === 'dev') ||
          value.draft === true
        ) {
          throw new Error('Invalid catalog release');
        }
        return {
          tag_name: value.tag_name,
          ...(typeof value.body === 'string' ? { body: value.body } : {}),
          ...(typeof value.published_at === 'string' ? { published_at: value.published_at } : {}),
        };
      });
    }),
  );
  return channels.flat().sort((a, b) => {
    const left = a.tag_name.slice(1).replace(/-dev$/, '').split('.').map(Number);
    const right = b.tag_name.slice(1).replace(/-dev$/, '').split('.').map(Number);
    for (let i = 0; i < 3; i++) {
      const difference = (right[i] ?? 0) - (left[i] ?? 0);
      if (difference) return difference;
    }
    return Number(a.tag_name.endsWith('-dev')) - Number(b.tag_name.endsWith('-dev'));
  });
}
/**
 * Show the changelog modal and fetch recent releases from the build catalog.
 * When afterUpdate is true, a "don't show again" option is displayed.
 */
export function showChangelog(afterUpdate = false): void {
  const modal = document.getElementById('changelog-modal');
  const body = document.getElementById('changelog-body');
  const dontShow = document.getElementById('changelog-dont-show');

  if (!releaseBackButtonLayer) {
    releaseBackButtonLayer = registerBackButtonLayer(closeChangelog);
  }

  if (modal) modal.classList.remove('hidden');
  if (body) body.innerHTML = `<div class="changelog-loading">${t('changelog.loading')}</div>`;

  if (dontShow) {
    if (afterUpdate) {
      dontShow.classList.remove('hidden');
    } else {
      dontShow.classList.add('hidden');
    }
  }

  fetchReleases();
}

export function disableChangelogAfterUpdate(): void {
  const settings = $currentSettings.get();
  if (!settings) return;

  const updated = { ...settings, showChangelogAfterUpdate: false };
  $currentSettings.set(updated);

  const checkbox = document.getElementById(
    'setting-changelog-after-update',
  ) as HTMLInputElement | null;
  if (checkbox) checkbox.checked = false;

  updateSettings(updated).catch((e: unknown) => {
    log.error(() => `Failed to save showChangelogAfterUpdate: ${String(e)}`);
  });

  closeChangelog();
}

/** Fetch the six most recent retained notes without GitHub API pagination. */
function fetchReleases(): void {
  const body = document.getElementById('changelog-body');
  if (!body) return;
  const request = ++changelogRequest;
  const historyLink =
    `<p><a href="${GITHUB_RELEASES_PAGE}" target="_blank" rel="noopener noreferrer">` +
    `${t('changelog.viewOnGithub')}</a></p>`;

  void fetchChangelogReleases()
    .then((releases) => {
      if (request !== changelogRequest) return;
      let html = releases.length ? '' : `<p>${t('changelog.noReleases')}</p>`;
      releases.forEach((release) => {
        const version = release.tag_name;
        const date = release.published_at
          ? new Date(release.published_at).toLocaleDateString()
          : '';
        const rawNotes = release.body || t('changelog.noNotes');
        const notes = stripVersionPrefix(rawNotes, version);

        html += '<div class="changelog-release">';
        html += '<div class="changelog-version">' + escapeHtml(version) + '</div>';
        if (date) html += '<div class="changelog-date">' + escapeHtml(date) + '</div>';
        html += '<div class="changelog-notes">' + formatMarkdown(notes) + '</div>';
        html += '</div>';
      });
      body.innerHTML = html + historyLink;
    })
    .catch((e: unknown) => {
      if (request !== changelogRequest) return;
      body.innerHTML = `<p class="changelog-error">${t('changelog.failed')}</p>` + historyLink;
      log.error(() => `Changelog error: ${String(e)}`);
    });
}
/**
 * Close the changelog modal
 */
export function closeChangelog(): void {
  changelogRequest++;
  releaseBackButtonLayer?.();
  releaseBackButtonLayer = null;

  const modal = document.getElementById('changelog-modal');
  if (modal) modal.classList.add('hidden');
}

/**
 * Strip version prefix from release notes
 *
 * Removes patterns like "v5.3.0: " or "5.3.0: " from the start of text
 * since the version is already shown in the header.
 */
function stripVersionPrefix(text: string, version: string): string {
  // Remove leading "v" from version if present for matching
  const versionNum = version.replace(/^v/, '');
  // Match "v5.3.0: " or "5.3.0: " at start of text
  const pattern = new RegExp(`^v?${escapeRegex(versionNum)}:\\s*`, 'i');
  return text.replace(pattern, '').trim();
}

/**
 * Escape special regex characters in a string
 */
function escapeRegex(str: string): string {
  return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Format markdown links with URL scheme validation.
 * Only allows http/https URLs to prevent XSS via javascript: URLs.
 */
function formatMarkdownLinks(escapedText: string): string {
  return escapedText.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_match, text: string, url: string) => {
    if (/^https?:\/\//i.test(url)) {
      return `<a href="${url}" target="_blank" rel="noopener noreferrer">${text}</a>`;
    }
    return `${text} (${url})`;
  });
}

/**
 * Format markdown text to HTML (basic subset)
 *
 * Supports: headers (## and ###), bold (**text**),
 * links [text](url), and bullet lists (- item)
 */
export function formatMarkdown(text: string): string {
  let html = formatMarkdownLinks(escapeHtml(text))
    .replace(/^### (.+)$/gm, '<h4>$1</h4>')
    .replace(/^## (.+)$/gm, '<h3>$1</h3>')
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');

  // Convert list items, then wrap consecutive <li> in <ul>
  html = html.replace(/^- (.+)$/gm, '<li>$1</li>');
  html = html.replace(/((?:<li>.*?<\/li>\n?)+)/g, (match) => {
    return '<ul>' + match.replace(/\n/g, '') + '</ul>';
  });

  // Clean up spacing
  html = html.replace(/\n{2,}/g, '\n');
  html = html.replace(/\n/g, '<br>');
  html = html.replace(/<br>(<ul>)/g, '$1');
  html = html.replace(/(<\/ul>)<br>/g, '$1');

  return html;
}
