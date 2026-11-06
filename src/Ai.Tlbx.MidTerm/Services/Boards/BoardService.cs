using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.Data.Sqlite;

namespace Ai.Tlbx.MidTerm.Services.Boards;

/// <summary>Transactional local board storage. Item revisions are independent of viewport edits.</summary>
public sealed partial class BoardService : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SqliteConnection _db;
    public event Action<string>? OnChanged;

    public BoardService(SettingsService settings) : this(settings.SettingsDirectory) { }

    public BoardService(string directory)
    {
        Directory.CreateDirectory(directory);
        SqliteNativeLoader.EnsureProvider(directory);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "boards.db"), ForeignKeys = true
        }.ToString());
        _db.Open();
        using var command = _db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS boards(id TEXT PRIMARY KEY, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS board_items(
                board_id TEXT NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
                id TEXT NOT NULL, document TEXT NOT NULL, PRIMARY KEY(board_id,id));
            CREATE TABLE IF NOT EXISTS board_launches(
                id TEXT PRIMARY KEY, board_id TEXT NOT NULL, item_id TEXT NOT NULL,
                request TEXT NOT NULL, document TEXT NOT NULL, created_at TEXT NOT NULL,
                FOREIGN KEY(board_id,item_id) REFERENCES board_items(board_id,id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS board_launches_item ON board_launches(board_id,item_id,created_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    public List<BoardDocument> List()
    {
        lock (_gate)
        {
            using var command = Command("SELECT document FROM boards ORDER BY rowid DESC");
            using var reader = command.ExecuteReader();
            var boards = new List<BoardDocument>();
            while (reader.Read()) boards.Add(JsonSerializer.Deserialize(reader.GetString(0), BoardJsonContext.Default.BoardDocument)!);
            return boards;
        }
    }

    public BoardDocument Create(BoardPatch request)
    {
        lock (_gate)
        {
            var board = new BoardDocument { Id = Guid.NewGuid().ToString("N"), Revision = 1 };
            Apply(board, request);
            using var command = Command("INSERT INTO boards VALUES($id,$document)", board.Id,
                JsonSerializer.Serialize(board, BoardJsonContext.Default.BoardDocument));
            command.ExecuteNonQuery();
            OnChanged?.Invoke(board.Id);
            return board;
        }
    }

    public BoardDocument? Get(string id)
    {
        lock (_gate)
        {
            var board = ReadBoard(id);
            if (board is null) return null;
            using var command = Command("SELECT document FROM board_items WHERE board_id=$id ORDER BY rowid", id);
            using (var reader = command.ExecuteReader())
                while (reader.Read()) board.Items.Add(JsonSerializer.Deserialize(reader.GetString(0), BoardJsonContext.Default.BoardItem)!);
            AttachLaunches(board);
            return board;
        }
    }

    public BoardDocument? Update(string id, BoardPatch patch)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            var board = ReadBoard(id, transaction);
            if (board is null) return null;
            CheckRevision(board.Revision, patch.ExpectedRevision);
            Apply(board, patch);
            board.Revision++;
            using var command = Command("UPDATE boards SET document=$document WHERE id=$id", id,
                JsonSerializer.Serialize(board, BoardJsonContext.Default.BoardDocument), transaction);
            command.ExecuteNonQuery();
            transaction.Commit();
            OnChanged?.Invoke(id);
            return Get(id);
        }
    }

    public BoardItem? GetItem(string boardId, string itemId)
    {
        lock (_gate)
        {
            var item = ReadStoredItem(boardId, itemId);
            if (item is not null) item.Launches = ListLaunches(boardId, itemId, 20);
            return item;
        }
    }

    public bool UpdateViewport(string id, BoardViewport viewport)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            var board = ReadBoard(id, transaction);
            if (board is null) return false;
            Apply(board, new() { PanX = viewport.PanX, PanY = viewport.PanY, Zoom = viewport.Zoom });
            using var command = Command("UPDATE boards SET document=$document WHERE id=$id", id,
                JsonSerializer.Serialize(board, BoardJsonContext.Default.BoardDocument), transaction);
            command.ExecuteNonQuery();
            transaction.Commit();
            // Camera persistence is deliberately independent of document revisions and live edits.
            return true;
        }
    }

    public BoardItem? WriteItem(string boardId, string? itemId, BoardItemWrite write)
    {
        ArgumentNullException.ThrowIfNull(write.Item);
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            if (ReadBoard(boardId, transaction) is null) return null;
            var item = write.Item with { Id = itemId ?? Guid.NewGuid().ToString("N") };
            item.Launches = [];
            Validate(item);
            var revision = 0L;
            if (itemId is not null)
            {
                using var read = Command("SELECT document FROM board_items WHERE board_id=$id AND id=$item", boardId, transaction: transaction);
                read.Parameters.AddWithValue("$item", itemId);
                if (read.ExecuteScalar() is not string json) return null;
                revision = JsonSerializer.Deserialize(json, BoardJsonContext.Default.BoardItem)!.Revision;
                CheckRevision(revision, write.ExpectedRevision);
            }
            item.Revision = revision + 1;
            using var command = Command("INSERT INTO board_items VALUES($id,$item,$document) ON CONFLICT(board_id,id) DO UPDATE SET document=$document",
                boardId, JsonSerializer.Serialize(item, BoardJsonContext.Default.BoardItem), transaction);
            command.Parameters.AddWithValue("$item", item.Id);
            command.ExecuteNonQuery();
            transaction.Commit();
            OnChanged?.Invoke(boardId);
            return item;
        }
    }

    public bool Delete(string boardId, string? itemId, long? expectedRevision)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            long revision;
            if (itemId is null)
            {
                var board = ReadBoard(boardId, transaction);
                if (board is null) return false;
                revision = board.Revision;
            }
            else
            {
                using var read = Command("SELECT document FROM board_items WHERE board_id=$id AND id=$item", boardId, transaction: transaction);
                read.Parameters.AddWithValue("$item", itemId);
                if (read.ExecuteScalar() is not string json) return false;
                revision = JsonSerializer.Deserialize(json, BoardJsonContext.Default.BoardItem)!.Revision;
            }
            CheckRevision(revision, expectedRevision);
            using var command = Command(itemId is null ? "DELETE FROM boards WHERE id=$id" : "DELETE FROM board_items WHERE board_id=$id AND id=$item", boardId, transaction: transaction);
            if (itemId is not null) command.Parameters.AddWithValue("$item", itemId);
            command.ExecuteNonQuery();
            transaction.Commit();
            OnChanged?.Invoke(boardId);
            return true;
        }
    }

    private BoardDocument? ReadBoard(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT document FROM boards WHERE id=$id", id, transaction: transaction);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize(json, BoardJsonContext.Default.BoardDocument) : null;
    }

    private SqliteCommand Command(string sql, string? id = null, string? document = null, SqliteTransaction? transaction = null)
    {
        var command = _db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        if (document is not null) command.Parameters.AddWithValue("$document", document);
        return command;
    }

    private static void CheckRevision(long actual, long? expected)
    {
        if (expected is null) throw new ArgumentException("expectedRevision is required for updates and deletes.");
        if (actual != expected) throw new BoardConflictException();
    }

    private static void Apply(BoardDocument board, BoardPatch patch)
    {
        if (patch.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(patch.Title) || patch.Title.Length > 256) throw new ArgumentException("Title must contain 1–256 characters.");
            board.Title = patch.Title.Trim();
        }
        if (patch.WorkingDirectory is not null)
        {
            if (patch.WorkingDirectory.Length > 0 && !Path.IsPathFullyQualified(patch.WorkingDirectory)) throw new ArgumentException("Session directory must be absolute.");
            board.WorkingDirectory = string.IsNullOrWhiteSpace(patch.WorkingDirectory) ? null : patch.WorkingDirectory;
        }
        if (patch.LaunchCommand is not null)
        {
            if (patch.LaunchCommand.Length > 8192) throw new ArgumentException("Session command is too large.");
            board.LaunchCommand = string.IsNullOrWhiteSpace(patch.LaunchCommand) ? null : patch.LaunchCommand;
        }
        if (patch.SessionProfile is not null)
        {
            if (patch.SessionProfile.Length > 128) throw new ArgumentException("Session profile is too large.");
            board.SessionProfile = string.IsNullOrWhiteSpace(patch.SessionProfile) ? null : patch.SessionProfile;
        }
        if (patch.AppServerControlOnly is { } controlOnly) board.AppServerControlOnly = controlOnly;
        if (patch.PanX is { } x) board.PanX = Coordinate(x);
        if (patch.PanY is { } y) board.PanY = Coordinate(y);
        if (patch.Zoom is { } zoom)
        {
            if (!double.IsFinite(zoom) || zoom is < 0.05 or > 8) throw new ArgumentException("Zoom must be between 0.05 and 8.");
            board.Zoom = zoom;
        }
    }

    private static double Coordinate(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > 100_000_000) throw new ArgumentException("Invalid board coordinate.");
        return value;
    }

    private static void Validate(BoardItem item)
    {
        if (item.Kind is not ("note" or "todo" or "calendar" or "mail" or "timeline" or "browser")) throw new ArgumentException("Unsupported item kind.");
        Coordinate(item.X); Coordinate(item.Y);
        if (!double.IsFinite(item.Width) || !double.IsFinite(item.Height) || item.Width is < 160 or > 10000 || item.Height is < 100 or > 10000) throw new ArgumentException("Invalid item size.");
        if (item.Title is null || item.Text is null || item.Entries is null) throw new ArgumentException("Title, text and entries cannot be null.");
        if (item.Title.Length > 256 || item.Text.Length > 100_000 || item.Entries.Count > 2000) throw new ArgumentException("Item is too large.");
        if (item.Color is not ("sand" or "rose" or "sage" or "blue" or "plain")) throw new ArgumentException("Unsupported note color.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in item.Entries)
        {
            if (entry is null || entry.Id is null || entry.Text is null) throw new ArgumentException("Invalid entry.");
            if (entry.Id.Length == 0) entry.Id = Guid.NewGuid().ToString("N");
            if (!ids.Add(entry.Id)) throw new ArgumentException("Duplicate entry id.");
            if (entry.Text.Length > 8192 || entry.Week is < 1 or > 53 || entry.Quarter is < 1 or > 4 || entry.Year is < 1 or > 9999) throw new ArgumentException("Invalid entry.");
            if (entry is { Year: { } year, Week: { } week } && week > ISOWeek.GetWeeksInYear(year)) throw new ArgumentException("This ISO year has no week 53.");
            if (entry.Date is not null && !DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ArgumentException("Dates use yyyy-MM-dd.");
            if (entry.Sender?.Length > 512 || entry.Url?.Length > 8192) throw new ArgumentException("Mail entry is too large.");
            if (entry.Url is not null && (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var link) || link.Scheme is not ("http" or "https" or "mailto"))) throw new ArgumentException("Mail links must use http, https or mailto.");
        }
        if (item.Url is not null && (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))) throw new ArgumentException("Browser URLs must use http or https.");
        if (item.LocalDirectory is not null && !Path.IsPathFullyQualified(item.LocalDirectory)) throw new ArgumentException("Local directory must be absolute.");
        if (item.WorkingDirectory is not null && !Path.IsPathFullyQualified(item.WorkingDirectory)) throw new ArgumentException("Session directory must be absolute.");
        if (item.Prompt?.Length > 100_000 || item.LaunchCommand?.Length > 8192 || item.SessionProfile?.Length > 128) throw new ArgumentException("Session configuration is too large.");
        if (item.CalendarMonth is not null) BoardCalendar.Month(item.CalendarMonth);
    }

    public void Dispose() => _db.Dispose();
}

public sealed class BoardConflictException : Exception
{
    public BoardConflictException() : base("This item changed. Read the latest revision before applying your edit.") { }
}
