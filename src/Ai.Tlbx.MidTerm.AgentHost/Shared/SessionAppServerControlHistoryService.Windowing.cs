using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class SessionAppServerControlHistoryService
{
    private static void SetHistoryEntry(AppServerControlConversationState state, AppServerControlHistoryItem entry)
    {
        RefreshHistoryIndexes(state, entry);
        var key = (entry.Order, entry.EntryId);
        if (state.HistoryOrderKeys.TryGetValue(entry.EntryId, out var previousKey))
        {
            if (previousKey == key)
            {
                if (!ReferenceEquals(state.HistoryEntries[entry.EntryId], entry))
                    state.HistoryEntries[entry.EntryId] = entry;
                return;
            }
            state.HistoryOrder.Remove(previousKey);
        }
        if (!state.HistoryEntries.TryGetValue(entry.EntryId, out var previous) || !ReferenceEquals(previous, entry))
            state.HistoryEntries[entry.EntryId] = entry;
        state.HistoryOrderKeys[entry.EntryId] = key;
        state.HistoryOrder.Add(key, entry.EntryId);
    }

    private static void RemoveHistoryEntry(AppServerControlConversationState state, string entryId)
    {
        RemoveHistoryIndexes(state, entryId);
        if (state.HistoryOrderKeys.Remove(entryId, out var key)) state.HistoryOrder.Remove(key);
        state.HistoryEntries.Remove(entryId);
    }

    private static void ClearHistoryEntries(AppServerControlConversationState state)
    {
        state.HistoryEntries.Clear();
        state.HistoryOrder.Clear();
        state.HistoryOrderKeys.Clear();
        state.HistoryByTurn.Clear();
        state.HistoryByTurnKind.Clear();
        state.HistoryIndexKeys.Clear();
        state.StreamingHistoryIds.Clear();
        state.ProvisionalToolIds.Clear();
    }

    private static void RebuildHistoryOrderIndex(AppServerControlConversationState state)
    {
        // A turn-lead promotion can change many keys. Rebuild once instead of shifting the
        // SortedList arrays for every changed key (quadratic for a large promoted range).
        state.HistoryOrder.Clear();
        state.HistoryOrderKeys.Clear();
        state.HistoryByTurn.Clear();
        state.HistoryByTurnKind.Clear();
        state.HistoryIndexKeys.Clear();
        state.StreamingHistoryIds.Clear();
        state.ProvisionalToolIds.Clear();
        foreach (var entry in state.HistoryEntries.Values.OrderBy(entry => entry.Order)
                     .ThenBy(entry => entry.EntryId, StringComparer.Ordinal))
        {
            SetHistoryEntry(state, entry);
        }
    }

    private static bool IsCurrentActivityItem(AppServerControlConversationState state, AppServerControlItemSummary item)
    {
        if (IsTerminalStatus(state.CurrentTurn.State) ||
            (!string.IsNullOrWhiteSpace(item.TurnId) && !string.IsNullOrWhiteSpace(state.CurrentTurn.TurnId) &&
             !string.Equals(item.TurnId, state.CurrentTurn.TurnId, StringComparison.Ordinal))) return false;
        return item.Status.Trim().ToLowerInvariant() switch
        {
            "active" or "in progress" or "inprogress" or "in_progress" or "open" or
                "running" or "starting" or "started" or "streaming" => true,
            _ => false
        };
    }

    private static void RefreshItemIndexes(AppServerControlConversationState state, AppServerControlItemSummary item)
    {
        if (state.UserItemTurns.TryGetValue(item.ItemId, out var oldTurn) &&
            (item.ItemType != "user_message" || oldTurn != (item.TurnId ?? string.Empty)))
        {
            state.UserItemTurns.Remove(item.ItemId);
            if (state.UserItemsByTurn.TryGetValue(oldTurn, out var oldIds))
            {
                oldIds.Remove(item.ItemId);
                if (oldIds.Count == 0) state.UserItemsByTurn.Remove(oldTurn);
            }
        }
        if (item.ItemType == "user_message")
        {
            var turn = item.TurnId ?? string.Empty;
            if (!state.UserItemsByTurn.TryGetValue(turn, out var ids)) state.UserItemsByTurn[turn] = ids = new(StringComparer.Ordinal);
            ids.Add(item.ItemId);
            state.UserItemTurns[item.ItemId] = turn;
        }
        foreach (var alias in item.AliasItemIds)
        {
            if (alias != item.ItemId) state.ItemAliases[alias] = item.ItemId;
        }
        if (IsCurrentActivityItem(state, item)) state.ActiveItemIds.Add(item.ItemId);
        else state.ActiveItemIds.Remove(item.ItemId);
    }

    private static void RemoveItemSummary(AppServerControlConversationState state, string itemId)
    {
        if (state.UserItemTurns.Remove(itemId, out var turn) && state.UserItemsByTurn.TryGetValue(turn, out var ids))
        {
            ids.Remove(itemId);
            if (ids.Count == 0) state.UserItemsByTurn.Remove(turn);
        }
        if (state.Items.Remove(itemId, out var previous))
        {
            foreach (var alias in previous.AliasItemIds.Append(itemId))
            {
                if (state.ItemAliases.TryGetValue(alias, out var owner) && owner == itemId)
                    state.ItemAliases.Remove(alias);
            }
        }
        state.ActiveItemIds.Remove(itemId);
    }

    private static void SetItemSummary(AppServerControlConversationState state, AppServerControlItemSummary item)
    {
        RemoveItemSummary(state, item.ItemId);
        state.Items[item.ItemId] = item;
        RefreshItemIndexes(state, item);
    }

    private static void RefreshRequestIndex(AppServerControlConversationState state, AppServerControlRequestSummary request)
    {
        if (string.Equals(request.State, "open", StringComparison.OrdinalIgnoreCase))
            state.OpenRequestIds.Add(request.RequestId);
        else state.OpenRequestIds.Remove(request.RequestId);
    }

    private static void PruneInactiveItemIndex(AppServerControlConversationState state)
    {
        foreach (var id in state.ActiveItemIds.ToArray())
        {
            if (!state.Items.TryGetValue(id, out var item) || !IsCurrentActivityItem(state, item))
                state.ActiveItemIds.Remove(id);
        }
    }

    private static List<AppServerControlItemSummary> CloneWindowItems(
        AppServerControlConversationState state, IReadOnlyList<AppServerControlHistoryItem> history)
    {
        var ids = new HashSet<string>(state.ActiveItemIds, StringComparer.Ordinal);
        foreach (var entry in history)
        {
            if (entry.ItemId is not { } itemId) continue;
            if (state.Items.ContainsKey(itemId)) ids.Add(itemId);
            else if (state.ItemAliases.TryGetValue(itemId, out var canonicalId)) ids.Add(canonicalId);
        }
        return ids.Select(id => state.Items[id]).OrderByDescending(item => item.UpdatedAt)
            .Select(CloneItemSummary).ToList();
    }

    private static List<AppServerControlRequestSummary> CloneWindowRequests(
        AppServerControlConversationState state, IReadOnlyList<AppServerControlHistoryItem> history)
    {
        var ids = new HashSet<string>(state.OpenRequestIds, StringComparer.Ordinal);
        foreach (var entry in history)
        {
            if (entry.RequestId is { } requestId && state.Requests.ContainsKey(requestId)) ids.Add(requestId);
        }
        return ids.Select(id => state.Requests[id]).OrderByDescending(request => request.UpdatedAt)
            .Select(CloneRequestSummary).ToList();
    }

    private static void RemoveHistoryIndexes(AppServerControlConversationState state, string id)
    {
        if (state.HistoryIndexKeys.Remove(id, out var previous) && state.HistoryByTurn.TryGetValue(previous.Turn, out var entries))
        {
            entries.Remove((previous.Order, id));
            if (entries.Count == 0) state.HistoryByTurn.Remove(previous.Turn);
            var kindKey = (previous.Turn, previous.Kind);
            if (state.HistoryByTurnKind.TryGetValue(kindKey, out var kindEntries))
            {
                kindEntries.Remove((previous.Order, id));
                if (kindEntries.Count == 0) state.HistoryByTurnKind.Remove(kindKey);
            }
        }
        state.StreamingHistoryIds.Remove(id);
        state.ProvisionalToolIds.Remove(id);
    }

    private static void RefreshHistoryIndexes(AppServerControlConversationState state, AppServerControlHistoryItem entry)
    {
        RemoveHistoryIndexes(state, entry.EntryId);
        var turn = entry.TurnId ?? string.Empty;
        if (!state.HistoryByTurn.TryGetValue(turn, out var entries))
            state.HistoryByTurn[turn] = entries = new(state.HistoryOrder.Comparer);
        entries.Add((entry.Order, entry.EntryId));
        if (entry.Kind is "assistant" or "reasoning" && entry.Body.Length > 0)
        {
            var kindKey = (turn, entry.Kind);
            if (!state.HistoryByTurnKind.TryGetValue(kindKey, out var kindEntries))
                state.HistoryByTurnKind[kindKey] = kindEntries = new(state.HistoryOrder.Comparer);
            kindEntries.Add((entry.Order, entry.EntryId));
        }
        state.HistoryIndexKeys[entry.EntryId] = (turn, entry.Kind, entry.Order);
        if (entry.Streaming) state.StreamingHistoryIds.Add(entry.EntryId);
        if (entry.Kind == "tool" && entry.Streaming && string.IsNullOrWhiteSpace(entry.ItemId))
            state.ProvisionalToolIds.Add(entry.EntryId);
    }

    private static string CollectTurnStreamTail(AppServerControlConversationState state, AppServerControlHistoryItem entry, int budget)
    {
        RefreshHistoryIndexes(state, entry);
        if (!state.HistoryByTurnKind.TryGetValue((entry.TurnId ?? string.Empty, entry.Kind), out var entries)) return string.Empty;
        var parts = new List<string>();
        // Normalization can halve CRLF input. Retain enough raw tail to preserve the exact
        // existing normalized truncation/marker result, including imported unnormalized bodies.
        var remaining = (budget + 1) * 2;
        foreach (var key in entries.Reverse())
        {
            var candidate = state.HistoryEntries[key.EntryId];
            if (candidate.Kind != entry.Kind || candidate.Body.Length == 0) continue;
            var take = Math.Min(remaining, candidate.Body.Length);
            parts.Add(candidate.Body[^take..]);
            remaining -= take;
            if (remaining == 0) break;
        }
        parts.Reverse();
        return string.Concat(parts);
    }
}
