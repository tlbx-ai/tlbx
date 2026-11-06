using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ai.Tlbx.MidTerm.Services.Boards;

public sealed partial class BoardService
{
    private BoardItem? ReadStoredItem(string boardId, string itemId, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT document FROM board_items WHERE board_id=$id AND id=$item", boardId, transaction: transaction);
        command.Parameters.AddWithValue("$item", itemId);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize(json, BoardJsonContext.Default.BoardItem) : null;
    }

    public List<BoardLaunch> ListLaunches(string boardId, string? itemId = null, int limit = 50, int offset = 0)
    {
        if (limit is < 1 or > 1000 || offset < 0) throw new ArgumentException("Invalid launch history range.");
        lock (_gate)
        {
            using var command = Command("SELECT document FROM board_launches WHERE board_id=$id" +
                (itemId is null ? "" : " AND item_id=$item") + " ORDER BY created_at DESC,id DESC LIMIT $limit OFFSET $offset", boardId);
            if (itemId is not null) command.Parameters.AddWithValue("$item", itemId);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader();
            var launches = new List<BoardLaunch>();
            while (reader.Read()) launches.Add(JsonSerializer.Deserialize(reader.GetString(0), BoardJsonContext.Default.BoardLaunch)!);
            return launches;
        }
    }

    private void AttachLaunches(BoardDocument board)
    {
        foreach (var item in board.Items) item.Launches = ListLaunches(board.Id, item.Id, 20);
    }

    internal BoardLaunchStart BeginLaunch(string boardId, string itemId, BoardLaunchRequest request, string origin)
    {
        var id = request.RequestId ?? Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("requestId must be a 32-character UUID.");
        var requestJson = JsonSerializer.Serialize(request with { RequestId = id }, BoardJsonContext.Default.BoardLaunchRequest);
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            using (var existing = Command("SELECT request,document FROM board_launches WHERE id=$id", id, transaction: transaction))
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    var launch = JsonSerializer.Deserialize(reader.GetString(1), BoardJsonContext.Default.BoardLaunch)!;
                    if (launch.BoardId != boardId || launch.ItemId != itemId || reader.GetString(0) != requestJson)
                        throw new ArgumentException("This requestId was already used for a different launch.");
                    return new(launch, false);
                }
            }
            var board = ReadBoard(boardId, transaction) ?? throw new KeyNotFoundException("Board not found.");
            var item = ReadStoredItem(boardId, itemId, transaction) ?? throw new KeyNotFoundException("Item not found.");
            if (request.ExpectedRevision is not null) CheckRevision(item.Revision, request.ExpectedRevision);
            var entry = request.EntryId is null ? null : item.Entries.Find(value => value.Id == request.EntryId)
                ?? throw new ArgumentException("Entry not found.");
            var directory = request.WorkingDirectory ?? item.WorkingDirectory ?? board.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
                throw new ArgumentException("Choose an existing absolute session directory in board or item settings.");
            var controlOnly = request.AppServerControlOnly ?? item.AppServerControlOnly ?? board.AppServerControlOnly;
            var command = request.LaunchCommand ?? item.LaunchCommand ?? board.LaunchCommand;
            var profile = request.Profile ?? item.SessionProfile ?? board.SessionProfile;
            if (!controlOnly && string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Choose a session command in board or item settings.");
            if (controlOnly && string.IsNullOrWhiteSpace(profile)) throw new ArgumentException("Agent Controller sessions require an explicit profile.");
            if (command?.Length > 8192 || profile?.Length > 128) throw new ArgumentException("Session configuration is too large.");
            var url = $"{origin.TrimEnd('/')}/?board={boardId}&item={itemId}" + (entry is null ? "" : $"&entry={Uri.EscapeDataString(entry.Id)}");
            var prompt = $"Board: {board.Title}\nItem: {item.Title}\nReference: {url}\nBoard ID: {boardId}\nItem ID: {itemId}\n" +
                $"Read current context with native tlbx MCP board_item_get using boardId={boardId} and itemId={itemId} when available. " +
                $"The maintained CLI equivalent is: mt board item {boardId} {itemId}\n\n{item.Text}\n\n{item.Prompt}";
            if (entry is not null) prompt += FormattableString.Invariant($"\n\nTask entry ID: {entry.Id}\nTask: {entry.Text}\nDate: {entry.Date}; Year: {entry.Year}; ISO week: {entry.Week}; Quarter: {entry.Quarter}");
            else if (item.Entries.Count > 0) prompt += "\n\nEntries:\n" + string.Join("\n", item.Entries.Select(value => $"{(value.Done ? "[x]" : "[ ]")} {value.Text} (entry {value.Id})"));
            var now = DateTimeOffset.UtcNow;
            var created = new BoardLaunch
            {
                Id = id, BoardId = boardId, ItemId = itemId, EntryId = entry?.Id,
                Name = (entry?.Text is { Length: > 0 } text ? text : item.Title).Trim(),
                WorkingDirectory = directory, LaunchCommand = command, Profile = profile,
                AppServerControlOnly = controlOnly, Prompt = prompt, CreatedAt = now, UpdatedAt = now
            };
            if (created.Name.Length > 128) created.Name = created.Name[..128];
            if (created.Name.Length == 0) created.Name = board.Title;
            using var insert = Command("INSERT INTO board_launches VALUES($id,$board,$item,$request,$document,$created)", id,
                JsonSerializer.Serialize(created, BoardJsonContext.Default.BoardLaunch), transaction);
            insert.Parameters.AddWithValue("$board", boardId);
            insert.Parameters.AddWithValue("$item", itemId);
            insert.Parameters.AddWithValue("$request", requestJson);
            insert.Parameters.AddWithValue("$created", now.ToString("O"));
            insert.ExecuteNonQuery();
            transaction.Commit();
            OnChanged?.Invoke(boardId);
            return new(created, true);
        }
    }

    internal BoardLaunch UpdateLaunch(BoardLaunch launch, string state, string? sessionId = null, string? error = null)
    {
        lock (_gate)
        {
            var updated = launch with { State = state, SessionId = sessionId ?? launch.SessionId, Error = error, UpdatedAt = DateTimeOffset.UtcNow };
            using var command = Command("UPDATE board_launches SET document=$document WHERE id=$id", launch.Id,
                JsonSerializer.Serialize(updated, BoardJsonContext.Default.BoardLaunch));
            command.ExecuteNonQuery();
            OnChanged?.Invoke(launch.BoardId);
            return updated;
        }
    }
}
