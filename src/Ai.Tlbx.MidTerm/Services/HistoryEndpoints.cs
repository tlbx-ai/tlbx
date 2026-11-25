using Ai.Tlbx.MidTerm.Models;

using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
namespace Ai.Tlbx.MidTerm.Services;

public static class HistoryEndpoints
{
    public static void MapHistoryEndpoints(WebApplication app, HistoryService historyService, TtyHostSessionManager sessionManager)
    {
        app.MapGet("/api/history", () =>
        {
            return Results.Json(historyService.GetEntries(), AppJsonContext.Default.ListLaunchEntry);
        });

        app.MapPost("/api/history", (CreateHistoryRequest request) =>
        {
            var id = historyService.RecordEntry(
                request.ShellType,
                request.Executable,
                request.CommandLine,
                request.WorkingDirectory,
                request.Label,
                request.Notes,
                request.DedupeKey,
                request.LaunchMode,
                request.Profile,
                request.LaunchOrigin,
                request.SurfaceType,
                request.ForegroundProcessName,
                request.ForegroundProcessCommandLine,
                request.ForegroundProcessDisplayName,
                request.ForegroundProcessIdentity,
                allowShellOnly: request.IsStarred);

            if (id is null)
            {
                return Results.BadRequest("Invalid history entry");
            }

            if (request.IsStarred)
            {
                historyService.SetStarred(id, true);
            }

            return Results.Json(new CreateHistoryResponse
            {
                Id = id
            }, AppJsonContext.Default.CreateHistoryResponse);
        });

        app.MapPatch("/api/history/{id}", (string id, HistoryPatchRequest request) =>
        {
            if (request.IsStarred.HasValue)
            {
                if (!historyService.SetStarred(id, request.IsStarred.Value))
                {
                    return Results.NotFound();
                }

                if (!request.IsStarred.Value)
                {
                    sessionManager.ClearBookmarksByHistoryId(id);
                }
            }
            if (request.Label is not null)
            {
                if (!historyService.SetLabel(id, request.Label))
                {
                    return Results.NotFound();
                }
            }
            if (request.Notes is not null)
            {
                if (!historyService.SetNotes(id, request.Notes))
                {
                    return Results.NotFound();
                }
            }
            return Results.Ok();
        });

        // Legacy PUT star endpoint kept for backward compatibility
        app.MapPut("/api/history/{id}/star", (string id) =>
        {
            if (historyService.ToggleStar(id))
            {
                var entry = historyService.GetEntry(id);
                if (entry is not null && !entry.IsStarred)
                {
                    sessionManager.ClearBookmarksByHistoryId(id);
                }
                return Results.Ok();
            }
            return Results.NotFound();
        });

        app.MapDelete("/api/history/{id}", (string id) =>
        {
            if (historyService.RemoveEntry(id))
            {
                sessionManager.ClearBookmarksByHistoryId(id);
                return Results.Ok();
            }
            return Results.NotFound();
        });

        app.MapPost("/api/history/reorder", (HistoryReorderRequest request) =>
        {
            historyService.ReorderStarred(request.OrderedIds);
            return Results.Ok();
        });

    }
}
