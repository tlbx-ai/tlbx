import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const directory = path.dirname(fileURLToPath(import.meta.url));
const source = readFileSync(path.join(directory, 'index.ts'), 'utf8');
const notifications = readFileSync(path.join(directory, 'notifications.ts'), 'utf8');
const html = readFileSync(path.join(directory, '../../../static/index.html'), 'utf8');
const css = readFileSync(path.join(directory, '../../../static/css/app.css'), 'utf8');

describe('operator view wiring', () => {
  it('uses the native sidebar and dense list surfaces', () => {
    expect(html).toContain('id="btn-operator"');
    expect(html).toContain('id="operator-sessions"');
    expect(html).toContain('id="operator-work-items"');
    expect(html).toContain('id="operator-checkpoints"');
    expect(html).toContain('id="operator-notification-badge"');
    expect(css).toContain('.operator-session-row {');
    expect(css).toContain('.operator-work-row {');
  });

  it('renders exact session facts and explicit control-plane records without supervisor heuristics', () => {
    expect(source).toContain('record.session.isRunning');
    expect(source).toContain('record.session.foregroundDisplayName');
    expect(source).toContain('origin.snapshot.sessionStatuses');
    expect(source).not.toContain('session.supervisor');
    expect(source).not.toContain('currentHeat');
    expect(source).not.toContain('attentionScore');
    expect(source).toContain("url.protocol === 'http:' || url.protocol === 'https:'");
  });

  it('stops polling and aborts in-flight reads when closed', () => {
    expect(source).toContain('stopRefreshTimer();');
    expect(source).toContain('refreshAbortController?.abort();');
    expect(source).toContain('window.clearInterval(refreshTimer)');
  });

  it('notifies only from explicit event fields and keeps a sequence cursor', () => {
    expect(notifications).toContain("event.type === 'sessionStatusPublished'");
    expect(notifications).toContain("event.state === 'needsInput'");
    expect(notifications).toContain("event.type === 'workItemCreated'");
    expect(notifications).toContain('cursors.set(origin.key, event.sequence)');
    expect(notifications).not.toContain('currentHeat');
    expect(notifications).not.toContain('terminal output');
  });
});
