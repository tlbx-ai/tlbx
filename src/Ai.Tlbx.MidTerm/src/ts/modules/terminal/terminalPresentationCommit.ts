import type {
  TerminalPresentationActionState,
  TerminalPresentationSnapshot,
} from './terminalPresentation';
import { clearTerminalGapFillers, updateTerminalGapFillers } from './terminalGapFillers';
import { createScalingOverlay, positionScalingOverlay } from './sizeControlOverlay';

type FitOwnedTerminal = (sessionId: string, container: HTMLElement) => void;

export interface TerminalPresentationCommitPlan {
  container: HTMLElement;
  xterm: HTMLElement;
  snapshot: TerminalPresentationSnapshot;
  hasOwner: boolean;
  label: string;
  mode: 'scaled-down' | 'undersized' | 'natural';
  scaleOwner: boolean;
  fitOwnedTerminal: FitOwnedTerminal;
  setActionState: (actionState: TerminalPresentationActionState) => void;
}

export function commitTerminalPresentationDom(plan: TerminalPresentationCommitPlan): void {
  const { container, xterm, snapshot } = plan;
  const ownsSize = snapshot.role === 'owner';
  // A desktop owner keeps natural cell size while reading history or waiting
  // for its resize acknowledgement. A height change must not shrink its width.
  const shouldScale = plan.mode === 'scaled-down' && (!ownsSize || plan.scaleOwner);

  if (shouldScale) {
    xterm.style.transform = `scale(${snapshot.passiveScale})`;
    xterm.style.transformOrigin = 'top left';
    container.classList.add('scaled');
    updateTerminalGapFillers(container, xterm, snapshot.passiveScale);
  } else {
    xterm.style.transform = '';
    xterm.style.transformOrigin = '';
    container.classList.remove('scaled');
    clearTerminalGapFillers(container);
    if (plan.mode !== 'scaled-down') updateTerminalGapFillers(container, xterm, 1);
  }

  let overlay = container.querySelector<HTMLButtonElement>('.scaled-overlay');
  if (!ownsSize && !plan.scaleOwner) {
    overlay ??= createScalingOverlay(container, plan.fitOwnedTerminal, plan.setActionState);
    positionScalingOverlay(overlay, {
      ownsSize: false,
      hasOwner: plan.hasOwner,
      ownerLabel: snapshot.ownerLabel,
      label: plan.label,
      actionState: snapshot.actionState,
    });
  } else if (overlay) {
    positionScalingOverlay(overlay, {
      ownsSize: true,
      hasOwner: true,
      ownerLabel: snapshot.ownerLabel,
      label: '',
      actionState: 'idle',
    });
  }

  if (typeof container.setAttribute === 'function') {
    container.setAttribute('data-terminal-presentation-role', snapshot.role);
    container.setAttribute('data-terminal-presentation-epoch', String(snapshot.epoch));
    container.setAttribute('data-terminal-presentation-action', snapshot.actionState);
  }
}
