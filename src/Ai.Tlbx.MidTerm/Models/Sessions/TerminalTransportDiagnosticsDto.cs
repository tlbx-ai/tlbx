namespace Ai.Tlbx.MidTerm.Models.Sessions;

public sealed class TerminalTransportDiagnosticsDto
{
    public string SourceSeq { get; set; } = "0";
    public string MuxReceivedSeq { get; set; } = "0";
    public string MthostIpcQueuedSeq { get; set; } = "0";
    public string MthostIpcFlushedSeq { get; set; } = "0";
    public int IpcBacklogFrames { get; set; }
    public long IpcBacklogBytes { get; set; }
    public long OldestBacklogAgeMs { get; set; }
    public int ScrollbackBytes { get; set; }
    public int LastReplayBytes { get; set; }
    public string? LastReplayReason { get; set; }
    public int ReconnectCount { get; set; }
    public int DataLossCount { get; set; }
    public string? LastDataLossReason { get; set; }
    public long RecoveryRequested { get; set; }
    public long RecoveryCoalesced { get; set; }
    public long RecoveryCompleted { get; set; }
    public long RecoveryResets { get; set; }
    public long RecoveryReplayBytes { get; set; }
    public long RecoveryFailed { get; set; }
}
