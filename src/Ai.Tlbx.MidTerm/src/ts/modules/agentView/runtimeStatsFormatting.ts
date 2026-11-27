import type { AppServerControlRuntimeStatsSummary } from './types';
import { getCurrentLocale, t } from '../i18n';

function appServerControlText(key: string, fallback: string): string {
  const translated = t(key);
  return !translated || translated === key ? fallback : translated;
}

function appServerControlFormat(
  key: string,
  fallback: string,
  replacements: Record<string, string | number>,
): string {
  return Object.entries(replacements).reduce(
    (text, [name, value]) => text.split(`{${name}}`).join(String(value)),
    appServerControlText(key, fallback),
  );
}

export function formatTokenCount(value: number): string {
  if (value >= 1000) {
    const compact = (value / 1000).toFixed(value >= 100000 ? 0 : 1);
    return `${compact.replace(/\.0$/, '')}k`;
  }

  return String(value);
}

export function formatPercent(value: number | null): string {
  return value === null ? '--' : `${Math.round(value)}%`;
}

function formatTokenWindowPercent(usedTokens: number, windowTokenLimit: number): string {
  if (windowTokenLimit <= 0) {
    return '--';
  }

  const percent = (usedTokens / windowTokenLimit) * 100;
  if (!Number.isFinite(percent)) {
    return '--';
  }

  return percent >= 10 ? `${Math.round(percent)}%` : `${percent.toFixed(1).replace(/\.0$/, '')}%`;
}

function formatTokenWindowCompact(stats: AppServerControlRuntimeStatsSummary): string {
  if (stats.windowTokenLimit === null) {
    return '--';
  }

  if (stats.windowUsedTokens === null) {
    return appServerControlFormat('appServerControl.usage.window', 'Window {limit}', {
      limit: formatTokenCount(stats.windowTokenLimit),
    });
  }

  return appServerControlFormat('appServerControl.usage.windowOf', '{percent} of {limit}', {
    percent: formatTokenWindowPercent(stats.windowUsedTokens, stats.windowTokenLimit),
    limit: formatTokenCount(stats.windowTokenLimit),
  });
}

function formatTokenWindowDetail(stats: AppServerControlRuntimeStatsSummary): string {
  if (stats.windowTokenLimit === null) {
    return appServerControlFormat('appServerControl.usage.window', 'Window {limit}', {
      limit: '--',
    });
  }

  if (stats.windowUsedTokens === null) {
    return appServerControlFormat('appServerControl.usage.window', 'Window {limit}', {
      limit: formatTokenCount(stats.windowTokenLimit),
    });
  }

  return appServerControlFormat(
    'appServerControl.usage.contextDetail',
    'Context {percent} of {limit} ({used} used)',
    {
      percent: formatTokenWindowPercent(stats.windowUsedTokens, stats.windowTokenLimit),
      limit: formatTokenCount(stats.windowTokenLimit),
      used: formatTokenCount(stats.windowUsedTokens),
    },
  );
}

export function buildRuntimeStatLabels(
  stats: AppServerControlRuntimeStatsSummary,
  detail: boolean,
): string[] {
  const labels: string[] = [];
  if (stats.windowTokenLimit !== null) {
    labels.push(detail ? formatTokenWindowDetail(stats) : formatTokenWindowCompact(stats));
  }
  if (stats.accumulatedInputTokens !== null) {
    const label = detail
      ? appServerControlText('appServerControl.usage.sessionIn', 'Session in')
      : appServerControlText('appServerControl.usage.inShort', 'in');
    labels.push(`${label} ${formatTokenCount(stats.accumulatedInputTokens)}`);
  }
  if (stats.accumulatedOutputTokens !== null) {
    const label = detail
      ? appServerControlText('appServerControl.usage.sessionOut', 'Session out')
      : appServerControlText('appServerControl.usage.outShort', 'out');
    labels.push(`${label} ${formatTokenCount(stats.accumulatedOutputTokens)}`);
  }
  if (stats.costAmount !== undefined && stats.costCurrency) {
    const amount = new Intl.NumberFormat(getCurrentLocale() || undefined, {
      maximumSignificantDigits: 6,
    }).format(stats.costAmount);
    labels.push(
      `${appServerControlText('appServerControl.usage.cost', 'Session cost')} ${amount} ${stats.costCurrency}`,
    );
  }
  return labels;
}

export function anchorDisclosureToggleInViewport(
  details: HTMLDetailsElement,
  summary: HTMLElement,
  unpinAutoScroll: () => void,
  performMutation: () => void,
): void {
  unpinAutoScroll();
  const viewport = details.closest<HTMLElement>('.agent-history-viewport');
  const summaryTopBefore = viewport ? summary.getBoundingClientRect().top : null;
  performMutation();
  if (!viewport || summaryTopBefore == null) {
    return;
  }
  const restoreAnchor = () => {
    if (!details.isConnected || !viewport.isConnected) return;
    const drift = summary.getBoundingClientRect().top - summaryTopBefore;
    if (Math.abs(drift) > 1) {
      const maxScrollTop = Math.max(0, viewport.scrollHeight - viewport.clientHeight);
      viewport.scrollTop = Math.max(0, Math.min(maxScrollTop, viewport.scrollTop + drift));
    }
  };
  restoreAnchor();
  if (typeof requestAnimationFrame === 'function') {
    requestAnimationFrame(restoreAnchor);
  }
}
