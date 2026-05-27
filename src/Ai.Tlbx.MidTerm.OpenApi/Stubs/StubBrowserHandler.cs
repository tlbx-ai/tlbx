using Ai.Tlbx.MidTerm.Api.Handlers;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Models.WebPreview;
using Microsoft.AspNetCore.Http;

namespace Ai.Tlbx.MidTerm.OpenApi.Stubs;

public sealed class StubBrowserHandler : IBrowserHandler
{
    public IResult GetStatus(string? sessionId, string? previewName) =>
        Results.Json(new BrowserStatusResponse());

    public IResult CreatePreviewClient(BrowserPreviewClientRequest request, HttpContext ctx) =>
        Results.Json(new BrowserPreviewClientResponse
        {
            PreviewId = "preview",
            PreviewToken = "token",
            RouteKey = "route"
        });

    public IResult Detach(WebPreviewSessionRequest request) =>
        Results.Ok();

    public IResult Dock(WebPreviewSessionRequest request) =>
        Results.Ok();

    public IResult SetViewport(ViewportRequest request) =>
        Results.Ok();

    public IResult Open(WebPreviewTargetRequest request) =>
        Results.Ok();

    public IResult ClaimMain(BrowserCommandRequest request) =>
        Results.Json(new BrowserCommandResponse { Success = true });

    public Task<IResult> ExecuteCommandAsync(BrowserCommandRequest request, HttpContext ctx) =>
        Task.FromResult<IResult>(Results.Json(new BrowserCommandResponse { Success = true }));
}
