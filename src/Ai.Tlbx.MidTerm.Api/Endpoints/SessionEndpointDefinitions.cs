using Ai.Tlbx.MidTerm.Api.Handlers;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
namespace Ai.Tlbx.MidTerm.Api.Endpoints;

public static class SessionEndpointDefinitions
{
    public static IEndpointRouteBuilder MapSessionApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/state", (ISessionHandler handler) =>
            handler.GetState())
            .Produces<StateUpdate>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/sessions", (ISessionHandler handler) =>
            handler.GetSessions())
            .Produces<SessionListDto>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/sessions/attention", (ISessionHandler handler, bool agentOnly = true) =>
            handler.GetSessionAttention(agentOnly))
            .Produces<SessionAttentionResponse>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/sessions", async (CreateSessionRequest? request, ISessionHandler handler) =>
            await handler.CreateSessionAsync(request))
            .Produces<SessionInfoDto>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/workers/bootstrap", async (WorkerBootstrapRequest request, ISessionHandler handler) =>
            await handler.BootstrapWorkerAsync(request))
            .Produces<WorkerBootstrapResponse>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/sessions/reorder", (SessionReorderRequest request, ISessionHandler handler) =>
            handler.ReorderSessions(request))
            .Produces(StatusCodes.Status200OK);

        app.MapDelete("/api/sessions/{id}", async (string id, ISessionHandler handler) =>
            await handler.DeleteSessionAsync(id))
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/sessions/{id}/resize", async (string id, ResizeRequest request, ISessionHandler handler) =>
            await handler.ResizeSessionAsync(id, request))
            .Produces<ResizeResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/sessions/{id}/state", async (string id, ISessionHandler handler, bool includeBuffer = true, bool includeBufferBase64 = false) =>
            await handler.GetSessionStateAsync(id, includeBuffer, includeBufferBase64))
            .Produces<SessionStateResponse>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/sessions/{id}/input", async (string id, byte[] body, ISessionHandler handler) =>
            await handler.SendRawInputAsync(id, body))
            .Accepts<byte[]>("application/octet-stream")
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/sessions/{id}/input/text", async (string id, SessionInputRequest request, ISessionHandler handler) =>
            await handler.SendTextInputAsync(id, request))
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/sessions/{id}/input/keys", async (string id, SessionKeyInputRequest request, ISessionHandler handler) =>
            await handler.SendKeyInputAsync(id, request))
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/sessions/{id}/input/prompt", async (string id, SessionPromptRequest request, ISessionHandler handler) =>
            await handler.SendPromptInputAsync(id, request))
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/api/sessions/{id}/buffer", async (string id, ISessionHandler handler) =>
            await handler.GetBufferAsync(id))
            .Produces<byte[]>(StatusCodes.Status200OK, "application/octet-stream");

        app.MapGet("/api/sessions/{id}/buffer/text", async (string id, ISessionHandler handler, bool includeBase64 = false) =>
            await handler.GetBufferTextAsync(id, includeBase64))
            .Produces<SessionBufferTextResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/sessions/{id}/buffer/tail", async (string id, ISessionHandler handler, int lines = 120, bool stripAnsi = true) =>
            await handler.GetBufferTailAsync(id, lines, stripAnsi))
            .Produces<string>(StatusCodes.Status200OK, "text/plain");

        app.MapGet("/api/sessions/{id}/activity", async (string id, ISessionHandler handler, int seconds = 120, int bellLimit = 25) =>
            await handler.GetActivityAsync(id, seconds, bellLimit))
            .Produces<SessionActivityResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/sessions/{id}/agent", async (string id, ISessionHandler handler, int tailLines = 80, int activitySeconds = 90, int bellLimit = 8) =>
            await handler.GetAgentVibeAsync(id, tailLines, activitySeconds, bellLimit))
            .Produces<AgentSessionVibeResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/sessions/{id}/agent/feed", async (string id, ISessionHandler handler, int tailLines = 80, int activitySeconds = 90, int bellLimit = 8) =>
            await handler.GetAgentFeedAsync(id, tailLines, activitySeconds, bellLimit))
            .Produces<AgentSessionFeedResponse>(StatusCodes.Status200OK, "application/json");

        app.MapPut("/api/sessions/{id}/name", async (string id, RenameSessionRequest request, ISessionHandler handler, bool auto = false) =>
            await handler.RenameSessionAsync(id, request, auto))
            .Produces(StatusCodes.Status200OK);

        app.MapPut("/api/sessions/{id}/notes", async (string id, SetSessionNotesRequest request, ISessionHandler handler) =>
            await handler.SetSessionNotesAsync(id, request))
            .Produces<SessionInfoDto>(StatusCodes.Status200OK, "application/json");

        app.MapPut("/api/sessions/{id}/topic", async (string id, SetSessionTopicRequest request, ISessionHandler handler) =>
            await handler.SetSessionTopicAsync(id, request))
            .Produces<SessionInfoDto>(StatusCodes.Status200OK, "application/json");

        app.MapPut("/api/sessions/{id}/control", async (string id, SetSessionControlRequest request, ISessionHandler handler) =>
            await handler.SetSessionControlAsync(id, request))
            .Produces<SessionInfoDto>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/sessions/{id}/upload", async (string id, IFormFile file, ISessionHandler handler) =>
            await handler.UploadFileAsync(id, file))
            .Produces<FileUploadResponse>(StatusCodes.Status200OK, "application/json")
            .DisableAntiforgery();

        app.MapPost("/api/sessions/{id}/paste-clipboard-image", async (string id, IFormFile file, ISessionHandler handler) =>
            await handler.PasteClipboardImageAsync(id, file))
            .Produces(StatusCodes.Status200OK)
            .DisableAntiforgery();

        app.MapPost("/api/sessions/{id}/inject-guidance", (string id, ISessionHandler handler) =>
            handler.InjectGuidance(id))
            .Produces<InjectGuidanceResponse>(StatusCodes.Status200OK, "application/json");

        app.MapPut("/api/sessions/{id}/bookmark", (string id, SetBookmarkRequest request, ISessionHandler handler) =>
            handler.SetBookmark(id, request))
            .Produces(StatusCodes.Status200OK);

        return app;
    }
}
