using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Models.Browser;

namespace Ai.Tlbx.MidTerm.Services.Boards;

public static class BoardEndpoints
{
    public static void MapBoardEndpoints(WebApplication app, BoardService boards)
    {
        var previews = app.Services.GetRequiredService<WebPreviewService>();
        var registry = app.Services.GetRequiredService<BrowserPreviewRegistry>();
        var owners = app.Services.GetRequiredService<BrowserPreviewOwnerService>();
        var commands = app.Services.GetRequiredService<BrowserCommandService>();
        var browserUi = app.Services.GetRequiredService<BrowserUiBridge>();
        app.MapGet("/api/boards", () => Results.Json(boards.List(), BoardJsonContext.Default.ListBoardDocument));
        app.MapGet("/api/boards/calendar", (string? month) => Try(() => Results.Json(
            BoardCalendar.Month(month), BoardJsonContext.Default.BoardCalendarMonth)));
        app.MapPost("/api/boards", (BoardPatch request) => Try(() =>
            Results.Json(boards.Create(request), BoardJsonContext.Default.BoardDocument)));
        app.MapGet("/api/boards/{id}", (string id) => boards.Get(id) is { } board
            ? Results.Json(board, BoardJsonContext.Default.BoardDocument) : Results.NotFound());
        app.MapPatch("/api/boards/{id}", (string id, BoardPatch request) => Try(() => boards.Update(id, request) is { } board
            ? Results.Json(board, BoardJsonContext.Default.BoardDocument) : Results.NotFound()));
        app.MapPatch("/api/boards/{id}/viewport", (string id, BoardViewport request) => Try(() =>
            boards.UpdateViewport(id, request) ? Results.NoContent() : Results.NotFound()));
        app.MapDelete("/api/boards/{id}", (string id, long? expectedRevision) => Try(() =>
        {
            if (!boards.Delete(id, null, expectedRevision)) return Results.NotFound();
            var session = $"board-{id}";
            previews.ClearSession(session);
            registry.ClearSession(session);
            owners.ClearSession(session);
            return Results.NoContent();
        }));
        app.MapPost("/api/boards/{id}/items", (string id, BoardItemWrite request) => Try(() =>
            boards.WriteItem(id, null, request) is { } item
                ? Results.Json(item, BoardJsonContext.Default.BoardItem) : Results.NotFound()));
        app.MapGet("/api/boards/{id}/items/{itemId}", (string id, string itemId) => boards.GetItem(id, itemId) is { } item
            ? Results.Json(item, BoardJsonContext.Default.BoardItem) : Results.NotFound());
        app.MapPut("/api/boards/{id}/items/{itemId}", (string id, string itemId, BoardItemWrite request) => Try(() =>
            boards.WriteItem(id, itemId, request) is { } item
                ? Results.Json(item, BoardJsonContext.Default.BoardItem) : Results.NotFound()));
        app.MapDelete("/api/boards/{id}/items/{itemId}", (string id, string itemId, long? expectedRevision) => Try(() =>
        {
            var item = boards.GetItem(id, itemId);
            if (!boards.Delete(id, itemId, expectedRevision)) return Results.NotFound();
            if (item is { Kind: "browser", BrowserSessionId: null, BrowserPreviewName: null })
            {
                var session = $"board-{id}";
                previews.DeletePreviewSession(session, itemId);
                registry.Remove(session, itemId);
                owners.Release(session, itemId);
            }
            return Results.NoContent();
        }));
        app.MapPost("/api/boards/{id}/items/{itemId}/browser", (
            string id, string itemId, HttpRequest httpRequest) => Try(() =>
        {
            var item = boards.GetItem(id, itemId);
            if (item is null) return Results.NotFound();
            if (item.Kind != "browser") return Results.BadRequest("This item is not a browser tile.");
            var url = item.Url;
            if (item.LocalDirectory is { } directory)
            {
                var file = WebPreviewLocalDirectory.ResolveFile(directory, "index.html");
                if (file is null) return Results.BadRequest("Local program requires an index.html inside its directory.");
                url = new Uri(file).AbsoluteUri;
            }
            var session = item.BrowserSessionId ?? $"board-{id}";
            var preview = item.BrowserPreviewName ?? item.Id;
            if (url is not null && !previews.ConfigureBoardTarget(session, preview, url, item.LocalDirectory,
                    $"{id}/{itemId}\0{url}\0{item.LocalDirectory}"))
                return Results.BadRequest("Unsupported browser target.");
            var browser = BrowserIdentity.TryBuildFromBrowserRequest(httpRequest);
            if (item.BrowserSessionId is null && owners.GetOwnerBrowserId(session, preview) is null
                && browser is not null && owners.IsConnected(browser)) owners.Claim(session, preview, browser);
            return Results.Json(previews.EnsurePreviewSession(session, preview), AppJsonContext.Default.WebPreviewSessionInfo);
        }));
        app.MapPost("/api/boards/{id}/items/{itemId}/browser-command", async (
            string id, string itemId, BrowserCommandRequest request, CancellationToken ct) =>
        {
            var item = boards.GetItem(id, itemId);
            if (item is null) return Results.NotFound();
            if (item.Kind != "browser") return Results.BadRequest("This item is not a browser tile.");
            var scoped = new BrowserCommandRequest
            {
                SessionId = item.BrowserSessionId ?? $"board-{id}", PreviewName = item.BrowserPreviewName ?? itemId,
                Command = request.Command, Selector = request.Selector, Value = request.Value,
                MaxDepth = request.MaxDepth, TextOnly = request.TextOnly, Timeout = request.Timeout,
                DeltaX = request.DeltaX, DeltaY = request.DeltaY, Steps = request.Steps,
                FullPage = request.FullPage, WaitForNavigation = request.WaitForNavigation
            };
            var result = await commands.ExecuteCommandAsync(scoped, ct).ConfigureAwait(false);
            return Results.Json(new BrowserCommandResponse
            {
                Success = result.Success, Result = result.Result, Error = result.Error, MatchCount = result.MatchCount
            }, AppJsonContext.Default.BrowserCommandResponse, statusCode: result.Success ? 200 : 409);
        });
        app.MapPost("/api/boards/{id}/items/{itemId}/browser-claim", (string id, string itemId, HttpRequest request) => Try(() =>
        {
            var item = boards.GetItem(id, itemId);
            if (item is null) return Results.NotFound();
            if (item.Kind != "browser") return Results.BadRequest("This item is not a browser tile.");
            var browser = BrowserIdentity.TryBuildFromBrowserRequest(request);
            if (browser is null) return Results.BadRequest("A connected board window is required.");
            return browserUi.RequestClaim(item.BrowserSessionId ?? $"board-{id}", item.BrowserPreviewName ?? itemId, out var error, browser)
                ? Results.NoContent() : Results.Problem(error, statusCode: 409);
        }));
    }

    private static IResult Try(Func<IResult> action)
    {
        try { return action(); }
        catch (BoardConflictException ex) { return Results.Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        catch (IOException) { return Results.Problem("Board storage is temporarily unavailable.", statusCode: 503); }
        catch (UnauthorizedAccessException) { return Results.Problem("Board storage is not accessible.", statusCode: 503); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return Results.Problem("Board storage is temporarily unavailable.", statusCode: 503); }
    }
}
