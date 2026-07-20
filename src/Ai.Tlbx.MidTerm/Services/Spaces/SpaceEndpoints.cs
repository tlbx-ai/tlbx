using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.Spaces;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace Ai.Tlbx.MidTerm.Services.Spaces;

public static class SpaceEndpoints
{
    public static void MapSpaceEndpoints(
        WebApplication app,
        SpaceService spaceService,
        TtyHostSessionManager sessionManager,
        SessionAgentFeedService agentFeed,
        SessionSupervisorService sessionSupervisor,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        WorkerSessionRegistryService workerSessionRegistry,
        TerminalSizeControlService terminalSizeControlService)
    {
        app.MapGet("/api/spaces", async (bool? includeWorkspaces, bool? pinnedOnly, CancellationToken ct) =>
        {
            var spaces = await spaceService
                .GetSpacesAsync(
                    sessionManager,
                    includeWorkspaces ?? true,
                    pinnedOnly ?? false,
                    ct)
                .ConfigureAwait(false);
            return Results.Json(spaces, AppJsonContext.Default.ListSpaceSummaryDto);
        });

        app.MapPost("/api/spaces/import", async (SpaceImportRequest request, CancellationToken ct) =>
        {
            var space = await spaceService.ImportAsync(request.Path, request.Label, sessionManager, ct).ConfigureAwait(false);
            return space is null
                ? Results.BadRequest("Path not found.")
                : Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
        });

        app.MapPatch("/api/spaces/{id}", async (string id, SpaceUpdateRequest request, CancellationToken ct) =>
        {
            if (!spaceService.UpdateSpace(id, request.Label, request.IsPinned))
            {
                return Results.NotFound();
            }

            var space = await spaceService.GetSpaceAsync(id, sessionManager, ct).ConfigureAwait(false);
            return space is null
                ? Results.NotFound()
                : Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
        });

        app.MapDelete("/api/spaces/{id}", (string id) =>
        {
            return spaceService.Delete(id) ? Results.Ok() : Results.NotFound();
        });

        app.MapGet("/api/spaces/{id}/workspaces", async (string id, CancellationToken ct) =>
        {
            var space = await spaceService.GetSpaceAsync(id, sessionManager, ct).ConfigureAwait(false);
            return space is null
                ? Results.NotFound()
                : Results.Json(space.Workspaces, AppJsonContext.Default.ListSpaceWorkspaceDto);
        });

        app.MapPost("/api/spaces/{id}/git/init", async (string id, CancellationToken ct) =>
        {
            var space = await spaceService.InitGitAsync(id, sessionManager, ct).ConfigureAwait(false);
            return space is null
                ? Results.NotFound()
                : Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
        });

        app.MapPost("/api/spaces/{id}/worktrees", async (string id, SpaceCreateWorktreeRequest request, CancellationToken ct) =>
        {
            try
            {
                var space = await spaceService
                    .CreateWorktreeAsync(id, request.Path, request.BranchName, request.Name, sessionManager, ct)
                    .ConfigureAwait(false);
                return space is null
                    ? Results.NotFound()
                    : Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPatch("/api/spaces/{id}/workspaces/{key}", async (string id, string key, SpaceUpdateWorkspaceRequest request, CancellationToken ct) =>
        {
            try
            {
                var space = await spaceService
                    .UpdateWorkspaceLabelAsync(id, key, request.Label, sessionManager, ct)
                    .ConfigureAwait(false);
                return space is null
                    ? Results.NotFound()
                    : Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapDelete("/api/spaces/{id}/workspaces/{key}", async (string id, string key, bool? force, CancellationToken ct) =>
        {
            try
            {
                var space = await spaceService
                    .RemoveWorktreeAsync(id, key, force == true, sessionManager, ct)
                    .ConfigureAwait(false);
                return space is null
                    ? Results.NotFound()
                    : Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPost("/api/spaces/{id}/workspaces/{key}/launch", async (
            string id,
            string key,
            SpaceLaunchRequest request,
            HttpRequest httpRequest,
            CancellationToken ct) =>
        {
            var workspacePath = await spaceService.ResolveWorkspacePathAsync(id, key, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                return Results.NotFound("Workspace not found.");
            }

            var surface = NormalizeSurface(request.Surface);
            if (surface is null)
            {
                return Results.BadRequest("Unsupported surface.");
            }

            if (surface == SpaceSurfaceKinds.Terminal)
            {
                var creation = await sessionManager.CreateSessionDetailedAsync(
                    request.Shell,
                    request.Cols,
                    request.Rows,
                    workspacePath,
                    ct).ConfigureAwait(false);
                if (!creation.Succeeded)
                {
                    return CreateLaunchProblem(creation.Failure);
                }

                var sessionId = creation.Session!.Id;
                ApplySessionSpaceMetadata(sessionManager, sessionId, id, workspacePath, surface);
                var browserId = BrowserIdentity.TryBuildFromBrowserRequest(httpRequest);
                if (browserId is not null)
                {
                    terminalSizeControlService.AssignNewSession(sessionId, browserId);
                }
                return Results.Json(
                    BuildSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, sessionId),
                    AppJsonContext.Default.SessionInfoDto);
            }

            var workerCreation = await sessionManager.CreateSessionDetailedAsync(
                request.Shell,
                request.Cols,
                request.Rows,
                workspacePath,
                applyTerminalEnvironmentVariables: false,
                ct).ConfigureAwait(false);
            if (!workerCreation.Succeeded)
            {
                return CreateLaunchProblem(workerCreation.Failure);
            }

            var session = workerCreation.Session!;
            var sessionIdForWorker = session.Id;
            ApplySessionSpaceMetadata(sessionManager, sessionIdForWorker, id, workspacePath, surface);
            sessionManager.SetAgentControlled(sessionIdForWorker, false);
            sessionManager.SetAppServerControlOnly(sessionIdForWorker, true);
            sessionManager.SetProfileHint(sessionIdForWorker, surface);
            workerSessionRegistry.Register(
                sessionIdForWorker,
                surface,
                launchCommand: null,
                slashCommands: [],
                launchDelayMs: 0,
                slashCommandDelayMs: 350);
            agentFeed.NoteWorkerBootstrap(
                sessionIdForWorker,
                surface,
                launchCommand: null,
                slashCommands: [],
                guidanceInjected: false);

            return Results.Json(
                BuildSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, sessionIdForWorker),
                AppJsonContext.Default.SessionInfoDto);
        });

        app.MapGet("/api/recents", (int? count) =>
        {
            var entries = spaceService.GetRecentEntries(count ?? 6);
            return Results.Json(entries, AppJsonContext.Default.ListLaunchEntry);
        });

        app.MapDelete("/api/recents/{id}", (string id) =>
        {
            return spaceService.RemoveRecentEntry(id) ? Results.Ok() : Results.NotFound();
        });
    }

    private static string? NormalizeSurface(string? surface)
    {
        return surface?.Trim().ToLowerInvariant() switch
        {
            SpaceSurfaceKinds.Terminal => SpaceSurfaceKinds.Terminal,
            SpaceSurfaceKinds.Codex => SpaceSurfaceKinds.Codex,
            SpaceSurfaceKinds.Claude => SpaceSurfaceKinds.Claude,
            SpaceSurfaceKinds.Grok => SpaceSurfaceKinds.Grok,
            _ => null
        };
    }

    private static void ApplySessionSpaceMetadata(
        TtyHostSessionManager sessionManager,
        string sessionId,
        string? spaceId,
        string? workspacePath,
        string? surface)
    {
        sessionManager.SetLaunchOrigin(sessionId, SessionLaunchOrigins.Space);
        sessionManager.SetSpaceId(sessionId, spaceId);
        sessionManager.SetWorkspacePath(sessionId, workspacePath);
        sessionManager.SetSurface(sessionId, surface);
    }

    private static SessionInfoDto BuildSessionDto(
        TtyHostSessionManager sessionManager,
        SessionSupervisorService sessionSupervisor,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        string sessionId)
    {
        var response = sessionManager.GetSessionList();
        foreach (var session in response.Sessions)
        {
            session.Supervisor = sessionSupervisor.Describe(session);
            session.HasAppServerControlHistory = appServerControlRuntime.HasHistory(session.Id);
        }

        return response.Sessions.First(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
    }

    private static IResult CreateLaunchProblem(SessionLaunchFailure? failure)
    {
        var statusCode = failure?.Stage == "limits"
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status500InternalServerError;

        return Results.Problem(new ProblemDetails
        {
            Title = "Session start failed",
            Status = statusCode,
            Detail = failure?.Message ?? "Failed to launch the requested space surface."
        });
    }
}
