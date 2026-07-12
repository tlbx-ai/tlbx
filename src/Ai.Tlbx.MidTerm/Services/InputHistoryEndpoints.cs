using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.InputHistory;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Sessions;

namespace Ai.Tlbx.MidTerm.Services;

public static class InputHistoryEndpoints
{
    public static void MapInputHistoryEndpoints(
        WebApplication app,
        InputHistoryService inputHistory,
        TtyHostSessionManager sessionManager,
        SessionTelemetryService sessionTelemetry,
        ManagerBarQueueService commandBayQueue)
    {
        app.MapGet("/api/input-history", (string? sessionId, string? kind, int limit = 100) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest("Session id is required.");
            }

            var response = inputHistory.GetEntries(sessionId, kind, limit);
            return Results.Json(response, AppJsonContext.Default.InputHistoryListResponse);
        });

        app.MapPost("/api/sessions/{sessionId}/input-history", (
            string sessionId,
            TerminalInputHistoryRequest request) =>
        {
            var session = sessionManager.GetSession(sessionId);
            if (session is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest("Terminal input history requires text.");
            }

            var entry = inputHistory.RecordPrompt(
                sessionId,
                session.Name,
                session.CurrentDirectory,
                InputHistorySources.TerminalInput,
                InputHistorySurfaces.Terminal,
                new AppServerControlTurnRequest { Text = request.Text });
            return Results.Json(entry, AppJsonContext.Default.InputHistoryEntry);
        });

        app.MapGet("/api/input-history/{id}", (string id) =>
        {
            var entry = inputHistory.GetEntry(id);
            return entry is null
                ? Results.NotFound()
                : Results.Json(entry, AppJsonContext.Default.InputHistoryEntry);
        });

        app.MapGet("/api/input-history/{id}/content", (string id) =>
        {
            var entry = inputHistory.GetEntry(id);
            if (entry is null ||
                !string.Equals(entry.Kind, InputHistoryKinds.ImagePaste, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.Path))
            {
                return Results.NotFound();
            }

            return File.Exists(entry.Path)
                ? Results.File(
                    entry.Path,
                    contentType: entry.MimeType ?? "application/octet-stream",
                    enableRangeProcessing: true)
                : Results.NotFound();
        });

        app.MapDelete("/api/input-history/{id}", (string id) =>
        {
            return inputHistory.Remove(id) ? Results.Ok() : Results.NotFound();
        });

        app.MapDelete("/api/input-history", (string sessionId) =>
        {
            return Results.Json(
                new InputHistoryClearResponse
                {
                    SessionId = sessionId,
                    RemovedCount = inputHistory.ClearSession(sessionId)
                },
                AppJsonContext.Default.InputHistoryClearResponse);
        });

        app.MapPost("/api/input-history/{id}/replay", async (
            string id,
            InputHistoryReplayRequest? request,
            CancellationToken ct) =>
        {
            var entry = inputHistory.GetEntry(id);
            if (entry is null)
            {
                return Results.NotFound();
            }

            var targetSessionId = string.IsNullOrWhiteSpace(request?.TargetSessionId)
                ? entry.SessionId
                : request.TargetSessionId.Trim();
            var targetSession = sessionManager.GetSession(targetSessionId);
            if (targetSession is null)
            {
                return Results.NotFound($"Target session '{targetSessionId}' was not found.");
            }

            var queued = false;
            if (entry.Submit)
            {
                var turn = InputHistoryService.CloneTurn(entry.Turn)
                    ?? (string.IsNullOrEmpty(entry.Text)
                        ? null
                        : new AppServerControlTurnRequest { Text = entry.Text });
                if (turn is null)
                {
                    return Results.BadRequest("History entry has no replayable prompt payload.");
                }

                var result = await commandBayQueue.SubmitPromptAsync(targetSessionId, turn, ct)
                    .ConfigureAwait(false);
                if (!result.Accepted)
                {
                    return Results.Conflict("Target session did not accept the prompt.");
                }

                queued = result.Entry is not null;
                inputHistory.RecordPrompt(
                    targetSessionId,
                    targetSession.Name,
                    targetSession.CurrentDirectory,
                    InputHistorySources.HistoryReplay,
                    entry.Surface,
                    turn);
            }
            else
            {
                var replayText = entry.Text ?? entry.Path;
                if (string.IsNullOrEmpty(replayText))
                {
                    return Results.BadRequest("History entry has no replayable paste payload.");
                }

                var sent = await SessionApiEndpoints.SendHistoryPasteAsync(
                    sessionManager,
                    sessionTelemetry,
                    targetSessionId,
                    replayText,
                    entry.BracketedPaste,
                    entry.IsFilePath,
                    ct).ConfigureAwait(false);
                if (!sent)
                {
                    return Results.BadRequest("History entry could not be replayed.");
                }

                if ((entry.Kind is InputHistoryKinds.ImagePaste or InputHistoryKinds.FileUpload) && entry.Path is not null)
                {
                    inputHistory.RecordUpload(
                        targetSessionId,
                        targetSession.Name,
                        targetSession.CurrentDirectory,
                        entry.Path,
                        entry.DisplayName,
                        entry.MimeType,
                        entry.SizeBytes ?? 0,
                        InputHistorySources.HistoryReplay);
                }
                else
                {
                    inputHistory.RecordPaste(
                        targetSessionId,
                        targetSession.Name,
                        targetSession.CurrentDirectory,
                        replayText,
                        entry.BracketedPaste,
                        entry.IsFilePath,
                        InputHistorySources.HistoryReplay);
                }
            }

            return Results.Json(
                new InputHistoryReplayResponse
                {
                    EntryId = id,
                    TargetSessionId = targetSessionId,
                    Accepted = true,
                    Queued = queued
                },
                AppJsonContext.Default.InputHistoryReplayResponse);
        });
    }
}
