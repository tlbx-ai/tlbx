using Ai.Tlbx.MidTerm.Api.Handlers;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Models.WebPreview;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ai.Tlbx.MidTerm.Api.Endpoints;

public static class BrowserEndpointDefinitions
{
    public static IEndpointRouteBuilder MapBrowserApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/browser/status", (string? sessionId, string? previewName, IBrowserHandler handler) =>
            handler.GetStatus(sessionId, previewName))
            .Produces<BrowserStatusResponse>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/browser/preview-client", (BrowserPreviewClientRequest request, HttpContext ctx, IBrowserHandler handler) =>
            handler.CreatePreviewClient(request, ctx))
            .Produces<BrowserPreviewClientResponse>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/browser/detach", (WebPreviewSessionRequest request, IBrowserHandler handler) =>
            handler.Detach(request))
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/browser/dock", (WebPreviewSessionRequest request, IBrowserHandler handler) =>
            handler.Dock(request))
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/browser/viewport", (ViewportRequest request, IBrowserHandler handler) =>
            handler.SetViewport(request))
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/browser/open", (WebPreviewTargetRequest request, IBrowserHandler handler) =>
            handler.Open(request))
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/browser/command", async (BrowserCommandRequest request, HttpContext ctx, IBrowserHandler handler) =>
            await handler.ExecuteCommandAsync(request, ctx))
            .Produces<BrowserCommandResponse>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/browser/main", (BrowserCommandRequest request, IBrowserHandler handler) =>
            handler.ClaimMain(request))
            .Produces<BrowserCommandResponse>(StatusCodes.Status200OK, "application/json");

        return app;
    }
}
