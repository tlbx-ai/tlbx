using Ai.Tlbx.MidTerm.Services.Browser;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class BrowserPreviewOriginServiceTests
{
    [Fact]
    public void GetOrigin_UsesRequestHostAndPreviewPort()
    {
        var service = new BrowserPreviewOriginService(mainPort: 2000, previewPort: 2001, isEnabled: true);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("midterm.test", 2000);

        var origin = service.GetOrigin(context.Request);

        Assert.Equal("https://midterm.test:2001", origin);
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/api/auth/status", true)]
    [InlineData("/ws/state", true)]
    [InlineData("/ws/browser", false)]
    [InlineData("/webpreview/", false)]
    [InlineData("/some/upstream/path", false)]
    public void ShouldBlockPath_OnlyBlocksMidTermAppRoutes(string path, bool expected)
    {
        var service = new BrowserPreviewOriginService(mainPort: 2000, previewPort: 2001, isEnabled: true);

        var result = service.ShouldBlockPath(path);

        Assert.Equal(expected, result);
    }
}
