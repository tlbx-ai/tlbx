import { describe, expect, it } from 'vitest';
import { commandPreview, tokenizeCommand } from './commandHighlight';

describe('compact command presentation', () => {
  it('exposes the script past a PowerShell launcher and here-string opening', () => {
    expect(
      commandPreview(
        '"C:\\Program Files\\PowerShell\\7\\pwsh.exe" -Command "@\'\r\nGet-Content data.json\r\n',
      ),
    ).toBe('Get-Content data.json');
    expect(commandPreview('bash -lc git status')).toBe('git status');
    expect(commandPreview('git status\n git diff')).toBe('git status git diff');
    expect(commandPreview('x'.repeat(900))).toHaveLength(600);
    expect(commandPreview('x'.repeat(900)).endsWith('…')).toBe(true);
  });

  it('preserves literal source and Unicode through highlighting, including the work limit', () => {
    for (const source of [
      '$p = "Grüße 😀 <script>alert(1)</script>"; if ($p) { Write-Output $p } # comment',
      "printf '%s' '$literal'; echo 42 | cat\n",
      'x'.repeat(40_000) + '\n$tail = 3',
      '"' + '`!'.repeat(10_000),
    ]) {
      expect(
        tokenizeCommand(source)
          .map((token) => token.text)
          .join(''),
      ).toBe(source);
    }
    const tokens = tokenizeCommand('$p = 42; if ($p) { echo "ok" } # comment');
    expect(tokens.filter((token) => token.kind).map((token) => token.kind)).toEqual(
      expect.arrayContaining(['variable', 'number', 'keyword', 'string', 'comment']),
    );
  });
});
