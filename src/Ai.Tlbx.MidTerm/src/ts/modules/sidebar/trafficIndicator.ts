/**
 * Traffic Indicator Module
 *
 * Displays WebSocket traffic rate in sidebar footer from WebSocket byte events.
 * Uses DIRECT DOM manipulation - no reactive stores for display value.
 * Completely isolated from sidebar rendering.
 */
import { onWsTraffic, resetWsAccum, setWsRateEma } from '../../state';
import { $muxWsConnected } from '../../stores';

const UPDATE_MS = 500;

let sampleTimerId: number | null = null;
let idleTimerId: number | null = null;
let el: HTMLSpanElement | null = null;
let lastText = '';
let unsubscribeTraffic: (() => void) | null = null;

function formatRate(bps: number): string {
  if (bps < 1) return '0 B/s';
  if (bps < 1000) return `${Math.round(bps)} B/s`;
  if (bps < 1000000) return `${(bps / 1000).toFixed(1)} KB/s`;
  return `${(bps / 1000000).toFixed(2)} MB/s`;
}

function setText(text: string): void {
  if (text !== lastText && el) {
    el.textContent = text;
    lastText = text;
  }
}

function clearSampleTimer(): void {
  if (sampleTimerId !== null) {
    window.clearTimeout(sampleTimerId);
    sampleTimerId = null;
  }
}

function clearIdleTimer(): void {
  if (idleTimerId !== null) {
    window.clearTimeout(idleTimerId);
    idleTimerId = null;
  }
}

function markIdle(): void {
  idleTimerId = null;
  setWsRateEma(0, 0);
  setText('0 B/s');
}

function sampleTraffic(): void {
  sampleTimerId = null;
  const { tx, rx } = resetWsAccum();
  const txRate = (tx / UPDATE_MS) * 1000;
  const rxRate = (rx / UPDATE_MS) * 1000;

  setWsRateEma(txRate, rxRate);
  setText(formatRate(txRate + rxRate));

  clearIdleTimer();
  idleTimerId = window.setTimeout(markIdle, UPDATE_MS);
}

function scheduleTrafficSample(): void {
  clearIdleTimer();
  if (sampleTimerId === null) {
    sampleTimerId = window.setTimeout(sampleTraffic, UPDATE_MS);
  }
}

export function initTrafficIndicator(): void {
  const wsTraffic = document.getElementById('ws-traffic');
  if (!wsTraffic) return;
  el = wsTraffic as HTMLSpanElement;

  unsubscribeTraffic = onWsTraffic(scheduleTrafficSample);

  $muxWsConnected.subscribe((connected) => {
    if (el) el.style.opacity = connected ? '1' : '0.3';
    if (!connected) {
      setWsRateEma(0, 0);
      lastText = '';
      clearSampleTimer();
      clearIdleTimer();
      if (el) el.textContent = '0 B/s';
    }
  });
}

export function destroyTrafficIndicator(): void {
  clearSampleTimer();
  clearIdleTimer();
  unsubscribeTraffic?.();
  unsubscribeTraffic = null;
}
