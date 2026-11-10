using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.ActionGraphs;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.Data.Sqlite;

namespace Ai.Tlbx.MidTerm.Services;

/// <summary>
/// SQLite-backed store for agent-curated action graphs. Pure verbatim CRUD:
/// tlbx persists scopes, graphs, nodes, edges, positions, and launch specs —
/// it never derives meaning from them.
/// </summary>
public sealed class ActionGraphService : IDisposable
{
    internal const int MaxGraphs = 50;
    internal const int MaxScopes = 50;
    internal const int MaxNodesPerGraph = 10_000;
    internal const int MaxEdgesPerGraph = 25_000;
    internal const int MaxActionsPerNode = 8;
    private const int MaxTitleLength = 256;
    private const int MaxStateLength = 256;
    private const int MaxHtmlLength = 65536;
    private const int MaxPromptLength = 8192;
    private const int MaxLabelLength = 128;
    private const int MaxReferenceLength = 4096;

    private readonly Lock _lock = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public ActionGraphService(SettingsService settingsService)
    {
        SqliteNativeLoader.EnsureProvider(settingsService.SettingsDirectory);
        var databasePath = Path.Combine(settingsService.SettingsDirectory, "action-graphs.db");
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        CreateSchema();
        MigrateLegacyJsonDocument(Path.Combine(settingsService.SettingsDirectory, "action-graphs.json"));
    }

    // ----- Scopes -----

    public ActionGraphScopeListResponse ListScopes()
    {
        lock (_lock)
        {
            var scopes = new List<ActionGraphScope>();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT s.id, s.name, s.created_at, COUNT(g.id)
                FROM scopes s LEFT JOIN graphs g ON g.scope_id = s.id
                GROUP BY s.id ORDER BY s.id = 'default' DESC, s.name COLLATE NOCASE
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                scopes.Add(new ActionGraphScope
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    CreatedAt = ReadTimestamp(reader.GetString(2)),
                    GraphCount = reader.GetInt32(3)
                });
            }
            return new ActionGraphScopeListResponse { Scopes = scopes };
        }
    }

    public ActionGraphScope CreateScope(CreateActionGraphScopeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.Id is null ? NewId() : ValidId(request.Id, "scopeId");
        var name = Required(request.Name ?? id, MaxTitleLength, "scope name");

        lock (_lock)
        {
            ThrowIfDisposed();
            if (Scalar("SELECT COUNT(*) FROM scopes") >= MaxScopes)
            {
                throw new ArgumentException($"The maximum of {MaxScopes} scopes already exists.");
            }
            if (Scalar("SELECT COUNT(*) FROM scopes WHERE id = $id", ("$id", id)) > 0)
            {
                throw new ArgumentException($"Scope '{id}' already exists.");
            }

            var now = Now();
            Execute(
                "INSERT INTO scopes(id, name, created_at) VALUES($id, $name, $now)",
                ("$id", id), ("$name", name), ("$now", now));
            return new ActionGraphScope { Id = id, Name = name, CreatedAt = ReadTimestamp(now) };
        }
    }

    public bool RenameScope(string scopeId, RenameActionGraphScopeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = ValidId(scopeId, nameof(scopeId));
        if (string.Equals(id, ActionGraphScope.DefaultId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The default scope cannot be renamed.");
        }
        var name = Required(request.Name, MaxTitleLength, "scope name");

        lock (_lock)
        {
            ThrowIfDisposed();
            return ExecuteRows("UPDATE scopes SET name = $name WHERE id = $id", ("$name", name), ("$id", id)) > 0;
        }
    }

    public bool DeleteScope(string scopeId)
    {
        var id = ValidId(scopeId, nameof(scopeId));
        if (string.Equals(id, ActionGraphScope.DefaultId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The default scope cannot be deleted.");
        }

        lock (_lock)
        {
            ThrowIfDisposed();
            if (Scalar("SELECT COUNT(*) FROM graphs WHERE scope_id = $id", ("$id", id)) > 0)
            {
                throw new ArgumentException($"Scope '{id}' still contains graphs. Move or delete them first.");
            }
            return ExecuteRows("DELETE FROM scopes WHERE id = $id", ("$id", id)) > 0;
        }
    }

    // ----- Graphs -----

    public ActionGraphListResponse ListGraphs(string? scopeId = null)
    {
        var normalizedScope = scopeId is null ? null : ValidId(scopeId, nameof(scopeId));
        lock (_lock)
        {
            var graphs = new List<ActionGraphSummary>();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT g.id, g.scope_id, g.name, g.updated_at, g.revision,
                       (SELECT COUNT(*) FROM nodes n WHERE n.graph_id = g.id),
                       (SELECT COUNT(*) FROM edges e WHERE e.graph_id = g.id)
                FROM graphs g
                WHERE $scope IS NULL OR g.scope_id = $scope
                ORDER BY g.name COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("$scope", (object?)normalizedScope ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                graphs.Add(new ActionGraphSummary
                {
                    Id = reader.GetString(0),
                    ScopeId = reader.GetString(1),
                    Name = reader.GetString(2),
                    UpdatedAt = ReadTimestamp(reader.GetString(3)),
                    Revision = reader.GetInt32(4),
                    NodeCount = reader.GetInt32(5),
                    EdgeCount = reader.GetInt32(6)
                });
            }
            return new ActionGraphListResponse { Graphs = graphs };
        }
    }

    public ActionGraph CreateGraph(CreateActionGraphRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_lock)
        {
            ThrowIfDisposed();
            CheckGraphRevisionLocked(request.Id, request.ExpectedRevision);
            var id = GetOrCreateGraphLocked(request.Id, request.Name, request.ScopeId);
            ApplyRefreshSpecLocked(id, request);
            return GetGraphLocked(id)!;
        }
    }

    public bool DeleteGraph(string graphId, int? expectedRevision = null)
    {
        var id = ValidId(graphId, nameof(graphId));
        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            CheckGraphRevisionLocked(id, expectedRevision);
            Execute("DELETE FROM node_actions WHERE graph_id = $id", ("$id", id));
            Execute("DELETE FROM node_sessions WHERE graph_id = $id", ("$id", id));
            Execute("DELETE FROM edges WHERE graph_id = $id", ("$id", id));
            Execute("DELETE FROM nodes WHERE graph_id = $id", ("$id", id));
            var removed = ExecuteRows("DELETE FROM graphs WHERE id = $id", ("$id", id)) > 0;
            transaction.Commit();
            return removed;
        }
    }

    public ActionGraph? GetGraph(string graphId)
    {
        var id = ValidId(graphId, nameof(graphId));
        lock (_lock)
        {
            return GetGraphLocked(id);
        }
    }

    public ActionGraphContextResponse? GetNodeContext(
        string graphId,
        string nodeId,
        int depth = 1,
        int limit = 120)
    {
        var id = ValidId(graphId, nameof(graphId));
        var anchorId = ValidId(nodeId, nameof(nodeId));
        if (depth is < 0 or > 4)
        {
            throw new ArgumentException("depth must be between 0 and 4.", nameof(depth));
        }
        if (limit is < 1 or > 500)
        {
            throw new ArgumentException("limit must be between 1 and 500.", nameof(limit));
        }

        lock (_lock)
        {
            ThrowIfDisposed();
            var graph = GetGraphLocked(id);
            var anchor = graph?.Nodes.FirstOrDefault(node => node.Id == anchorId);
            if (graph is null || anchor is null)
            {
                return null;
            }

            var adjacency = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var edge in graph.Edges)
            {
                if (!adjacency.TryGetValue(edge.FromId, out var from))
                {
                    from = new(Comparer<string>.Default);
                    adjacency[edge.FromId] = from;
                }
                if (!adjacency.TryGetValue(edge.ToId, out var to))
                {
                    to = new(Comparer<string>.Default);
                    adjacency[edge.ToId] = to;
                }
                from.Add(edge.ToId);
                to.Add(edge.FromId);
            }

            var included = new HashSet<string>(StringComparer.Ordinal) { anchorId };
            var frontier = new List<string> { anchorId };
            for (var level = 0; level < depth && included.Count < limit; level++)
            {
                var next = new List<string>();
                foreach (var current in frontier)
                {
                    if (!adjacency.TryGetValue(current, out var neighbors))
                    {
                        continue;
                    }
                    foreach (var neighbor in neighbors)
                    {
                        if (included.Count >= limit)
                        {
                            break;
                        }
                        if (included.Add(neighbor))
                        {
                            next.Add(neighbor);
                        }
                    }
                }
                frontier = next;
            }

            return new ActionGraphContextResponse
            {
                GraphId = graph.Id,
                GraphRevision = graph.Revision,
                Anchor = anchor,
                Nodes = graph.Nodes.Where(node => included.Contains(node.Id)).ToList(),
                Edges = graph.Edges
                    .Where(edge => included.Contains(edge.FromId) && included.Contains(edge.ToId))
                    .ToList()
            };
        }
    }

    /// <summary>
    /// Deterministically arranges exact graph structure without interpreting semantic fields.
    /// Pinned nodes and frames retain their agent-published positions.
    /// </summary>
    public ActionGraph? OrganizeGraph(string graphId, int? expectedGraphRevision = null)
    {
        var id = ValidId(graphId, nameof(graphId));
        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var graph = GetGraphLocked(id);
            if (graph is null)
            {
                return null;
            }
            CheckRevision($"graph '{id}'", expectedGraphRevision, graph.Revision);

            var movable = graph.Nodes
                .Where(node => node.Kind != ActionGraphNodeKinds.Frame)
                .ToDictionary(node => node.Id, StringComparer.Ordinal);
            var outgoing = movable.Keys.ToDictionary(
                nodeId => nodeId,
                _ => new List<string>(),
                StringComparer.Ordinal);
            var indegree = movable.Keys.ToDictionary(nodeId => nodeId, _ => 0, StringComparer.Ordinal);
            var connected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in graph.Edges)
            {
                if (!movable.ContainsKey(edge.FromId) || !movable.ContainsKey(edge.ToId))
                {
                    continue;
                }
                outgoing[edge.FromId].Add(edge.ToId);
                indegree[edge.ToId]++;
                connected.Add(edge.FromId);
                connected.Add(edge.ToId);
            }
            foreach (var neighbors in outgoing.Values)
            {
                neighbors.Sort(StringComparer.Ordinal);
            }

            var ranks = movable.Keys.ToDictionary(nodeId => nodeId, _ => 0, StringComparer.Ordinal);
            var queue = new SortedSet<string>(
                indegree.Where(pair => pair.Value == 0 && connected.Contains(pair.Key)).Select(pair => pair.Key),
                StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                var nodeId = queue.Min!;
                queue.Remove(nodeId);
                visited.Add(nodeId);
                foreach (var nextId in outgoing[nodeId])
                {
                    ranks[nextId] = Math.Max(ranks[nextId], ranks[nodeId] + 1);
                    indegree[nextId]--;
                    if (indegree[nextId] == 0)
                    {
                        queue.Add(nextId);
                    }
                }
            }

            var cycleRank = visited.Count == 0 ? 0 : visited.Max(nodeId => ranks[nodeId]) + 1;
            foreach (var nodeId in connected.Where(nodeId => !visited.Contains(nodeId)))
            {
                ranks[nodeId] = cycleRank;
            }

            const double horizontalGap = 112;
            const double verticalGap = 46;
            var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
            var x = 0d;
            foreach (var layer in connected
                         .GroupBy(nodeId => ranks[nodeId])
                         .OrderBy(group => group.Key))
            {
                var ordered = layer
                    .Select(nodeId => movable[nodeId])
                    .OrderByDescending(node => node.Pinned)
                    .ThenBy(node => node.Y)
                    .ThenBy(node => node.Id, StringComparer.Ordinal)
                    .ToList();
                var y = 0d;
                var layerWidth = 224d;
                foreach (var node in ordered)
                {
                    positions[node.Id] = (x, y);
                    y += NodeHeight(node) + verticalGap;
                    layerWidth = Math.Max(layerWidth, NodeWidth(node));
                }
                x += layerWidth + horizontalGap;
            }

            var loose = movable.Values
                .Where(node => !connected.Contains(node.Id))
                .OrderByDescending(node => node.Pinned)
                .ThenBy(node => node.Id, StringComparer.Ordinal)
                .ToList();
            if (loose.Count > 0)
            {
                var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(loose.Count)));
                const double looseColumnWidth = 304;
                const double looseRowHeight = 150;
                for (var index = 0; index < loose.Count; index++)
                {
                    var node = loose[index];
                    positions[node.Id] = (
                        x + (index % columns) * looseColumnWidth,
                        (index / columns) * looseRowHeight);
                }
            }

            var now = Now();
            var changed = false;
            foreach (var (nodeId, position) in positions)
            {
                var node = movable[nodeId];
                if (node.Pinned ||
                    (Math.Abs(node.X - position.X) < 0.01 && Math.Abs(node.Y - position.Y) < 0.01))
                {
                    continue;
                }
                Execute(
                    """
                    UPDATE nodes
                    SET x = $x, y = $y, updated_at = $now, revision = revision + 1
                    WHERE graph_id = $g AND id = $n
                    """,
                    ("$x", position.X), ("$y", position.Y), ("$now", now),
                    ("$g", id), ("$n", nodeId));
                changed = true;
            }
            if (changed)
            {
                TouchGraphLocked(id, now);
            }
            transaction.Commit();
            return GetGraphLocked(id);
        }
    }

    // ----- Nodes -----

    public ActionGraphNode CreateNode(string graphId, UpsertActionGraphNodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = ValidId(graphId, nameof(graphId));

        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            GetOrCreateGraphLocked(id, name: null, scopeId: null);
            CheckGraphRevisionLocked(id, request.ExpectedGraphRevision);
            if (Scalar("SELECT COUNT(*) FROM nodes WHERE graph_id = $g", ("$g", id)) >= MaxNodesPerGraph)
            {
                throw new ArgumentException($"Graph '{id}' already holds the maximum of {MaxNodesPerGraph} nodes.");
            }

            var nodeId = request.Id is null ? NewId() : ValidId(request.Id, nameof(request.Id));
            if (Scalar("SELECT COUNT(*) FROM nodes WHERE graph_id = $g AND id = $n", ("$g", id), ("$n", nodeId)) > 0)
            {
                throw new ArgumentException($"Node '{nodeId}' already exists in graph '{id}'.");
            }

            var now = Now();
            var node = new ActionGraphNode
            {
                Id = nodeId,
                Kind = Optional(request.Kind, 64) ?? ActionGraphNodeKinds.Identity,
                Title = Required(request.Title, MaxTitleLength, nameof(request.Title)),
                State = Optional(request.State, MaxStateLength),
                Html = Optional(request.Html, MaxHtmlLength),
                X = request.X ?? 0,
                Y = request.Y ?? 0,
                Width = request.Width,
                Height = request.Height,
                MinZoom = NormalizedZoom(request.MinZoom, nameof(request.MinZoom)),
                MaxZoom = NormalizedZoom(request.MaxZoom, nameof(request.MaxZoom)),
                Pinned = request.Pinned ?? false,
                Attention = request.Attention ?? false,
                Hidden = request.Hidden ?? false,
                Color = Optional(request.Color, 32),
                Url = Optional(request.Url, MaxReferenceLength),
                Path = Optional(request.Path, MaxReferenceLength),
                Host = Optional(request.Host, MaxReferenceLength),
                Project = Optional(request.Project, MaxTitleLength),
                SessionId = Optional(request.SessionId, 128),
                ExternalRef = Optional(request.ExternalRef, MaxReferenceLength),
                Date = request.Date,
                Actions = NormalizedActions(request.Actions),
                Source = Optional(request.Source, 128) ?? "agent",
                CreatedAt = ReadTimestamp(now),
                UpdatedAt = ReadTimestamp(now),
                Revision = 1
            };
            ValidateZoomRange(node.MinZoom, node.MaxZoom);
            InsertNodeLocked(id, node, now);
            TouchGraphLocked(id, now);
            transaction.Commit();
            return node;
        }
    }

    public ActionGraphNode? UpdateNode(string graphId, string nodeId, UpsertActionGraphNodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = ValidId(graphId, nameof(graphId));
        var normalizedNodeId = ValidId(nodeId, nameof(nodeId));

        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var node = ReadNodeLocked(id, normalizedNodeId);
            if (node is null)
            {
                return null;
            }
            CheckRevision($"node '{normalizedNodeId}'", request.ExpectedRevision, node.Revision);

            if (request.Kind is not null) node.Kind = Optional(request.Kind, 64) ?? node.Kind;
            if (request.Title is not null) node.Title = Required(request.Title, MaxTitleLength, nameof(request.Title));
            if (request.State is not null) node.State = Optional(request.State, MaxStateLength);
            if (request.Html is not null) node.Html = Optional(request.Html, MaxHtmlLength);
            // Position ownership: publishes keep manual layout unless they explicitly move a node.
            if (request.X is not null) node.X = request.X.Value;
            if (request.Y is not null) node.Y = request.Y.Value;
            if (request.Width is not null) node.Width = request.Width;
            if (request.Height is not null) node.Height = request.Height;
            if (request.MinZoom is not null) node.MinZoom = NormalizedZoom(request.MinZoom, nameof(request.MinZoom));
            if (request.MaxZoom is not null) node.MaxZoom = NormalizedZoom(request.MaxZoom, nameof(request.MaxZoom));
            if (request.Pinned is not null) node.Pinned = request.Pinned.Value;
            if (request.Attention is not null) node.Attention = request.Attention.Value;
            if (request.Hidden is not null) node.Hidden = request.Hidden.Value;
            if (request.Color is not null) node.Color = Optional(request.Color, 32);
            if (request.Url is not null) node.Url = Optional(request.Url, MaxReferenceLength);
            if (request.Path is not null) node.Path = Optional(request.Path, MaxReferenceLength);
            if (request.Host is not null) node.Host = Optional(request.Host, MaxReferenceLength);
            if (request.Project is not null) node.Project = Optional(request.Project, MaxTitleLength);
            if (request.SessionId is not null) node.SessionId = Optional(request.SessionId, 128);
            if (request.ExternalRef is not null) node.ExternalRef = Optional(request.ExternalRef, MaxReferenceLength);
            if (request.Date is not null) node.Date = request.Date;
            if (request.Actions is not null) node.Actions = NormalizedActions(request.Actions);
            if (request.Source is not null) node.Source = Optional(request.Source, 128) ?? node.Source;
            ValidateZoomRange(node.MinZoom, node.MaxZoom);

            var now = Now();
            node.UpdatedAt = ReadTimestamp(now);
            node.Revision++;
            Execute("DELETE FROM node_actions WHERE graph_id = $g AND node_id = $n", ("$g", id), ("$n", normalizedNodeId));
            Execute("DELETE FROM nodes WHERE graph_id = $g AND id = $n", ("$g", id), ("$n", normalizedNodeId));
            InsertNodeLocked(id, node, now, node.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
            TouchGraphLocked(id, now);
            transaction.Commit();
            return node;
        }
    }

    public ActionGraphNode? SetNodePosition(
        string graphId,
        string nodeId,
        double x,
        double y,
        int? expectedRevision = null)
    {
        var id = ValidId(graphId, nameof(graphId));
        var normalizedNodeId = ValidId(nodeId, nameof(nodeId));

        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var node = ReadNodeLocked(id, normalizedNodeId);
            if (node is null)
            {
                return null;
            }
            CheckRevision($"node '{normalizedNodeId}'", expectedRevision, node.Revision);
            var now = Now();
            Execute(
                """
                UPDATE nodes
                SET x = $x, y = $y, updated_at = $now, revision = revision + 1
                WHERE graph_id = $g AND id = $n
                """,
                ("$x", x), ("$y", y), ("$now", now), ("$g", id), ("$n", normalizedNodeId));
            TouchGraphLocked(id, now);
            transaction.Commit();
            return ReadNodeLocked(id, normalizedNodeId);
        }
    }

    public bool DeleteNode(
        string graphId,
        string nodeId,
        int? expectedRevision = null,
        int? expectedGraphRevision = null)
    {
        var id = ValidId(graphId, nameof(graphId));
        var normalizedNodeId = ValidId(nodeId, nameof(nodeId));

        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            CheckGraphRevisionLocked(id, expectedGraphRevision);
            var node = ReadNodeLocked(id, normalizedNodeId);
            if (node is null)
            {
                return false;
            }
            CheckRevision($"node '{normalizedNodeId}'", expectedRevision, node.Revision);
            var removed = ExecuteRows(
                "DELETE FROM nodes WHERE graph_id = $g AND id = $n", ("$g", id), ("$n", normalizedNodeId)) > 0;
            if (removed)
            {
                Execute("DELETE FROM node_actions WHERE graph_id = $g AND node_id = $n", ("$g", id), ("$n", normalizedNodeId));
                Execute("DELETE FROM node_sessions WHERE graph_id = $g AND node_id = $n", ("$g", id), ("$n", normalizedNodeId));
                Execute(
                    "DELETE FROM edges WHERE graph_id = $g AND (from_id = $n OR to_id = $n)",
                    ("$g", id), ("$n", normalizedNodeId));
                TouchGraphLocked(id, Now());
            }
            transaction.Commit();
            return removed;
        }
    }

    // ----- Edges -----

    public ActionGraphEdge CreateEdge(string graphId, CreateActionGraphEdgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = ValidId(graphId, nameof(graphId));
        var fromId = ValidId(request.FromId ?? "", nameof(request.FromId));
        var toId = ValidId(request.ToId ?? "", nameof(request.ToId));

        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            if (Scalar("SELECT COUNT(*) FROM graphs WHERE id = $g", ("$g", id)) == 0)
            {
                throw new ArgumentException($"Graph '{id}' does not exist.");
            }
            CheckGraphRevisionLocked(id, request.ExpectedGraphRevision);
            if (Scalar("SELECT COUNT(*) FROM edges WHERE graph_id = $g", ("$g", id)) >= MaxEdgesPerGraph)
            {
                throw new ArgumentException($"Graph '{id}' already holds the maximum of {MaxEdgesPerGraph} edges.");
            }
            if (Scalar("SELECT COUNT(*) FROM nodes WHERE graph_id = $g AND id = $n", ("$g", id), ("$n", fromId)) == 0)
            {
                throw new ArgumentException($"Node '{fromId}' does not exist in graph '{id}'.");
            }
            if (Scalar("SELECT COUNT(*) FROM nodes WHERE graph_id = $g AND id = $n", ("$g", id), ("$n", toId)) == 0)
            {
                throw new ArgumentException($"Node '{toId}' does not exist in graph '{id}'.");
            }

            var edgeId = request.Id is null ? NewId() : ValidId(request.Id, nameof(request.Id));
            if (Scalar("SELECT COUNT(*) FROM edges WHERE graph_id = $g AND id = $e", ("$g", id), ("$e", edgeId)) > 0)
            {
                throw new ArgumentException($"Edge '{edgeId}' already exists in graph '{id}'.");
            }

            var now = Now();
            var edge = new ActionGraphEdge
            {
                Id = edgeId,
                FromId = fromId,
                ToId = toId,
                Label = Optional(request.Label, MaxLabelLength),
                Kind = Optional(request.Kind, 64),
                CreatedAt = ReadTimestamp(now),
                Revision = 1
            };
            Execute(
                """
                INSERT INTO edges(graph_id, id, from_id, to_id, label, kind, created_at, revision)
                VALUES($g, $id, $from, $to, $label, $kind, $now, $revision)
                """,
                ("$g", id), ("$id", edge.Id), ("$from", edge.FromId), ("$to", edge.ToId),
                ("$label", (object?)edge.Label ?? DBNull.Value), ("$kind", (object?)edge.Kind ?? DBNull.Value),
                ("$now", now), ("$revision", edge.Revision));
            TouchGraphLocked(id, now);
            transaction.Commit();
            return edge;
        }
    }

    public bool DeleteEdge(string graphId, string edgeId, int? expectedGraphRevision = null)
    {
        var id = ValidId(graphId, nameof(graphId));
        var normalizedEdgeId = ValidId(edgeId, nameof(edgeId));

        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            CheckGraphRevisionLocked(id, expectedGraphRevision);
            var removed = ExecuteRows(
                "DELETE FROM edges WHERE graph_id = $g AND id = $e", ("$g", id), ("$e", normalizedEdgeId)) > 0;
            if (removed)
            {
                TouchGraphLocked(id, Now());
            }
            transaction.Commit();
            return removed;
        }
    }

    // ----- Session bindings -----

    public ActionGraphNode? BindSession(
        string graphId,
        string nodeId,
        BindActionGraphSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = ValidId(graphId, nameof(graphId));
        var normalizedNodeId = ValidId(nodeId, nameof(nodeId));
        var sessionId = Required(request.SessionId, 128, nameof(request.SessionId));
        var role = Optional(request.Role, 64);

        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            if (ReadNodeLocked(id, normalizedNodeId) is null)
            {
                return null;
            }
            CheckGraphRevisionLocked(id, request.ExpectedGraphRevision);
            var now = Now();
            Execute(
                """
                INSERT INTO node_sessions(graph_id, node_id, session_id, role, created_at)
                VALUES($g, $n, $session, $role, $now)
                ON CONFLICT(graph_id, node_id, session_id)
                DO UPDATE SET role = excluded.role
                """,
                ("$g", id), ("$n", normalizedNodeId), ("$session", sessionId),
                ("$role", (object?)role ?? DBNull.Value), ("$now", now));
            Execute(
                """
                UPDATE nodes
                SET session_id = COALESCE(session_id, $session), updated_at = $now, revision = revision + 1
                WHERE graph_id = $g AND id = $n
                """,
                ("$session", sessionId), ("$now", now), ("$g", id), ("$n", normalizedNodeId));
            TouchGraphLocked(id, now);
            transaction.Commit();
            return ReadNodeLocked(id, normalizedNodeId);
        }
    }

    public bool UnbindSession(
        string graphId,
        string nodeId,
        string sessionId,
        int? expectedGraphRevision = null)
    {
        var id = ValidId(graphId, nameof(graphId));
        var normalizedNodeId = ValidId(nodeId, nameof(nodeId));
        var normalizedSessionId = Required(sessionId, 128, nameof(sessionId));

        lock (_lock)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            CheckGraphRevisionLocked(id, expectedGraphRevision);
            var removed = ExecuteRows(
                """
                DELETE FROM node_sessions
                WHERE graph_id = $g AND node_id = $n AND session_id = $session
                """,
                ("$g", id), ("$n", normalizedNodeId), ("$session", normalizedSessionId)) > 0;
            if (!removed)
            {
                return false;
            }
            var now = Now();
            Execute(
                """
                UPDATE nodes
                SET session_id = (
                    SELECT session_id FROM node_sessions
                    WHERE graph_id = $g AND node_id = $n
                    ORDER BY created_at LIMIT 1
                ), updated_at = $now, revision = revision + 1
                WHERE graph_id = $g AND id = $n
                """,
                ("$now", now), ("$g", id), ("$n", normalizedNodeId));
            TouchGraphLocked(id, now);
            transaction.Commit();
            return true;
        }
    }

    // ----- Internals -----

    private ActionGraph? GetGraphLocked(string id)
    {
        ActionGraph? graph = null;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, scope_id, name, created_at, updated_at,
                       refresh_command, refresh_cwd, refresh_prompt, revision
                FROM graphs WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                graph = new ActionGraph
                {
                    Id = reader.GetString(0),
                    ScopeId = reader.GetString(1),
                    Name = reader.GetString(2),
                    RefreshCommand = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RefreshCwd = reader.IsDBNull(6) ? null : reader.GetString(6),
                    RefreshPrompt = reader.IsDBNull(7) ? null : reader.GetString(7),
                    CreatedAt = ReadTimestamp(reader.GetString(3)),
                    UpdatedAt = ReadTimestamp(reader.GetString(4)),
                    Revision = reader.GetInt32(8)
                };
            }
        }
        if (graph is null)
        {
            return null;
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, kind, title, state, html, x, y, width, color, url, path, host, project,
                       session_id, external_ref, date, source, created_at, updated_at, revision, height,
                       min_zoom, max_zoom, pinned, attention, hidden
                FROM nodes WHERE graph_id = $g ORDER BY created_at
                """;
            command.Parameters.AddWithValue("$g", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                graph.Nodes.Add(new ActionGraphNode
                {
                    Id = reader.GetString(0),
                    Kind = reader.GetString(1),
                    Title = reader.GetString(2),
                    State = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Html = reader.IsDBNull(4) ? null : reader.GetString(4),
                    X = reader.GetDouble(5),
                    Y = reader.GetDouble(6),
                    Width = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    Color = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Url = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Path = reader.IsDBNull(10) ? null : reader.GetString(10),
                    Host = reader.IsDBNull(11) ? null : reader.GetString(11),
                    Project = reader.IsDBNull(12) ? null : reader.GetString(12),
                    SessionId = reader.IsDBNull(13) ? null : reader.GetString(13),
                    ExternalRef = reader.IsDBNull(14) ? null : reader.GetString(14),
                    Date = reader.IsDBNull(15) ? null : ReadTimestamp(reader.GetString(15)),
                    Source = reader.GetString(16),
                    CreatedAt = ReadTimestamp(reader.GetString(17)),
                    UpdatedAt = ReadTimestamp(reader.GetString(18)),
                    Revision = reader.GetInt32(19),
                    Height = reader.IsDBNull(20) ? null : reader.GetDouble(20),
                    MinZoom = reader.IsDBNull(21) ? null : reader.GetDouble(21),
                    MaxZoom = reader.IsDBNull(22) ? null : reader.GetDouble(22),
                    Pinned = reader.GetInt32(23) != 0,
                    Attention = reader.GetInt32(24) != 0,
                    Hidden = reader.GetInt32(25) != 0
                });
            }
        }

        var actionsByNode = new Dictionary<string, List<ActionGraphNodeAction>>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT node_id, id, label, cwd, command, profile, prompt, session_name, slash_commands
                FROM node_actions WHERE graph_id = $g ORDER BY node_id, ord
                """;
            command.Parameters.AddWithValue("$g", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var nodeId = reader.GetString(0);
                if (!actionsByNode.TryGetValue(nodeId, out var actions))
                {
                    actions = [];
                    actionsByNode[nodeId] = actions;
                }
                actions.Add(new ActionGraphNodeAction
                {
                    Id = reader.GetString(1),
                    Label = reader.GetString(2),
                    Cwd = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Command = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Profile = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Prompt = reader.IsDBNull(6) ? null : reader.GetString(6),
                    SessionName = reader.IsDBNull(7) ? null : reader.GetString(7),
                    SlashCommands = DeserializeSlashCommands(reader.GetString(8))
                });
            }
        }
        foreach (var node in graph.Nodes)
        {
            if (actionsByNode.TryGetValue(node.Id, out var actions))
            {
                node.Actions = actions;
            }
        }

        var sessionsByNode = new Dictionary<string, List<ActionGraphSessionBinding>>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT node_id, session_id, role, created_at
                FROM node_sessions WHERE graph_id = $g ORDER BY node_id, created_at
                """;
            command.Parameters.AddWithValue("$g", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var nodeId = reader.GetString(0);
                if (!sessionsByNode.TryGetValue(nodeId, out var sessions))
                {
                    sessions = [];
                    sessionsByNode[nodeId] = sessions;
                }
                sessions.Add(new ActionGraphSessionBinding
                {
                    SessionId = reader.GetString(1),
                    Role = reader.IsDBNull(2) ? null : reader.GetString(2),
                    CreatedAt = ReadTimestamp(reader.GetString(3))
                });
            }
        }
        foreach (var node in graph.Nodes)
        {
            if (sessionsByNode.TryGetValue(node.Id, out var sessions))
            {
                node.Sessions = sessions;
            }
            else if (!string.IsNullOrWhiteSpace(node.SessionId))
            {
                node.Sessions =
                [
                    new ActionGraphSessionBinding
                    {
                        SessionId = node.SessionId,
                        Role = "legacy",
                        CreatedAt = node.UpdatedAt
                    }
                ];
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, from_id, to_id, label, kind, created_at, revision
                FROM edges WHERE graph_id = $g ORDER BY created_at
                """;
            command.Parameters.AddWithValue("$g", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                graph.Edges.Add(new ActionGraphEdge
                {
                    Id = reader.GetString(0),
                    FromId = reader.GetString(1),
                    ToId = reader.GetString(2),
                    Label = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Kind = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = ReadTimestamp(reader.GetString(5)),
                    Revision = reader.GetInt32(6)
                });
            }
        }
        return graph;
    }

    private string GetOrCreateGraphLocked(string? requestedId, string? name, string? scopeId)
    {
        var id = requestedId is null ? NewId() : ValidId(requestedId, "graphId");
        var normalizedScope = scopeId is null ? null : ValidId(scopeId, "scopeId");
        var exists = Scalar("SELECT COUNT(*) FROM graphs WHERE id = $id", ("$id", id)) > 0;
        if (exists)
        {
            var now = Now();
            if (name is not null)
            {
                Execute(
                    "UPDATE graphs SET name = $name, updated_at = $now, revision = revision + 1 WHERE id = $id",
                    ("$name", Required(name, MaxTitleLength, nameof(name))), ("$now", now), ("$id", id));
            }
            if (normalizedScope is not null)
            {
                EnsureScopeExistsLocked(normalizedScope);
                Execute(
                    "UPDATE graphs SET scope_id = $scope, updated_at = $now, revision = revision + 1 WHERE id = $id",
                    ("$scope", normalizedScope), ("$now", now), ("$id", id));
            }
            return id;
        }

        if (Scalar("SELECT COUNT(*) FROM graphs") >= MaxGraphs)
        {
            throw new ArgumentException($"The maximum of {MaxGraphs} graphs already exists.");
        }

        var scope = normalizedScope ?? ActionGraphScope.DefaultId;
        EnsureScopeExistsLocked(scope);
        var createdAt = Now();
        Execute(
            """
            INSERT INTO graphs(id, scope_id, name, created_at, updated_at)
            VALUES($id, $scope, $name, $now, $now)
            """,
            ("$id", id), ("$scope", scope),
            ("$name", Optional(name, MaxTitleLength) ?? id), ("$now", createdAt));
        return id;
    }

    private void EnsureScopeExistsLocked(string scopeId)
    {
        if (Scalar("SELECT COUNT(*) FROM scopes WHERE id = $id", ("$id", scopeId)) == 0)
        {
            throw new ArgumentException($"Scope '{scopeId}' does not exist.");
        }
    }

    private void InsertNodeLocked(string graphId, ActionGraphNode node, string updatedAt, string? createdAt = null)
    {
        Execute(
            """
            INSERT INTO nodes(graph_id, id, kind, title, state, html, x, y, width, height, min_zoom, max_zoom,
                              pinned, attention, hidden, color, url, path, host, project, session_id, external_ref, date, source,
                              created_at, updated_at, revision)
            VALUES($g, $id, $kind, $title, $state, $html, $x, $y, $width, $height, $minZoom, $maxZoom,
                   $pinned, $attention, $hidden, $color, $url, $path, $host, $project, $sessionId, $externalRef, $date, $source,
                   $createdAt, $updatedAt, $revision)
            """,
            ("$g", graphId), ("$id", node.Id), ("$kind", node.Kind), ("$title", node.Title),
            ("$state", (object?)node.State ?? DBNull.Value), ("$html", (object?)node.Html ?? DBNull.Value),
            ("$x", node.X), ("$y", node.Y), ("$width", (object?)node.Width ?? DBNull.Value),
            ("$height", (object?)node.Height ?? DBNull.Value),
            ("$minZoom", (object?)node.MinZoom ?? DBNull.Value),
            ("$maxZoom", (object?)node.MaxZoom ?? DBNull.Value),
            ("$pinned", node.Pinned ? 1 : 0),
            ("$attention", node.Attention ? 1 : 0),
            ("$hidden", node.Hidden ? 1 : 0),
            ("$color", (object?)node.Color ?? DBNull.Value), ("$url", (object?)node.Url ?? DBNull.Value),
            ("$path", (object?)node.Path ?? DBNull.Value), ("$host", (object?)node.Host ?? DBNull.Value),
            ("$project", (object?)node.Project ?? DBNull.Value),
            ("$sessionId", (object?)node.SessionId ?? DBNull.Value),
            ("$externalRef", (object?)node.ExternalRef ?? DBNull.Value),
            ("$date", node.Date is null
                ? DBNull.Value
                : node.Date.Value.ToString("o", CultureInfo.InvariantCulture)),
            ("$source", node.Source), ("$createdAt", createdAt ?? updatedAt), ("$updatedAt", updatedAt),
            ("$revision", node.Revision));

        var ord = 0;
        foreach (var action in node.Actions)
        {
            Execute(
                """
                INSERT INTO node_actions(graph_id, node_id, id, ord, label, cwd, command, profile, prompt, session_name, slash_commands)
                VALUES($g, $n, $id, $ord, $label, $cwd, $command, $profile, $prompt, $sessionName, $slash)
                """,
                ("$g", graphId), ("$n", node.Id), ("$id", action.Id), ("$ord", ord++),
                ("$label", action.Label), ("$cwd", (object?)action.Cwd ?? DBNull.Value),
                ("$command", (object?)action.Command ?? DBNull.Value),
                ("$profile", (object?)action.Profile ?? DBNull.Value),
                ("$prompt", (object?)action.Prompt ?? DBNull.Value),
                ("$sessionName", (object?)action.SessionName ?? DBNull.Value),
                ("$slash", JsonSerializer.Serialize(action.SlashCommands, ActionGraphsJsonContext.Default.ListString)));
        }
    }

    private ActionGraphNode? ReadNodeLocked(string graphId, string nodeId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, title, state, html, x, y, width, height, min_zoom, max_zoom, pinned,
                   color, url, path, host, project, session_id, external_ref, date, source,
                   created_at, updated_at, revision, attention, hidden
            FROM nodes WHERE graph_id = $g AND id = $n
            """;
        command.Parameters.AddWithValue("$g", graphId);
        command.Parameters.AddWithValue("$n", nodeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        var node = new ActionGraphNode
        {
            Id = reader.GetString(0),
            Kind = reader.GetString(1),
            Title = reader.GetString(2),
            State = reader.IsDBNull(3) ? null : reader.GetString(3),
            Html = reader.IsDBNull(4) ? null : reader.GetString(4),
            X = reader.GetDouble(5),
            Y = reader.GetDouble(6),
            Width = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            Height = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            MinZoom = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            MaxZoom = reader.IsDBNull(10) ? null : reader.GetDouble(10),
            Pinned = reader.GetInt32(11) != 0,
            Color = reader.IsDBNull(12) ? null : reader.GetString(12),
            Url = reader.IsDBNull(13) ? null : reader.GetString(13),
            Path = reader.IsDBNull(14) ? null : reader.GetString(14),
            Host = reader.IsDBNull(15) ? null : reader.GetString(15),
            Project = reader.IsDBNull(16) ? null : reader.GetString(16),
            SessionId = reader.IsDBNull(17) ? null : reader.GetString(17),
            ExternalRef = reader.IsDBNull(18) ? null : reader.GetString(18),
            Date = reader.IsDBNull(19) ? null : ReadTimestamp(reader.GetString(19)),
            Source = reader.GetString(20),
            CreatedAt = ReadTimestamp(reader.GetString(21)),
            UpdatedAt = ReadTimestamp(reader.GetString(22)),
            Revision = reader.GetInt32(23),
            Attention = reader.GetInt32(24) != 0,
            Hidden = reader.GetInt32(25) != 0
        };
        reader.Close();
        PopulateNodeActionsLocked(graphId, node);
        PopulateNodeSessionsLocked(graphId, node);
        return node;
    }

    private void PopulateNodeActionsLocked(string graphId, ActionGraphNode node)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, label, cwd, command, profile, prompt, session_name, slash_commands
            FROM node_actions
            WHERE graph_id = $g AND node_id = $n
            ORDER BY ord
            """;
        command.Parameters.AddWithValue("$g", graphId);
        command.Parameters.AddWithValue("$n", node.Id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            node.Actions.Add(new ActionGraphNodeAction
            {
                Id = reader.GetString(0),
                Label = reader.GetString(1),
                Cwd = reader.IsDBNull(2) ? null : reader.GetString(2),
                Command = reader.IsDBNull(3) ? null : reader.GetString(3),
                Profile = reader.IsDBNull(4) ? null : reader.GetString(4),
                Prompt = reader.IsDBNull(5) ? null : reader.GetString(5),
                SessionName = reader.IsDBNull(6) ? null : reader.GetString(6),
                SlashCommands = DeserializeSlashCommands(reader.GetString(7))
            });
        }
    }

    private void PopulateNodeSessionsLocked(string graphId, ActionGraphNode node)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, role, created_at
            FROM node_sessions
            WHERE graph_id = $g AND node_id = $n
            ORDER BY created_at
            """;
        command.Parameters.AddWithValue("$g", graphId);
        command.Parameters.AddWithValue("$n", node.Id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            node.Sessions.Add(new ActionGraphSessionBinding
            {
                SessionId = reader.GetString(0),
                Role = reader.IsDBNull(1) ? null : reader.GetString(1),
                CreatedAt = ReadTimestamp(reader.GetString(2))
            });
        }
        if (node.Sessions.Count == 0 && !string.IsNullOrWhiteSpace(node.SessionId))
        {
            node.Sessions.Add(new ActionGraphSessionBinding
            {
                SessionId = node.SessionId,
                Role = "legacy",
                CreatedAt = node.UpdatedAt
            });
        }
    }

    private void CheckGraphRevisionLocked(string? graphId, int? expectedRevision)
    {
        if (expectedRevision is null || string.IsNullOrWhiteSpace(graphId))
        {
            return;
        }
        var id = ValidId(graphId, nameof(graphId));
        var current = Scalar("SELECT revision FROM graphs WHERE id = $id", ("$id", id));
        if (current == 0)
        {
            throw new ArgumentException($"Graph '{id}' does not exist.");
        }
        CheckRevision($"graph '{id}'", expectedRevision, checked((int)current));
    }

    private static void CheckRevision(string entity, int? expectedRevision, int currentRevision)
    {
        if (expectedRevision is not null && expectedRevision.Value != currentRevision)
        {
            throw new ActionGraphConflictException(entity, expectedRevision.Value, currentRevision);
        }
    }

    private static double? NormalizedZoom(double? value, string field)
    {
        if (value is null)
        {
            return null;
        }
        if (!double.IsFinite(value.Value) || value.Value is < 0.02 or > 8)
        {
            throw new ArgumentException($"{field} must be a finite value between 0.02 and 8.");
        }
        return value.Value;
    }

    private static void ValidateZoomRange(double? minZoom, double? maxZoom)
    {
        if (minZoom is not null && maxZoom is not null && minZoom.Value > maxZoom.Value)
        {
            throw new ArgumentException("minZoom cannot be greater than maxZoom.");
        }
    }

    private static double NodeWidth(ActionGraphNode node) =>
        node.Width ?? (node.Kind == ActionGraphNodeKinds.Frame ? 360 : 224);

    private static double NodeHeight(ActionGraphNode node) =>
        node.Height ?? (node.Kind == ActionGraphNodeKinds.Frame ? 240 : 92);

    private void TouchGraphLocked(string graphId, string now)
    {
        Execute(
            "UPDATE graphs SET updated_at = $now, revision = revision + 1 WHERE id = $id",
            ("$now", now), ("$id", graphId));
    }

    private void CreateSchema()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS scopes(
                id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS graphs(
                id TEXT PRIMARY KEY, scope_id TEXT NOT NULL DEFAULT 'default',
                name TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS nodes(
                graph_id TEXT NOT NULL, id TEXT NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL,
                state TEXT NULL, html TEXT NULL, x REAL NOT NULL, y REAL NOT NULL, width REAL NULL,
                height REAL NULL, min_zoom REAL NULL, max_zoom REAL NULL, pinned INTEGER NOT NULL DEFAULT 0,
                attention INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0,
                color TEXT NULL, url TEXT NULL, path TEXT NULL, host TEXT NULL, project TEXT NULL,
                session_id TEXT NULL, external_ref TEXT NULL, date TEXT NULL, source TEXT NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, revision INTEGER NOT NULL,
                PRIMARY KEY(graph_id, id));
            CREATE TABLE IF NOT EXISTS node_actions(
                graph_id TEXT NOT NULL, node_id TEXT NOT NULL, id TEXT NOT NULL, ord INTEGER NOT NULL,
                label TEXT NOT NULL, cwd TEXT NULL, command TEXT NULL, profile TEXT NULL, prompt TEXT NULL,
                session_name TEXT NULL, slash_commands TEXT NOT NULL,
                PRIMARY KEY(graph_id, node_id, id));
            CREATE TABLE IF NOT EXISTS node_sessions(
                graph_id TEXT NOT NULL, node_id TEXT NOT NULL, session_id TEXT NOT NULL,
                role TEXT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY(graph_id, node_id, session_id));
            CREATE TABLE IF NOT EXISTS edges(
                graph_id TEXT NOT NULL, id TEXT NOT NULL, from_id TEXT NOT NULL, to_id TEXT NOT NULL,
                label TEXT NULL, kind TEXT NULL, created_at TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY(graph_id, id));
            """);
        Execute(
            "INSERT OR IGNORE INTO scopes(id, name, created_at) VALUES('default', 'Default', $now)",
            ("$now", Now()));
        EnsureColumnLocked("nodes", "height", "REAL NULL");
        EnsureColumnLocked("nodes", "min_zoom", "REAL NULL");
        EnsureColumnLocked("nodes", "max_zoom", "REAL NULL");
        EnsureColumnLocked("nodes", "pinned", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnLocked("nodes", "attention", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnLocked("nodes", "hidden", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnLocked("graphs", "revision", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumnLocked("graphs", "refresh_command", "TEXT NULL");
        EnsureColumnLocked("graphs", "refresh_cwd", "TEXT NULL");
        EnsureColumnLocked("graphs", "refresh_prompt", "TEXT NULL");
        EnsureColumnLocked("node_actions", "command", "TEXT NULL");
        EnsureColumnLocked("edges", "revision", "INTEGER NOT NULL DEFAULT 1");
    }

    /// <summary>Persist the graph's refresh spec; only fields present in the request change, empty strings clear.</summary>
    private void ApplyRefreshSpecLocked(string graphId, CreateActionGraphRequest request)
    {
        if (request.RefreshCommand is null && request.RefreshCwd is null && request.RefreshPrompt is null)
        {
            return;
        }
        var now = Now();
        if (request.RefreshCommand is not null)
        {
            Execute("UPDATE graphs SET refresh_command = $v, updated_at = $now, revision = revision + 1 WHERE id = $id",
                ("$v", (object?)Optional(request.RefreshCommand, 512) ?? DBNull.Value), ("$now", now), ("$id", graphId));
        }
        if (request.RefreshCwd is not null)
        {
            Execute("UPDATE graphs SET refresh_cwd = $v, updated_at = $now, revision = revision + 1 WHERE id = $id",
                ("$v", (object?)Optional(request.RefreshCwd, MaxReferenceLength) ?? DBNull.Value), ("$now", now), ("$id", graphId));
        }
        if (request.RefreshPrompt is not null)
        {
            Execute("UPDATE graphs SET refresh_prompt = $v, updated_at = $now, revision = revision + 1 WHERE id = $id",
                ("$v", (object?)Optional(request.RefreshPrompt, MaxPromptLength) ?? DBNull.Value), ("$now", now), ("$id", graphId));
        }
    }

    private void EnsureColumnLocked(string table, string column, string definition)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        reader.Close();
        Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    /// <summary>One-time import of the pre-SQLite JSON document into the default scope.</summary>
    private void MigrateLegacyJsonDocument(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath) || Scalar("SELECT COUNT(*) FROM graphs") > 0)
            {
                return;
            }

            var document = JsonSerializer.Deserialize(
                File.ReadAllText(jsonPath),
                ActionGraphsJsonContext.Default.ActionGraphsDocument);
            if (document is not null)
            {
                using var transaction = _connection.BeginTransaction();
                foreach (var graph in document.Graphs)
                {
                    var createdAt = graph.CreatedAt.ToString("o", CultureInfo.InvariantCulture);
                    var updatedAt = graph.UpdatedAt.ToString("o", CultureInfo.InvariantCulture);
                    Execute(
                        """
                        INSERT OR IGNORE INTO graphs(id, scope_id, name, created_at, updated_at)
                        VALUES($id, 'default', $name, $created, $updated)
                        """,
                        ("$id", graph.Id), ("$name", graph.Name), ("$created", createdAt), ("$updated", updatedAt));
                    foreach (var node in graph.Nodes)
                    {
                        InsertNodeLocked(
                            graph.Id,
                            node,
                            node.UpdatedAt.ToString("o", CultureInfo.InvariantCulture),
                            node.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
                    }
                    foreach (var edge in graph.Edges)
                    {
                        Execute(
                            """
                            INSERT OR IGNORE INTO edges(graph_id, id, from_id, to_id, label, kind, created_at)
                            VALUES($g, $id, $from, $to, $label, $kind, $created)
                            """,
                            ("$g", graph.Id), ("$id", edge.Id), ("$from", edge.FromId), ("$to", edge.ToId),
                            ("$label", (object?)edge.Label ?? DBNull.Value),
                            ("$kind", (object?)edge.Kind ?? DBNull.Value),
                            ("$created", edge.CreatedAt.ToString("o", CultureInfo.InvariantCulture)));
                    }
                }
                transaction.Commit();
            }

            File.Move(jsonPath, jsonPath + ".migrated", overwrite: true);
            Log.Info(() => $"Migrated action graphs from '{jsonPath}' into SQLite.");
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Could not migrate legacy action graphs from '{jsonPath}': {ex.Message}");
        }
    }

    private static List<ActionGraphNodeAction> NormalizedActions(List<ActionGraphNodeAction>? actions)
    {
        if (actions is null || actions.Count == 0)
        {
            return [];
        }
        if (actions.Count > MaxActionsPerNode)
        {
            throw new ArgumentException($"A node may hold at most {MaxActionsPerNode} actions.");
        }

        var normalized = new List<ActionGraphNodeAction>(actions.Count);
        foreach (var action in actions)
        {
            normalized.Add(new ActionGraphNodeAction
            {
                Id = string.IsNullOrWhiteSpace(action.Id) ? NewId() : ValidId(action.Id, "actionId"),
                Label = Required(action.Label, MaxLabelLength, "action label"),
                Cwd = Optional(action.Cwd, MaxReferenceLength),
                Command = Optional(action.Command, 512),
                Profile = Optional(action.Profile, 64),
                Prompt = Optional(action.Prompt, MaxPromptLength),
                SessionName = Optional(action.SessionName, MaxTitleLength),
                SlashCommands = (action.SlashCommands ?? [])
                    .Where(static command => !string.IsNullOrWhiteSpace(command))
                    .Select(static command => command.Trim())
                    .Take(8)
                    .ToList()
            });
        }
        return normalized;
    }

    private static List<string> DeserializeSlashCommands(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ActionGraphsJsonContext.Default.ListString) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }

    private int ExecuteRows(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command.ExecuteNonQuery();
    }

    private long Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string ValidId(string? value, string field)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length is 0 or > 64 || !trimmed.All(static c =>
                char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException($"{field} must match [A-Za-z0-9._-] with 1-64 characters.");
        }
        return trimmed;
    }

    private static string Required(string? value, int maxLength, string field)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"{field} is required.");
        }
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? Optional(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            _disposed = true;
        }
        _connection.Dispose();
    }
}
