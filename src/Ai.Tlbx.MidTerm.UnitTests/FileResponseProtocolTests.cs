using Ai.Tlbx.MidTerm.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class FileResponseProtocolTests
{
    [Theory]
    [InlineData("page.html", true, true)]
    [InlineData("page.xhtml", true, true)]
    [InlineData("image.svg", true, true)]
    [InlineData("document.xml", true, true)]
    [InlineData("transform.xsl", true, true)]
    [InlineData("page.html", false, false)]
    [InlineData("image.svg", false, false)]
    [InlineData("document.pdf", true, false)]
    [InlineData("image.png", true, false)]
    [InlineData("document.txt", true, false)]
    public async Task FileResponseIsolatesActiveInlineDocumentsAndPreservesBytes(
        string fileName, bool inline, bool sandbox)
    {
        var path = Path.GetTempFileName();
        byte[] bytes = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        try
        {
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = "GET";
            context.Response.Body = new MemoryStream();
            const string applicationPolicy = "default-src 'self'";
            context.Response.Headers.ContentSecurityPolicy = applicationPolicy;
            await FileEndpoints.CreateFileResponse(context, path, fileName, inline).ExecuteAsync(context);

            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal(bytes, ((MemoryStream)context.Response.Body).ToArray());
            Assert.Equal(sandbox ? "sandbox" : applicationPolicy,
                context.Response.Headers.ContentSecurityPolicy.ToString());
            if (sandbox) Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
            Assert.Equal(!inline, context.Response.Headers.ContentDisposition.ToString().Contains("attachment", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }
}
