using Ai.Tlbx.MidTerm.Models.Sessions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ai.Tlbx.MidTerm.Services.Boards;

public static class BoardSessionEndpoints
{
    public static void Map(WebApplication app,
        Func<WorkerBootstrapRequest, CancellationToken, Action<string>?, Task<IResult>> bootstrap,
        Func<string, SessionPromptRequest, HttpContext, CancellationToken, bool, Task<IResult>> prompt)
    {
        var boards = app.Services.GetRequiredService<BoardService>();
        app.MapGet("/api/boards/{id}/launches", (string id, string? itemId, int? limit, int? offset) =>
        {
            try { return Results.Json(boards.ListLaunches(id, itemId, limit ?? 50, offset ?? 0), BoardJsonContext.Default.ListBoardLaunch); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });
        app.MapPost("/api/boards/{id}/items/{itemId}/launch", async (string id, string itemId, BoardLaunchRequest request, HttpContext context) =>
        {
            BoardLaunchStart start;
            try { start = boards.BeginLaunch(id, itemId, request, $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}"); }
            catch (BoardConflictException ex) { return Results.Problem(ex.Message, statusCode: 409); }
            catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: 404); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            var launch = start.Launch;
            if (!start.Created) return Results.Json(launch, BoardJsonContext.Default.BoardLaunch);
            // Once committed, a disappearing browser must not leave a half-created session or resend its prompt.
            var ct = app.Lifetime.ApplicationStopping;
            try
            {
                var result = await bootstrap(new()
                {
                    Name = launch.Name, WorkingDirectory = launch.WorkingDirectory,
                    LaunchCommand = launch.LaunchCommand, Profile = launch.Profile,
                    AppServerControlOnly = launch.AppServerControlOnly,
                    Surface = launch.AppServerControlOnly ? "agent-control" : "terminal"
                }, ct, sessionId => launch = boards.UpdateLaunch(launch, "starting", sessionId));
                if (result is not JsonHttpResult<WorkerBootstrapResponse> { Value: { } worker })
                    return Finish("failed", ResultError(result));
                launch = boards.UpdateLaunch(launch, "submitting", worker.Session.Id);
                var submitted = await prompt(worker.Session.Id, new() { Text = launch.Prompt, Profile = worker.Profile, Mode = "append" }, context, ct, false);
                return submitted is IStatusCodeHttpResult { StatusCode: >= 400 }
                    ? Finish("failed", ResultError(submitted)) : Finish("submitted", null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The receipt retains any created session; uncertain delivery is never retried automatically.
                return Finish(launch.SessionId is null ? "failed" : "uncertain", ex.Message);
            }

            IResult Finish(string state, string? error)
            {
                launch = boards.UpdateLaunch(launch, state, error: error);
                return Results.Json(launch, BoardJsonContext.Default.BoardLaunch);
            }
        });
    }

    private static string ResultError(IResult result) => result is IValueHttpResult { Value: string message }
        ? message : result is IValueHttpResult { Value: Microsoft.AspNetCore.Mvc.ProblemDetails problem }
            ? problem.Detail ?? problem.Title ?? "Session launch failed." : "Session launch failed.";
}
