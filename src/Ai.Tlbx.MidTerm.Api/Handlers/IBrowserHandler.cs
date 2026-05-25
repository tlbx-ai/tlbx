using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Models.WebPreview;
using Microsoft.AspNetCore.Http;

namespace Ai.Tlbx.MidTerm.Api.Handlers;

public interface IBrowserHandler
{
    IResult GetStatus(string? sessionId, string? previewName);
    IResult CreatePreviewClient(BrowserPreviewClientRequest request, HttpContext ctx);
    IResult Detach(WebPreviewSessionRequest request);
    IResult Dock(WebPreviewSessionRequest request);
    IResult SetViewport(ViewportRequest request);
    IResult Open(WebPreviewTargetRequest request);
    IResult ClaimMain(BrowserCommandRequest request);
    Task<IResult> ExecuteCommandAsync(BrowserCommandRequest request, HttpContext ctx);
}
