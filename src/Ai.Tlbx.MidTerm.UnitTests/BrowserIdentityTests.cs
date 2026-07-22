using Ai.Tlbx.MidTerm.Services.Browser;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class BrowserIdentityTests
{
    [Fact]
    public void GetDeviceLabel_TrimsControlCharactersAndLength()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(
            "deviceLabel",
            new string('A', 90) + "\r\nPC");

        var label = BrowserIdentity.GetDeviceLabel(context.Request);

        Assert.NotNull(label);
        Assert.Equal(80, label.Length);
        Assert.DoesNotContain(label, char.IsControl);
    }

    [Fact]
    public void GetDeviceLabel_FallsBackToBrowserHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[BrowserIdentity.DeviceLabelHeader] = "iPad · Safari";

        var label = BrowserIdentity.GetDeviceLabel(context.Request);

        Assert.Equal("iPad · Safari", label);
    }

    [Fact]
    public void GetDeviceLabel_DecodesAsciiSafeBrowserHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[BrowserIdentity.DeviceLabelHeader] = "Windows%20PC%20%C2%B7%20Chrome";

        var label = BrowserIdentity.GetDeviceLabel(context.Request);

        Assert.Equal("Windows PC · Chrome", label);
    }
}
