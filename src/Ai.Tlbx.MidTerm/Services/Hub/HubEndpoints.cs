using Ai.Tlbx.MidTerm.Models.Hub;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.Spaces;
using Ai.Tlbx.MidTerm.Models.ControlPlane;
using Ai.Tlbx.MidTerm.Services.Hub;

namespace Ai.Tlbx.MidTerm.Services.Hub;

public static class HubEndpoints
{
    public static void MapHubEndpoints(WebApplication app, HubService hubService)
    {
        app.MapGet("/api/hub/state", async (CancellationToken ct) =>
        {
            var state = await hubService.GetStateAsync(ct);
            return Results.Json(state, AppJsonContext.Default.HubStateResponse);
        });

        app.MapPost("/api/hub/machines", (HubMachineUpsertRequest request) =>
        {
            var machine = hubService.UpsertMachine(id: null, request);
            return Results.Json(machine, AppJsonContext.Default.HubMachineInfo);
        });

        app.MapPut("/api/hub/machines/{id}", (string id, HubMachineUpsertRequest request) =>
        {
            var machine = hubService.UpsertMachine(id, request);
            return Results.Json(machine, AppJsonContext.Default.HubMachineInfo);
        });

        app.MapDelete("/api/hub/machines/{id}", (string id) =>
        {
            return hubService.DeleteMachine(id) ? Results.Ok() : Results.NotFound();
        });

        app.MapPost("/api/hub/machines/{id}/refresh", async (string id, CancellationToken ct) =>
        {
            var machine = await hubService.GetMachineStateAsync(id, ct);
            return Results.Json(machine, AppJsonContext.Default.HubMachineState);
        });

        app.MapGet("/api/hub/machines/{id}/control-plane", async (string id, CancellationToken ct) =>
        {
            var snapshot = await hubService.GetControlPlaneAsync(id, ct);
            return Results.Json(snapshot, AppJsonContext.Default.ControlPlaneSnapshotResponse);
        });

        app.MapGet("/api/hub/machines/{id}/control-plane/events", async (
            string id,
            long after,
            int limit,
            CancellationToken ct) =>
        {
            var events = await hubService.GetControlPlaneEventsAsync(id, after, limit, ct);
            return Results.Json(events, AppJsonContext.Default.ControlPlaneEventListResponse);
        });

        app.MapGet("/api/hub/machines/{id}/control-plane/capabilities", async (
            string id,
            CancellationToken ct) =>
        {
            var capabilities = await hubService.GetControlPlaneCapabilitiesAsync(id, ct);
            return Results.Json(capabilities, AppJsonContext.Default.ControlPlaneCapabilitiesResponse);
        });

        app.MapPost("/api/hub/machines/{id}/pin", async (string id, HubMachinePinRequest request, CancellationToken ct) =>
        {
            var fingerprint = request.Fingerprint;
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                var machine = await hubService.GetMachineStateAsync(id, ct);
                fingerprint = machine.Machine.LastFingerprint;
            }

            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                return Results.BadRequest("No fingerprint available to pin.");
            }

            var pinned = hubService.PinFingerprint(id, fingerprint);
            return Results.Json(new HubMachinePinRequest { Fingerprint = pinned }, AppJsonContext.Default.HubMachinePinRequest);
        });

        app.MapDelete("/api/hub/machines/{id}/pin", (string id) =>
        {
            return hubService.ClearPinnedFingerprint(id) ? Results.Ok() : Results.NotFound();
        });

        app.MapPost("/api/hub/machines/{id}/sessions", async (string id, CreateSessionRequest? request, CancellationToken ct) =>
        {
            var session = await hubService.CreateSessionAsync(id, request, ct);
            return Results.Json(session, AppJsonContext.Default.SessionInfoDto);
        });

        app.MapDelete("/api/hub/machines/{id}/sessions/{sessionId}", async (string id, string sessionId, CancellationToken ct) =>
        {
            await hubService.DeleteSessionAsync(id, sessionId, ct);
            return Results.Ok();
        });

        app.MapPut("/api/hub/machines/{id}/sessions/{sessionId}/name", async (string id, string sessionId, RenameSessionRequest request, CancellationToken ct) =>
        {
            await hubService.RenameSessionAsync(id, sessionId, request, ct);
            return Results.Ok();
        });

        app.MapPost("/api/hub/machines/{id}/history", async (string id, CreateHistoryRequest request, CancellationToken ct) =>
        {
            var response = await hubService.CreateHistoryEntryAsync(id, request, ct);
            return Results.Json(response, AppJsonContext.Default.CreateHistoryResponse);
        });

        app.MapPut("/api/hub/machines/{id}/sessions/{sessionId}/bookmark", async (string id, string sessionId, SetBookmarkRequest request, CancellationToken ct) =>
        {
            await hubService.SetSessionBookmarkAsync(id, sessionId, request, ct);
            return Results.Ok();
        });

        app.MapGet("/api/hub/machines/{id}/files/picker/home", async (string id, CancellationToken ct) =>
        {
            var response = await hubService.GetLauncherHomeAsync(id, ct);
            return Results.Json(response, AppJsonContext.Default.LauncherPathResponse);
        });

        app.MapGet("/api/hub/machines/{id}/files/picker/roots", async (string id, CancellationToken ct) =>
        {
            var response = await hubService.GetLauncherRootsAsync(id, ct);
            return Results.Json(response, AppJsonContext.Default.LauncherDirectoryListResponse);
        });

        app.MapGet("/api/hub/machines/{id}/files/picker/directories", async (string id, string path, CancellationToken ct) =>
        {
            var response = await hubService.GetLauncherDirectoriesAsync(id, path, ct);
            return Results.Json(response, AppJsonContext.Default.LauncherDirectoryListResponse);
        });

        app.MapGet("/api/hub/machines/{id}/files/picker/writable", async (string id, string path, CancellationToken ct) =>
        {
            var response = await hubService.GetLauncherWritableAsync(id, path, ct);
            return Results.Json(response, AppJsonContext.Default.LauncherDirectoryAccessResponse);
        });

        app.MapPost("/api/hub/machines/{id}/files/picker/folders", async (string id, LauncherCreateDirectoryRequest request, CancellationToken ct) =>
        {
            var response = await hubService.CreateLauncherFolderAsync(id, request, ct);
            return Results.Json(response, AppJsonContext.Default.LauncherDirectoryMutationResponse);
        });

        app.MapPost("/api/hub/machines/{id}/files/picker/clone", async (string id, LauncherCloneRepositoryRequest request, CancellationToken ct) =>
        {
            var response = await hubService.CloneLauncherRepositoryAsync(id, request, ct);
            return Results.Json(response, AppJsonContext.Default.LauncherDirectoryMutationResponse);
        });

        app.MapGet("/api/hub/machines/{id}/spaces", async (string id, bool? includeWorkspaces, bool? pinnedOnly, CancellationToken ct) =>
        {
            var spaces = await hubService.GetSpacesAsync(
                id,
                includeWorkspaces ?? true,
                pinnedOnly ?? false,
                ct);
            return Results.Json(spaces, AppJsonContext.Default.ListSpaceSummaryDto);
        });

        app.MapPost("/api/hub/machines/{id}/spaces/import", async (string id, SpaceImportRequest request, CancellationToken ct) =>
        {
            var space = await hubService.ImportSpaceAsync(id, request, ct);
            return Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
        });

        app.MapPatch("/api/hub/machines/{id}/spaces/{spaceId}", async (string id, string spaceId, SpaceUpdateRequest request, CancellationToken ct) =>
        {
            var space = await hubService.UpdateSpaceAsync(id, spaceId, request, ct);
            return Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
        });

        app.MapPost("/api/hub/machines/{id}/spaces/{spaceId}/git/init", async (string id, string spaceId, CancellationToken ct) =>
        {
            var space = await hubService.InitGitSpaceAsync(id, spaceId, ct);
            return Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
        });

        app.MapPost("/api/hub/machines/{id}/spaces/{spaceId}/worktrees", async (string id, string spaceId, SpaceCreateWorktreeRequest request, CancellationToken ct) =>
        {
            var space = await hubService.CreateWorktreeAsync(id, spaceId, request, ct);
            return Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
        });

        app.MapPatch("/api/hub/machines/{id}/spaces/{spaceId}/workspaces/{key}", async (string id, string spaceId, string key, SpaceUpdateWorkspaceRequest request, CancellationToken ct) =>
        {
            var space = await hubService.UpdateWorkspaceAsync(id, spaceId, key, request, ct);
            return Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
        });

        app.MapDelete("/api/hub/machines/{id}/spaces/{spaceId}/workspaces/{key}", async (string id, string spaceId, string key, bool? force, CancellationToken ct) =>
        {
            var space = await hubService.DeleteWorktreeAsync(id, spaceId, key, force == true, ct);
            return Results.Json(space, AppJsonContext.Default.SpaceSummaryDto);
        });

        app.MapPost("/api/hub/machines/{id}/spaces/{spaceId}/workspaces/{key}/launch", async (string id, string spaceId, string key, SpaceLaunchRequest request, CancellationToken ct) =>
        {
            var session = await hubService.LaunchSpaceAsync(id, spaceId, key, request, ct);
            return Results.Json(session, AppJsonContext.Default.SessionInfoDto);
        });

        app.MapGet("/api/hub/machines/{id}/recents", async (string id, int? count, CancellationToken ct) =>
        {
            var recents = await hubService.GetRecentsAsync(id, count ?? 6, ct);
            return Results.Json(recents, AppJsonContext.Default.ListLaunchEntry);
        });

        app.MapPost("/api/hub/updates/apply", async (HubUpdateRolloutRequest request, CancellationToken ct) =>
        {
            var response = await hubService.ApplyUpdatesAsync(request, ct);
            return Results.Json(response, AppJsonContext.Default.HubUpdateRolloutResponse);
        });
    }
}
