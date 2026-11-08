using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class SessionAppServerControlHistoryService
{
    private const int JournalFormatVersion = 1;
    private const int JournalHeaderBytes = 8;
    private const int JournalDigestBytes = 32;

    private string GetJournalStorePath(string sessionId) =>
        Path.Combine(_storeDirectory, Uri.EscapeDataString(sessionId) + ".journal");

    // Called under the reducer lock. Freeze only this mutation, not the canonical archive.
    // Its ordinal is independent of Sequence: imports/patches can share a provider sequence.
    private static void QueueJournalMutation(SessionAppServerControlHistoryLog log, HistoryJournalMutation mutation)
    {
        mutation.RecordNumber = checked(log.NextJournalRecord + 1);
        mutation.FormatVersion = JournalFormatVersion;
        mutation.LatestSequence = log.NextSequence;
        mutation.SessionId = log.SessionId;
        if (log.RecoveryCheckpointRequired)
        {
            // The reducer already owns the latest complete state. While storage is down,
            // coalesce there instead of retaining every delta payload a second time.
            log.NextJournalRecord = mutation.RecordNumber;
            return;
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(mutation,
            AppServerControlPersistenceJsonContext.Default.HistoryJournalMutation);
        log.PendingJournal.Enqueue(new(mutation.RecordNumber, payload));
        log.NextJournalRecord = mutation.RecordNumber;
    }

    private void SchedulePersistence(SessionAppServerControlHistoryLog log)
    {
        lock (log.SyncRoot)
        {
            if (log.Forgotten || log.PersistenceScheduled || !HasPendingPersistence(log)) return;
            log.PersistenceScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            var delay = PersistDebounceMilliseconds;
            while (true)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    FlushPendingJournal(log);
                    delay = PersistDebounceMilliseconds;
                }
                catch (Exception ex)
                {
                    // IO errors coalesce into a recovery checkpoint; other failures keep the batch.
                    Log.Verbose(() => $"[AppServerControlHistory] Failed to persist journal for {log.SessionId}: {ex.Message}");
                    delay = Math.Min(30000, Math.Max(1000, delay * 2));
                }

                lock (log.SyncRoot)
                {
                    if (!log.Forgotten && HasPendingPersistence(log)) continue;
                    log.PersistenceScheduled = false;
                    return;
                }
            }
        });
    }

    // Also permits deterministic persistence/IO-failure assertions in the existing fixtures.
    internal void FlushPersistence(string sessionId)
    {
        if (_logs.TryGetValue(sessionId, out var log)) FlushPendingJournal(log);
    }

    internal long GetPendingPersistenceBytes(string sessionId)
    {
        if (!_logs.TryGetValue(sessionId, out var log)) return 0;
        lock (log.SyncRoot) return log.PendingJournal.Sum(static mutation => mutation.Payload.LongLength);
    }

    private static bool HasPendingPersistence(SessionAppServerControlHistoryLog log) =>
        log.RecoveryCheckpointRequired || log.PendingJournal.Count > 0;

    private void FlushPendingJournal(SessionAppServerControlHistoryLog log)
    {
        // Forget waits for any in-flight append and removes the files before admitting a new log.
        lock (_storeSync)
        {
            lock (log.SyncRoot)
            {
                if (log.Forgotten || !_logs.TryGetValue(log.SessionId, out var current) || !ReferenceEquals(log, current)) return;
            }
            try
            {
                if (log.RecoveryCheckpointRequired) PersistRecoveryCheckpoint(log);
                else AppendPendingJournal(log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (log.SyncRoot)
                {
                    log.RecoveryCheckpointRequired = true;
                    log.PendingJournal.Clear();
                }
                throw;
            }
        }
    }

    private void AppendPendingJournal(SessionAppServerControlHistoryLog log)
    {
        PendingJournalMutation[] batch;
        lock (log.SyncRoot) batch = log.PendingJournal.ToArray();
        if (batch.Length == 0) return;
        long committedLength;
        using (var output = new FileStream(GetJournalStorePath(log.SessionId), FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None))
        {
            log.CommittedJournalLength ??= output.Length;
            output.SetLength(log.CommittedJournalLength.Value);
            output.Position = log.CommittedJournalLength.Value;
            foreach (var mutation in batch) WriteJournalRecord(output, mutation.Payload);
            output.Flush(flushToDisk: true);
            committedLength = output.Position;
        }
        log.CommittedJournalLength = committedLength;

        lock (log.SyncRoot)
        {
            foreach (var mutation in batch)
            {
                if (log.PendingJournal.Peek().RecordNumber != mutation.RecordNumber)
                    throw new InvalidOperationException("Canonical history journal order changed during persistence.");
                log.PendingJournal.Dequeue();
            }
        }
    }

    private void PersistRecoveryCheckpoint(SessionAppServerControlHistoryLog log)
    {
        // Confirm this directory is writable before cloning an archive on repeated open errors.
        using (File.Open(GetStateStorePath(log.SessionId) + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { }
        AppServerControlPersistedSessionState checkpoint;
        lock (log.SyncRoot)
        {
            checkpoint = BuildPersistedState(log);
            log.PendingJournal.Clear();
            // New events after this capture queue normally, after the checkpoint's watermark.
            log.RecoveryCheckpointRequired = false;
        }
        PersistState(log.SessionId, checkpoint);
        File.Delete(GetJournalStorePath(log.SessionId));
        log.CommittedJournalLength = null;
    }

    private static void WriteJournalRecord(Stream output, byte[] payload)
    {
        Span<byte> header = stackalloc byte[JournalHeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], ~payload.Length);
        output.Write(header);
        output.Write(payload);
        Span<byte> digest = stackalloc byte[JournalDigestBytes];
        SHA256.HashData(payload, digest);
        output.Write(digest);
    }

    private bool ReplayJournal(SessionAppServerControlHistoryLog log)
    {
        var path = GetJournalStorePath(log.SessionId);
        if (!File.Exists(path)) return false;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long previousRecord = 0;
        while (TryReadJournalRecord(input, out var payload))
        {
            var mutation = JsonSerializer.Deserialize(payload,
                AppServerControlPersistenceJsonContext.Default.HistoryJournalMutation)
                ?? throw new InvalidDataException("Null canonical history journal record.");
            ValidateJournalMutation(log, mutation, previousRecord);
            previousRecord = mutation.RecordNumber;
            // Checkpoint rename can succeed before old journal deletion. Its watermark wins.
            if (mutation.RecordNumber <= log.NextJournalRecord) continue;
            ApplyJournalMutation(log, mutation);
            log.NextJournalRecord = mutation.RecordNumber;
        }
        // Even an incomplete first/last frame is compacted away, never appended behind.
        return true;
    }

    private static bool TryReadJournalRecord(FileStream input, out byte[] payload)
    {
        payload = [];
        var remaining = input.Length - input.Position;
        if (remaining < JournalHeaderBytes) return false; // Only a torn terminal frame is tolerated.
        Span<byte> header = stackalloc byte[JournalHeaderBytes];
        input.ReadExactly(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != ~length)
            throw new InvalidDataException("Corrupt canonical history journal frame header.");
        if (input.Length - input.Position < (long)length + JournalDigestBytes) return false;
        payload = new byte[length];
        input.ReadExactly(payload);
        Span<byte> digest = stackalloc byte[JournalDigestBytes];
        Span<byte> expected = stackalloc byte[JournalDigestBytes];
        input.ReadExactly(digest);
        SHA256.HashData(payload, expected);
        if (!CryptographicOperations.FixedTimeEquals(digest, expected))
            throw new InvalidDataException("Corrupt canonical history journal record checksum.");
        return true;
    }

    private static void ValidateJournalMutation(SessionAppServerControlHistoryLog log, HistoryJournalMutation mutation, long previousRecord)
    {
        var payloadCount = (mutation.Event is null ? 0 : 1) + (mutation.Window is null ? 0 : 1) +
                           (mutation.Patch is null ? 0 : 1) + (mutation.Backlog is null ? 0 : 1);
        if (mutation.FormatVersion != JournalFormatVersion || payloadCount != 1 ||
            !string.Equals(mutation.SessionId, log.SessionId, StringComparison.Ordinal) ||
            mutation.RecordNumber <= 0 || mutation.LatestSequence < 0 ||
            (previousRecord > 0 && mutation.RecordNumber != previousRecord + 1) ||
            mutation.RecordNumber > log.NextJournalRecord + 1)
            throw new InvalidDataException("Invalid canonical history journal version, scope or order.");
        if (mutation.Event is { } e && (e.SessionId != log.SessionId || e.Sequence != mutation.LatestSequence) ||
            mutation.Window is { } w && w.SessionId != log.SessionId ||
            mutation.Patch is { } p && p.SessionId != log.SessionId)
            throw new InvalidDataException("Invalid canonical history journal payload scope.");
    }

    private static void ApplyJournalMutation(SessionAppServerControlHistoryLog log, HistoryJournalMutation mutation)
    {
        if (mutation.LatestSequence < log.NextSequence ||
            mutation.Event is not null && mutation.LatestSequence != log.NextSequence + 1)
            throw new InvalidDataException("Nonmonotone canonical history journal sequence.");
        if (mutation.Event is { } e)
        {
            // Already compacted on ingress. Recompacting here could change a persisted payload.
            ApplyEvent(log.State, e);
            log.EventBacklog.Add(e);
            TrimEventBacklog(log.EventBacklog);
        }
        else if (mutation.Window is { } window) ImportHistoryWindow(log.State, window);
        else if (mutation.Patch is { } patch) ApplyHistoryPatch(log.State, patch);
        else if (mutation.Backlog is { } backlog)
        {
            log.EventBacklog.Clear();
            log.EventBacklog.AddRange(backlog);
            TrimEventBacklog(log.EventBacklog);
        }
        log.NextSequence = mutation.LatestSequence;
    }

    private sealed record PendingJournalMutation(long RecordNumber, byte[] Payload);

    private sealed class HistoryJournalMutation
    {
        public int FormatVersion { get; set; }
        public long RecordNumber { get; set; }
        public long LatestSequence { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public AppServerControlProviderEvent? Event { get; init; }
        public AppServerControlHistoryWindowResponse? Window { get; init; }
        public AppServerControlHistoryPatch? Patch { get; init; }
        public List<AppServerControlProviderEvent>? Backlog { get; init; }
    }
}
