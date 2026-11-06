namespace Ai.Tlbx.MidTerm.Services.Browser;

/// <summary>Preview capabilities share the configured listener; opaque documents provide browser isolation.</summary>
public sealed class BrowserPreviewOriginService(int mainPort)
{
    public int MainPort { get; } = mainPort;
    public int PreviewPort => MainPort;
    public bool IsEnabled => true;

    public static BrowserPreviewOriginService Create(int mainPort) => new(mainPort);

    public string? GetOrigin(HttpRequest request) => request.Host.HasValue
        ? $"{request.Scheme}://{request.Host}"
        : null;
}
