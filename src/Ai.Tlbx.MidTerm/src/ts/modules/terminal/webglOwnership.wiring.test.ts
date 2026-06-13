import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const managerSource = readFileSync(path.join(__dirname, 'manager.ts'), 'utf8');
const mainSource = readFileSync(path.join(__dirname, '../../main.ts'), 'utf8');

describe('WebGL context ownership wiring', () => {
  it('gives visible sessions priority and evicts hidden context holders at the cap', () => {
    expect(managerSource).toContain('function evictWebglContextForPrioritySession()');
    expect(managerSource).toContain('!webglPrioritySessionIds.has(candidateId)');
    expect(managerSource).toContain(
      "if (!hasWebglPriority(sessionId, state) || !evictWebglContextForPrioritySession()) {",
    );
  });

  it('retries a lost WebGL context with backoff instead of permanent DOM downgrade', () => {
    expect(managerSource).toContain('function scheduleWebglReattach(');
    expect(managerSource).toContain('recordWebglContextLoss(sessionId)');
    expect(managerSource).toContain('WEBGL_LOSS_SLOW_RETRY_THRESHOLD');
    expect(managerSource).toContain(
      'Math.min(delayMs * 2, WEBGL_REATTACH_MAX_DELAY_MS)',
    );
  });

  it('re-attempts WebGL attach on foreground recovery after an earlier loss', () => {
    expect(managerSource).toContain(
      '// A context lost while backgrounded (or denied at open) must come back as',
    );
    expect(managerSource).toMatch(
      /if \(!state\.hasWebgl\) \{[\s\S]*?if \(!shouldUseWebglRenderer\(settings\) \|\| !attachWebglAddon\(sessionId, state\)\) \{/,
    );
  });

  it('leaves aux terminals outside session wrappers unmanaged', () => {
    expect(managerSource).toContain("state.container.closest('.session-wrapper')");
  });

  it('feeds visible session ids from the visibility sync into the WebGL priority set', () => {
    expect(mainSource).toContain('syncWebglSessionPriority([...prioritySessionIds])');
    expect(mainSource).toMatch(
      /const prioritySessionIds = new Set\(visibleSessionIds\);[\s\S]*?prioritySessionIds\.add\(activeSessionId\);/,
    );
  });
});
