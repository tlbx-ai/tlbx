export type SessionDirection = 'up' | 'left' | 'down' | 'right';

interface SessionPane {
  id: string;
  left: number;
  top: number;
  width: number;
  height: number;
}

/** Prefer a pane in the same row/column, then the nearest directional neighbor. */
export function directionalSession(
  panes: SessionPane[],
  active: string,
  direction: SessionDirection,
): string | null {
  const origin = panes.find((pane) => pane.id === active);
  if (!origin) return null;
  const vertical = direction === 'up' || direction === 'down';
  const sign = direction === 'up' || direction === 'left' ? -1 : 1;
  const center = (pane: SessionPane): number =>
    vertical ? pane.top + pane.height / 2 : pane.left + pane.width / 2;
  const crossStart = (pane: SessionPane): number => (vertical ? pane.left : pane.top);
  const crossEnd = (pane: SessionPane): number =>
    crossStart(pane) + (vertical ? pane.width : pane.height);
  const crossCenter = (pane: SessionPane): number => (crossStart(pane) + crossEnd(pane)) / 2;
  return (
    panes
      .filter((pane) => pane.id !== active && sign * (center(pane) - center(origin)) > 1)
      .map((pane) => ({
        id: pane.id,
        aligned: crossStart(pane) < crossEnd(origin) && crossEnd(pane) > crossStart(origin),
        distance: Math.hypot(
          center(pane) - center(origin),
          crossCenter(pane) - crossCenter(origin),
        ),
      }))
      .sort((a, b) => Number(b.aligned) - Number(a.aligned) || a.distance - b.distance)[0]?.id ??
    null
  );
}
