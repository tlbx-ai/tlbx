export function resolveMuxDataLossReason(reasonCode: number): string {
  switch (reasonCode) {
    case 1:
      return 'mthost_ipc_overflow';
    case 2:
      return 'mux_overflow';
    case 3:
      return 'browser_pending_overflow';
    case 4:
      return 'ipc_timeout_reconnect';
    case 5:
      return 'buffer_refresh_tail_replay';
    case 6:
      return 'reconnect_tail_replay';
    case 7:
      return 'quick_resume_tail_replay';
    default:
      return 'manual';
  }
}
