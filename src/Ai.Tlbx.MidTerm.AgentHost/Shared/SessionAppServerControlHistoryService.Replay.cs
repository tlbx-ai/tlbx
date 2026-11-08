using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class SessionAppServerControlHistoryService
{
    // Initial attachment is a canonical import, not a live replay for every subscriber.
    // The regular attach window RPC supplies the bounded viewport after this metadata/tail patch.
    public void AppendInitialReplay(IReadOnlyList<AppServerControlProviderEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;
        var sessionId = events[0].SessionId;
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (events.Any(e => !string.Equals(e.SessionId, sessionId, StringComparison.Ordinal)))
            throw new ArgumentException("Initial replay must belong to one session.", nameof(events));
        var log = GetOrLoadLog(sessionId);
        try
        {
            lock (log.SyncRoot)
            {
                if (log.Forgotten) return;
                foreach (var source in events)
                {
                    var retained = AppServerControlProviderEventCompaction.CloneForRetention(source);
                    retained.Sequence = ++log.NextSequence;
                    log.EventBacklog.Add(retained);
                    TrimEventBacklog(log.EventBacklog);
                    ApplyEvent(log.State, retained);
                    QueueJournalMutation(log, new() { Event = retained });
                }

                var window = CloneSnapshot(sessionId, log.NextSequence, log.State,
                    Math.Max(0, log.State.HistoryEntries.Count - DefaultHistoryWindowSize), DefaultHistoryWindowSize, null);
                var delta = new AppServerControlHistoryPatch
                {
                    SessionId = sessionId,
                    Provider = window.Provider,
                    GeneratedAt = window.GeneratedAt,
                    LatestSequence = window.LatestSequence,
                    HistoryCount = window.HistoryCount,
                    Session = window.Session,
                    Thread = window.Thread,
                    CurrentTurn = window.CurrentTurn,
                    QuickSettings = window.QuickSettings,
                    Streams = window.Streams,
                    HistoryUpserts = window.History,
                    ItemUpserts = window.Items,
                    RequestUpserts = window.Requests,
                    NoticeUpserts = window.Notices
                };
                PersistScreenLogDelta(log, delta);
                foreach (var subscriber in log.DeltaSubscribers.ToArray())
                {
                    if (!subscriber.Writer.TryWrite(CloneDelta(delta))) log.DeltaSubscribers.Remove(subscriber);
                }
            }
        }
        finally
        {
            // Preserve already reduced canonical data even if a malformed later event aborts attach.
            SchedulePersistence(log);
        }
    }
}
