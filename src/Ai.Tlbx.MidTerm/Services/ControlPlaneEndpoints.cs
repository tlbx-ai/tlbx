using Ai.Tlbx.MidTerm.Models.ControlPlane;
using Ai.Tlbx.MidTerm.Models.InputHistory;
using Ai.Tlbx.MidTerm.Services.Sessions;

namespace Ai.Tlbx.MidTerm.Services;

public static class ControlPlaneEndpoints
{
    public static void MapControlPlaneEndpoints(
        WebApplication app,
        ControlPlaneService controlPlane,
        TtyHostSessionManager sessionManager,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        ManagerBarQueueService commandBayQueue,
        InputHistoryService inputHistory)
    {
        app.MapGet("/api/control-plane", (int workLimit = 100, int checkpointLimit = 30) =>
        {
            var snapshot = new ControlPlaneSnapshotResponse
            {
                WorkItems = controlPlane.GetWorkItems(null, null, null, null, workLimit).Items,
                SessionStatuses = controlPlane.GetSessionStatuses(null).Statuses,
                Checkpoints = controlPlane.GetCheckpoints(null, null, checkpointLimit).Checkpoints
            };
            return Results.Json(snapshot, AppJsonContext.Default.ControlPlaneSnapshotResponse);
        });

        app.MapGet("/api/control-plane/work-items", (
            string? state,
            string? kind,
            string? sessionId,
            string? project,
            int limit = 100) => Try(() => Results.Json(
                controlPlane.GetWorkItems(state, kind, sessionId, project, limit),
                AppJsonContext.Default.ControlPlaneWorkItemListResponse)));

        app.MapPost("/api/control-plane/work-items", (CreateControlPlaneWorkItemRequest request) =>
            Try(() => Results.Json(
                controlPlane.CreateWorkItem(request),
                AppJsonContext.Default.ControlPlaneWorkItem)));

        app.MapPatch("/api/control-plane/work-items/{id}", (
            string id,
            UpdateControlPlaneWorkItemRequest request) => Try(() =>
        {
            var item = controlPlane.UpdateWorkItem(id, request);
            return item is null
                ? Results.NotFound()
                : Results.Json(item, AppJsonContext.Default.ControlPlaneWorkItem);
        }));

        app.MapDelete("/api/control-plane/work-items/{id}", (string id) => Try(() =>
            controlPlane.RemoveWorkItem(id) ? Results.Ok() : Results.NotFound()));

        app.MapGet("/api/control-plane/session-status", (string? sessionId) => Results.Json(
            controlPlane.GetSessionStatuses(sessionId),
            AppJsonContext.Default.ControlPlaneSessionStatusListResponse));

        app.MapPut("/api/control-plane/session-status/{sessionId}", (
            string sessionId,
            PublishControlPlaneSessionStatusRequest request) => Try(() => Results.Json(
                controlPlane.PublishSessionStatus(sessionId, request),
                AppJsonContext.Default.ControlPlaneSessionStatus)));

        app.MapDelete("/api/control-plane/session-status/{sessionId}", (string sessionId) => Try(() =>
            controlPlane.ClearSessionStatus(sessionId) ? Results.Ok() : Results.NotFound()));

        app.MapGet("/api/control-plane/checkpoints", (string? sessionId, string? kind, int limit = 100) =>
            Results.Json(
                controlPlane.GetCheckpoints(sessionId, kind, limit),
                AppJsonContext.Default.ControlPlaneCheckpointListResponse));

        app.MapPost("/api/control-plane/checkpoints", (CreateControlPlaneCheckpointRequest request) =>
            Try(() => Results.Json(
                controlPlane.CreateCheckpoint(request),
                AppJsonContext.Default.ControlPlaneCheckpoint)));

        app.MapGet("/api/control-plane/events", (long after = 0, int limit = 100) => Results.Json(
            controlPlane.GetEvents(after, limit),
            AppJsonContext.Default.ControlPlaneEventListResponse));

        app.MapGet("/api/control-plane/capabilities", () =>
        {
            var sessions = sessionManager.GetSessionList().Sessions
                .OrderBy(static session => session.Order)
                .Select(session => new ControlPlaneSessionCapability
                {
                    SessionId = session.Id,
                    IsRunning = session.IsRunning,
                    AgentControlled = session.AgentControlled,
                    AppServerControlAttached = appServerControlRuntime.IsAttached(session.Id),
                    AppServerControlOnly = session.AppServerControlOnly,
                    Surface = session.Surface,
                    ProfileHint = session.ProfileHint,
                    PromptModes = session.IsRunning
                        ? appServerControlRuntime.IsAttached(session.Id) || session.AppServerControlOnly
                            ? ["terminal", "appServerControl"]
                            : ["terminal"]
                        : []
                })
                .ToList();
            var response = new ControlPlaneCapabilitiesResponse
            {
                Features =
                [
                    "inputHistory",
                    "controlPlane",
                    "operatorView",
                    "multiSessionDispatch",
                    "hubControlPlane",
                    "exactEvents"
                ],
                WorkItemStates = ControlPlaneWorkItemStates.All.Order(StringComparer.Ordinal).ToList(),
                SessionStates = ControlPlaneSessionStates.All.Order(StringComparer.Ordinal).ToList(),
                Sessions = sessions
            };
            return Results.Json(response, AppJsonContext.Default.ControlPlaneCapabilitiesResponse);
        });

        app.MapPost("/api/control-plane/dispatch", async (
            ControlPlaneDispatchRequest request,
            CancellationToken ct) =>
        {
            var sessionIds = (request.SessionIds ?? [])
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var turn = InputHistoryService.CloneTurn(request.Turn);
            if (sessionIds.Count is 0 or > 32 || turn is null)
            {
                return Results.BadRequest("Dispatch requires one to 32 explicit session ids and a replayable turn.");
            }

            var tasks = sessionIds.Select(async sessionId =>
            {
                var session = sessionManager.GetSession(sessionId);
                if (session is null)
                {
                    return new ControlPlaneDispatchResult
                    {
                        SessionId = sessionId,
                        Error = "Session not found."
                    };
                }

                try
                {
                    var accepted = await commandBayQueue.DispatchPromptDirectAsync(sessionId, turn, ct).ConfigureAwait(false);
                    if (accepted)
                    {
                        inputHistory.RecordPrompt(
                            sessionId,
                            session.Name,
                            session.CurrentDirectory,
                            InputHistorySources.ControlPlaneDispatch,
                            appServerControlRuntime.IsAttached(sessionId)
                                ? InputHistorySurfaces.AgentControl
                                : InputHistorySurfaces.Terminal,
                            turn);
                    }

                    return new ControlPlaneDispatchResult
                    {
                        SessionId = sessionId,
                        Accepted = accepted,
                        Queued = false,
                        Error = accepted ? null : "Prompt was not accepted."
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new ControlPlaneDispatchResult
                    {
                        SessionId = sessionId,
                        Error = ex.Message
                    };
                }
            });
            var response = new ControlPlaneDispatchResponse
            {
                Results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList()
            };
            return Results.Json(response, AppJsonContext.Default.ControlPlaneDispatchResponse);
        });
    }

    private static IResult Try(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }
}
