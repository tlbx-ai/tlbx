/** Browser-side bindings never reserve browser navigation or ordinary terminal editing keys. */
export interface KeyStroke {
  key: string;
  code?: string;
  ctrlKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
  metaKey: boolean;
  isComposing?: boolean;
  repeat?: boolean;
  getModifierState?(key: string): boolean;
}

function physicalDirectionKey(event: KeyStroke): string | undefined {
  if (
    event.ctrlKey &&
    event.altKey &&
    !event.shiftKey &&
    !event.metaKey &&
    /^Key[WASD]$/.test(event.code ?? '')
  ) {
    return event.code?.slice(3);
  }
  return undefined;
}

export function normalizeBinding(event: KeyStroke): string | null {
  if (event.isComposing || event.key === 'Dead' || event.getModifierState?.('AltGraph'))
    return null;
  if (['Control', 'Shift', 'Alt', 'Meta', 'AltGraph', 'Unidentified'].includes(event.key))
    return null;
  const directionalKey = physicalDirectionKey(event);
  const key =
    directionalKey ??
    (event.key === ' ' ? 'Space' : event.key.length === 1 ? event.key.toUpperCase() : event.key);
  return [
    event.ctrlKey && 'Ctrl',
    event.altKey && 'Alt',
    event.shiftKey && 'Shift',
    event.metaKey && 'Meta',
    key,
  ]
    .filter(Boolean)
    .join('+');
}

/** Conservative across Chrome/Edge/Firefox/Safari. OS/user extensions may reserve more. */
export function bindingProblem(binding: string): 'browser' | 'modifier' | null {
  const parts = binding.split('+');
  const key = parts[parts.length - 1] ?? '';
  const primary = parts.includes('Ctrl') || parts.includes('Meta');
  const shift = parts.includes('Shift');
  const alt = parts.includes('Alt');
  if (/^Ctrl\+Alt\+[WASD]$/.test(binding)) return null;
  if (key === 'F2' && parts.length === 1) return null;
  if (key.startsWith('F') && /^F\d+$/.test(key)) return 'browser';
  if (!primary || !shift || alt) return 'modifier';
  if (['Tab', 'PageUp', 'PageDown', 'Escape', 'Backspace', 'Delete', 'Home', 'End'].includes(key))
    return 'browser';
  if (/^[0-9A-Z]$/.test(key)) return 'browser';
  if (!['Space', 'Enter', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(key))
    return 'browser';
  return null;
}

export function matchesSearch(query: string, ...text: string[]): boolean {
  const normalize = (value: string): string =>
    value
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLocaleLowerCase();
  const haystack = normalize(text.join(' '));
  return normalize(query)
    .trim()
    .split(/\s+/)
    .every((word) => haystack.includes(word));
}
