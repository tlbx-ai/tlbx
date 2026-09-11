using System.Globalization;
using System.Text;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>Bounded text-cell state for activity comparison, not a terminal renderer.
/// Synchronized paints compare the final text with the cells before the paint,
/// so erase-and-repaint does not invent activity. Colors and graphics are absent.</summary>
internal sealed class TerminalTextScreen
{
    private int[] _cells = [];
    private int _cols, _rows, _x, _y, _savedX, _savedY;
    private int _top, _bottom;
    private bool _sync;
    private bool _frameScrolled;
    private string? _beforeClear;
    private int[] _before = [];
    private readonly HashSet<int> _written = [];

    public void Resize(int cols, int rows)
    {
        cols = Math.Clamp(cols, 1, 1024); rows = Math.Clamp(rows, 1, 1024);
        if (cols == _cols && rows == _rows) return;
        if (_cells.Length > 0) _beforeClear ??= TextContent();
        var next = new int[cols * rows];
        for (var y = 0; y < Math.Min(rows, _rows); y++)
            Array.Copy(_cells, y * _cols, next, y * cols, Math.Min(cols, _cols));
        _cells = next; _cols = cols; _rows = rows; _top = 0; _bottom = rows - 1;
        _x = Math.Min(_x, cols - 1); _y = Math.Min(_y, rows - 1);
        _before = (int[])next.Clone(); _written.Clear();
    }

    public int Put(Rune rune, bool text)
    {
        var v = rune.Value;
        if (v < 32 || v == 127)
        {
            switch (v)
            {
                case 8: _x = Math.Max(0, _x - 1); break;
                case 9: _x = Math.Min(_cols - 1, (_x / 8 + 1) * 8); break;
                case 10: case 11: case 12: LineFeed(); break;
                case 13: _x = 0; break;
            }
            return 0;
        }
        var combining = Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark;
        if (_x >= _cols && !combining) { _x = 0; LineFeed(); }
        var index = _y * _cols + (combining ? Math.Max(0, _x - 1) : _x);
        // Combining marks form part of the cell's text identity.
        var value = text ? combining ? unchecked((_cells[index] * 397) ^ v) : v : 0;
        if (!_sync && text) _beforeClear = null;
        var changed = text && _cells[index] != value;
        Set(index, value);
        if (_sync && text) _written.Add(index);
        if (!combining)
        {
            // Wide East Asian and supplementary pictographic ranges occupy two cells.
            var wide = v >= 0x1100 && (v <= 0x115f || v is 0x2329 or 0x232a ||
                v is >= 0x2e80 and <= 0xa4cf || v is >= 0xac00 and <= 0xd7a3 ||
                v is >= 0xf900 and <= 0xfaff || v is >= 0xfe10 and <= 0xfe6f ||
                v is >= 0xff01 and <= 0xff60 || v is >= 0xffe0 and <= 0xffe6 || v >= 0x1f000);
            _x += wide ? 2 : 1;
            if (wide && index % _cols + 1 < _cols) Set(index + 1, 0);
        }
        return !_sync && changed ? 1 : 0;
    }

    public int Command(char final, ReadOnlySpan<int> p, bool privateMode)
    {
        var n = p.Length > 0 ? Math.Max(1, p[0]) : 1;
        var raw = p.Length > 0 ? p[0] : 0;
        if (privateMode)
        {
            foreach (var mode in p)
            {
                if (mode == 2026 && final == 'h' && !_sync)
                {
                    // Snapshot once per paint, not once per changed/scrolled cell.
                    // This keeps scrolling output on the bulk-copy path as well.
                    Array.Copy(_cells, _before, _cells.Length);
                    _sync = true;
                }
                if (mode == 2026 && final == 'l')
                {
                    var count = 0;
                    foreach (var index in _written)
                        if (_cells[index] != 0 && (_frameScrolled || _cells[index] != _before[index])) count++;
                    if (_beforeClear is { } previous)
                    {
                        // A full repaint can move retained text vertically (e.g. a
                        // shell prompt entering scrollback). Compare ordered text,
                        // not its former row numbers, only at this explicit clear.
                        var current = TextContent();
                        if (previous.EndsWith(current, StringComparison.Ordinal)) count = 0;
                        _beforeClear = null;
                    }
                    _written.Clear(); _sync = false; _frameScrolled = false;
                    return count;
                }
                if (mode is 47 or 1047 or 1049 && final is 'h' or 'l')
                {
                    Array.Clear(_cells); Array.Clear(_before); _written.Clear(); _x = _y = 0;
                }
            }
            return 0;
        }
        switch (final)
        {
            case 'A': _y = Math.Max(_top, _y - n); break;
            case 'B': case 'e': _y = Math.Min(_bottom, _y + n); break;
            case 'C': case 'a': _x = Math.Min(_cols - 1, _x + n); break;
            case 'D': _x = Math.Max(0, _x - n); break;
            case 'E': _y = Math.Min(_bottom, _y + n); _x = 0; break;
            case 'F': _y = Math.Max(_top, _y - n); _x = 0; break;
            case 'G': case '`': _x = Math.Clamp(n - 1, 0, _cols - 1); break;
            case 'd': _y = Math.Clamp(n - 1, 0, _rows - 1); break;
            case 'H': case 'f':
                _y = Math.Clamp(n - 1, 0, _rows - 1);
                _x = Math.Clamp((p.Length > 1 ? Math.Max(1, p[1]) : 1) - 1, 0, _cols - 1); break;
            case 'J':
                if (raw == 2 || (raw == 0 && _x == 0 && _y == 0)) _beforeClear ??= TextContent();
                var cursor = _y * _cols + Math.Min(_x, _cols - 1);
                if (raw == 0) Clear(cursor, _cells.Length);
                else if (raw == 1) Clear(0, cursor + 1);
                else if (raw == 2) Clear(0, _cells.Length);
                break;
            case 'K':
                var start = _y * _cols;
                Clear(start + (raw == 0 ? Math.Min(_x, _cols - 1) : 0), start + (raw == 1 ? Math.Min(_x + 1, _cols) : _cols)); break;
            case 'X': Clear(_y * _cols + Math.Min(_x, _cols - 1), _y * _cols + Math.Min(_cols, _x + n)); break;
            case 'S': Scroll(_top, _bottom, Math.Min(n, _bottom - _top + 1)); break;
            case 'T': Scroll(_top, _bottom, -Math.Min(n, _bottom - _top + 1)); break;
            case 'L': Scroll(_y, _bottom, -Math.Min(n, _bottom - _y + 1)); break;
            case 'M': Scroll(_y, _bottom, Math.Min(n, _bottom - _y + 1)); break;
            case 'P': case '@':
                var row = _y * _cols; var x = Math.Min(_x, _cols - 1); n = Math.Min(n, _cols - x);
                if (final == 'P') { for (var i = x; i < _cols - n; i++) Set(row + i, _cells[row + i + n]); Clear(row + _cols - n, row + _cols); }
                else { for (var i = _cols - 1; i >= x + n; i--) Set(row + i, _cells[row + i - n]); Clear(row + x, row + x + n); }
                break;
            case 'r': _top = Math.Clamp(n - 1, 0, _rows - 1); _bottom = Math.Clamp((p.Length > 1 && p[1] > 0 ? p[1] : _rows) - 1, _top, _rows - 1); _x = _y = 0; break;
            case 's': _savedX = _x; _savedY = _y; break;
            case 'u': _x = Math.Min(_savedX, _cols - 1); _y = Math.Min(_savedY, _rows - 1); break;
        }
        return 0;
    }

    private string TextContent()
    {
        var text = new StringBuilder();
        foreach (var cell in _cells)
            if (cell != 0) text.Append(cell).Append(',');
        return text.ToString();
    }

    private void Set(int index, int value)
    {
        if (_cells[index] == value) return;
        _cells[index] = value;
    }
    private void Clear(int start, int end)
    {
        Array.Clear(_cells, start, end - start);
    }
    private void LineFeed() { if (_y == _bottom) Scroll(_top, _bottom, 1); else _y = Math.Min(_rows - 1, _y + 1); }
    private void Scroll(int top, int bottom, int lines)
    {
        if (_sync && lines > 0) _frameScrolled = true;
        var start = top * _cols; var end = (bottom + 1) * _cols; var shift = Math.Abs(lines) * _cols;
        // Array.Copy handles the overlapping scroll region in either direction.
        if (lines > 0) { Array.Copy(_cells, start + shift, _cells, start, end - start - shift); Clear(end - shift, end); }
        else { Array.Copy(_cells, start, _cells, start + shift, end - start - shift); Clear(start, start + shift); }
    }
}
