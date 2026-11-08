export interface AnnotationPoint { x: number; y: number; pressure: number }
export interface AnnotationStroke { points: AnnotationPoint[]; color: string; width: number }

export function annotationHasChanges(strokes: readonly AnnotationStroke[]): boolean {
  return strokes.some((stroke) => stroke.points.length > 0);
}

export function annotationPoint(
  clientX: number, clientY: number, pressure: number,
  rect: Pick<DOMRect, 'left' | 'top' | 'width' | 'height'>,
): AnnotationPoint {
  const clamp = (value: number): number => Math.max(0, Math.min(1, value));
  return { x: clamp((clientX - rect.left) / Math.max(1, rect.width)),
    y: clamp((clientY - rect.top) / Math.max(1, rect.height)), pressure: pressure > 0 ? clamp(pressure) : 0.5 };
}

export function undoAnnotationStroke(strokes: readonly AnnotationStroke[]): AnnotationStroke[] {
  return strokes.slice(0, -1);
}

export function drawAnnotationStroke(context: CanvasRenderingContext2D, stroke: AnnotationStroke): void {
  const first = stroke.points[0];
  if (!first) return;
  const width = context.canvas.width;
  const height = context.canvas.height;
  context.strokeStyle = context.fillStyle = stroke.color;
  context.lineCap = context.lineJoin = 'round';
  context.lineWidth = Math.max(1, stroke.width * Math.min(width, height));
  if (stroke.points.every((point) => point.x === first.x && point.y === first.y)) {
    context.beginPath(); context.arc(first.x * width, first.y * height, context.lineWidth / 2, 0, Math.PI * 2); context.fill();
    return;
  }
  context.beginPath(); context.moveTo(first.x * width, first.y * height);
  for (const point of stroke.points.slice(1)) context.lineTo(point.x * width, point.y * height);
  context.stroke();
}
