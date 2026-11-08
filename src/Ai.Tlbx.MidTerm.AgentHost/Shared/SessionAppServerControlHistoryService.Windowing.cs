using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class SessionAppServerControlHistoryService
{
    private static void SetHistoryEntry(AppServerControlConversationState state, AppServerControlHistoryItem entry)
    {
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
        if (state.HistoryOrderKeys.Remove(entryId, out var key)) state.HistoryOrder.Remove(key);
        state.HistoryEntries.Remove(entryId);
    }

    private static void ClearHistoryEntries(AppServerControlConversationState state)
    {
        state.HistoryEntries.Clear();
        state.HistoryOrder.Clear();
        state.HistoryOrderKeys.Clear();
    }

    private static void RebuildHistoryOrderIndex(AppServerControlConversationState state)
    {
        // A turn-lead promotion can change many keys. Rebuild once instead of shifting the
        // SortedList arrays for every changed key (quadratic for a large promoted range).
        state.HistoryOrder.Clear();
        state.HistoryOrderKeys.Clear();
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
        foreach (var alias in item.AliasItemIds)
        {
            if (alias != item.ItemId) state.ItemAliases[alias] = item.ItemId;
        }
        if (IsCurrentActivityItem(state, item)) state.ActiveItemIds.Add(item.ItemId);
        else state.ActiveItemIds.Remove(item.ItemId);
    }

    private static void RemoveItemSummary(AppServerControlConversationState state, string itemId)
    {
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
}
