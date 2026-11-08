using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class CodexAppServerControlAgentRuntime
{
    private readonly object _attachEventLock = new();
    private readonly Action<AppServerControlProviderEvent> _publish;
    private readonly Action<IReadOnlyList<AppServerControlProviderEvent>>? _publishInitialReplay;
    private List<AppServerControlProviderEvent>? _attachEvents;
    private string? _historyReadRequestId;
    private readonly List<JsonElement> _historyTurnPages = [];

    private async Task ReadCodexThreadHistoryAsync(string threadId, CancellationToken ct)
    {
        var metadata = await SendCodexRequestAsync("thread/read",
            id => BuildCodexThreadHistoryRequest(id, threadId, includeTurns: false), ct).ConfigureAwait(false);
        if (!string.Equals(GetString(metadata, "thread", "historyMode"), "paginated", StringComparison.Ordinal))
        {
            await SendCodexRequestAsync("thread/read", id =>
            {
                _historyReadRequestId = id;
                return BuildCodexThreadHistoryRequest(id, threadId, includeTurns: true);
            }, ct).ConfigureAwait(false);
            return;
        }

        _historyTurnPages.Clear();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await SendCodexRequestAsync("thread/turns/list", id =>
            {
                _historyReadRequestId = id;
                return BuildJsonString(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", id);
                    writer.WriteString("method", "thread/turns/list");
                    writer.WriteStartObject("params");
                    writer.WriteString("threadId", threadId);
                    writer.WriteString("sortDirection", "asc");
                    writer.WriteString("itemsView", "full");
                    writer.WriteNumber("limit", 100);
                    if (cursor is not null) writer.WriteString("cursor", cursor);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                });
            }, ct).ConfigureAwait(false);
            cursor = GetString(page, "nextCursor");
            if (cursor is not null && !cursors.Add(cursor))
            {
                throw new InvalidOperationException("Codex history pagination returned a repeated cursor.");
            }
        }
        while (cursor is not null);
    }

    private void HandleCodexHistoryReply(JsonElement result)
    {
        if (result.TryGetProperty("data", out var turns) && turns.ValueKind == JsonValueKind.Array)
        {
            using var enumerator = turns.EnumerateArray();
            foreach (var turn in enumerator) _historyTurnPages.Add(turn.Clone());
            if (GetString(result, "nextCursor") is not null) return;
            using var snapshot = JsonDocument.Parse(BuildJsonString(writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("thread");
                writer.WriteStartArray("turns");
                foreach (var turn in _historyTurnPages) turn.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }));
            RestoreCodexThreadHistory(snapshot.RootElement);
            _historyTurnPages.Clear();
        }
        else
        {
            RestoreCodexThreadHistory(result);
        }
    }

    private static string BuildCodexThreadHistoryRequest(string id, string threadId, bool includeTurns) => BuildJsonString(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("method", "thread/read");
        writer.WriteStartObject("params");
        writer.WriteString("threadId", threadId);
        writer.WriteBoolean("includeTurns", includeTurns);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    // Runs on the native reader at the response boundary. Notifications before
    // this response are covered by the snapshot; later deltas follow it in order.
    private void RestoreCodexThreadHistory(JsonElement result)
    {
        if (!result.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        lock (_attachEventLock)
        {
            var buffered = _attachEvents ?? [];
            _attachEvents = [];
            var restoredTurns = new HashSet<string>(StringComparer.Ordinal);
            _activeTurnId = null;
            using var turnEnumerator = turns.EnumerateArray();
            foreach (var turn in turnEnumerator)
            {
                var turnId = GetString(turn, "id");
                if (string.IsNullOrWhiteSpace(turnId))
                {
                    continue;
                }

                restoredTurns.Add(turnId);
                var status = GetString(turn, "status") ?? "completed";
                var active = string.Equals(status, "inProgress", StringComparison.Ordinal);
                if (active)
                {
                    _activeTurnId = turnId;
                }

                _emit(CreateEvent("turn.started", turnId, null, null, "codex.app-server.history", "thread/read", turn,
                    e =>
                    {
                        e.TurnStarted = new AppServerControlProviderTurnStartedPayload { IsReplay = true };
                        if (GetLong(turn, "startedAt") is long startedAt && startedAt is > 0 and < 253402300800L)
                        {
                            e.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(startedAt);
                        }
                    }));
                if (turn.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    using var itemEnumerator = items.EnumerateArray();
                    foreach (var item in itemEnumerator)
                    {
                        using var document = JsonDocument.Parse(BuildJsonString(writer =>
                        {
                            writer.WriteStartObject();
                            writer.WriteString("threadId", _providerThreadId);
                            writer.WriteString("turnId", turnId);
                            writer.WritePropertyName("item");
                            item.WriteTo(writer);
                            writer.WriteEndObject();
                        }));
                        if (NormalizeCodexItemType(GetString(item, "type")) is "assistant_message" or "agent_message")
                        {
                            _emit(CreateEvent("content.delta", turnId, GetString(item, "id"), null,
                                "codex.app-server.history", "thread/read", item,
                                e => e.ContentDelta = new AppServerControlProviderContentDeltaPayload
                                {
                                    StreamKind = "assistant_text",
                                    Delta = BuildCodexItemDetail(document.RootElement),
                                    Replace = true
                                }));
                        }

                        HandleCodexNotification("item/completed", document.RootElement);
                    }
                }

                if (!active)
                {
                    _emit(CreateEvent("turn.completed", turnId, null, null, "codex.app-server.history", "thread/read", turn,
                        e =>
                        {
                            e.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
                            {
                                State = status,
                                StateLabel = HumanizeTurnState(status),
                                ErrorMessage = GetString(turn, "error", "message")
                            };
                            if (GetLong(turn, "completedAt") is long completedAt && completedAt is > 0 and < 253402300800L)
                            {
                                e.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(completedAt);
                            }
                        }));
                }
            }

            foreach (var e in buffered)
            {
                if (e.SessionState is not null ||
                    (e.TurnId is { } turnId && restoredTurns.Contains(turnId) &&
                     (e.Item is not null || e.ContentDelta is not null || e.TurnStarted is not null ||
                      e.TurnCompleted is not null || e.PlanDelta is not null || e.PlanCompleted is not null)))
                {
                    continue;
                }

                _attachEvents.Add(e);
            }

            _emit(CreateEvent("session.state.changed", _activeTurnId, null, null, "codex.app-server.history", "thread/read", thread,
                e => e.SessionState = new AppServerControlProviderSessionStatePayload
                {
                    State = _activeTurnId is null ? "ready" : "running",
                    StateLabel = _activeTurnId is null ? "Ready" : "Running",
                    Reason = "Codex native history restored."
                }));
        }
    }

    private void PublishAttachedHistory(IEnumerable<AppServerControlProviderEvent> initialEvents)
    {
        lock (_attachEventLock)
        {
            if (_publishInitialReplay is not null && _attachEvents is { } replay)
            {
                var batch = initialEvents.ToList();
                batch.AddRange(replay);
                _publishInitialReplay(batch);
                _attachEvents = null;
                return;
            }
            foreach (var e in initialEvents)
            {
                _publish(e);
            }

            if (_attachEvents is { } buffered)
            {
                foreach (var e in buffered)
                {
                    _publish(e);
                }
            }

            _attachEvents = null;
        }
    }
}
