using Ai.Tlbx.MidTerm.Services.StaticFiles;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class EmbeddedWebRootFileProviderTests
{
    [Fact]
    public void GetFileInfo_ReturnsEmbeddedMobileDeviceBridgeArchive()
    {
        var assembly = typeof(EmbeddedWebRootFileProvider).Assembly;
        var provider = new EmbeddedWebRootFileProvider(assembly, "Ai.Tlbx.MidTerm");

        var file = provider.GetFileInfo("midterm-mobile-device-bridge.zip");

        Assert.True(file.Exists);
        Assert.True(file.Length > 0);
    }

    [Theory]
    [InlineData("midterm-mobile-device-bridge.zip", "midterm-mobile-device-bridge.zip")]
    [InlineData("js.terminal.min.js.br", "js/terminal.min.js.br")]
    [InlineData("fonts.CascadiaCode-Regular.woff2", "fonts/CascadiaCode-Regular.woff2")]
    public void ConvertResourceNameToPath_PreservesKnownFileExtensions(
        string resourceName,
        string expectedPath
    )
    {
        Assert.Equal(expectedPath, EmbeddedWebRootFileProvider.ConvertResourceNameToPath(resourceName));
    }
}
