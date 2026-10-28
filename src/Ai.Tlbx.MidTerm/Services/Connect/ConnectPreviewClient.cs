using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>A scoped preview cookie jar. It is never given the installation's full-user session cookie.</summary>
internal sealed class ConnectPreviewClient : IDisposable
{
    private readonly HttpClientHandler _handler;
    public HttpClient Http { get; }
    public CookieContainer Cookies { get; } = new();

    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient",
        Justification = "Each connection/preview route needs an isolated cookie container; the client is reused for that entire route lifetime.")]
    public ConnectPreviewClient()
    {
        _handler = ConnectTunnel.CreateLoopbackHandler();
        _handler.UseCookies = true;
        _handler.CookieContainer = Cookies;
        Http = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
    }
}
