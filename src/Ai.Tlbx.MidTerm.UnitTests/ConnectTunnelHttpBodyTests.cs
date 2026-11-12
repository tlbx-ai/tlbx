using System.IO.Compression;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Connect;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectTunnelHttpBodyTests
{
    [Fact]
    public async Task FramedMultipartUploadPreservesBoundaryAndEveryBinaryByte()
    {
        var file = new byte[2 * 1024 * 1024 + 31];
        for (var i = 0; i < file.Length; i++) file[i] = (byte)(i % 256);
        const string boundary = "----tlbx-upload-browser-boundary";
        using var multipart = new MultipartFormDataContent(boundary);
        using var fileContent = new ByteArrayContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(fileContent, "file", "upload.bin");
        var body = await multipart.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var contentType = multipart.Headers.ContentType!.ToString();
        var response = new byte[180_000];
        for (var i = 0; i < response.Length; i++) response[i] = (byte)(255 - i % 256);
        using var handler = new CaptureHandler(response);
        var result = await RoundTripAsync(handler, new ConnectEnvelope
        {
            Type = "http", Id = "multipart-upload", Method = "POST", Path = "/api/sessions/12345678/upload?source=upload",
            Headers = new(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = contentType },
            Body = Convert.ToBase64String(body)
        });
        Assert.Equal(200, result.Status);
        Assert.Equal(contentType, handler.ContentType);
        Assert.Equal(body, handler.Body);
        Assert.Equal(response, Convert.FromBase64String(result.Body!));
        using var received = new MemoryStream(handler.Body!);
        var section = await new MultipartReader(boundary, received).ReadNextSectionAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(section);
        using var uploaded = new MemoryStream();
        await section.Body.CopyToAsync(uploaded, TestContext.Current.CancellationToken);
        Assert.Equal(file, uploaded.ToArray());
        Assert.Equal("application/octet-stream", section.ContentType);
    }

    [Fact]
    public async Task HttpTunnelPreservesBinaryRequestAndResponseWithoutTextDecoding()
    {
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 255, 128, 0, 195, 40, 10];
        using var handler = new CaptureHandler(png);
        var result = await RoundTripAsync(handler, new ConnectEnvelope
        {
            Type = "http", Id = "png-upload", Method = "POST", Path = "/api/sessions/12345678/upload",
            Headers = new(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "image/png" },
            Body = Convert.ToBase64String(png)
        });
        Assert.Equal(200, result.Status);
        Assert.Equal(png, handler.Body);
        Assert.Equal(png, Convert.FromBase64String(result.Body!));
        Assert.Equal("image/png", handler.ContentType);
    }

    [Fact]
    public async Task UploadBeyondDecodedBodyLimitDoesNotReachLoopbackHttp()
    {
        using var handler = new CaptureHandler([]);
        var result = await RoundTripAsync(handler, new ConnectEnvelope
        {
            Type = "http", Id = "oversized-upload", Method = "POST", Path = "/api/sessions/12345678/upload",
            Body = Convert.ToBase64String(new byte[20 * 1024 * 1024 + 1])
        });
        Assert.Equal(502, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task InvalidBase64UploadDoesNotReachLoopbackHttp()
    {
        using var handler = new CaptureHandler([]);
        var result = await RoundTripAsync(handler, new ConnectEnvelope
        {
            Type = "http", Id = "invalid-upload", Method = "POST", Path = "/api/sessions/12345678/upload",
            Body = "this is not base64"
        });
        Assert.Equal(502, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ResponseBeyondDecodedBodyLimitIsRejected()
    {
        using var handler = new CaptureHandler(new byte[20 * 1024 * 1024 + 1]);
        var result = await RoundTripAsync(handler, new ConnectEnvelope
        {
            Type = "http", Id = "oversized-response", Method = "GET", Path = "/api/version"
        });
        Assert.Equal(502, result.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NegotiatedCompressionPreservesAllDecodedBytes(bool knownLength)
    {
        var response = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("const text = 'ä binary';\n", 20000)));
        using var handler = new CaptureHandler(response, knownLength);
        var result = await RoundTripAsync(handler, new ConnectEnvelope
        {
            Type = "http", Id = "gzip-response", Method = "GET", Path = "/js/terminal.min.js", AcceptBodyEncoding = "gzip"
        });
        Assert.Equal(200, result.Status);
        Assert.Equal("gzip", result.BodyEncoding);
        var compressed = Convert.FromBase64String(result.Body!);
        Assert.True(compressed.Length < response.Length / 4);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        await gzip.CopyToAsync(decoded, TestContext.Current.CancellationToken);
        Assert.Equal(response, decoded.ToArray());
    }

    [Fact]
    public async Task CompressedUnknownLengthResponseStillEnforcesOriginalBodyLimit()
    {
        using var handler = new CaptureHandler(new byte[20 * 1024 * 1024 + 1], knownLength: false);
        var result = await RoundTripAsync(handler, new ConnectEnvelope
        {
            Type = "http", Id = "gzip-too-large", Method = "GET", Path = "/api/version", AcceptBodyEncoding = "gzip"
        });
        Assert.Equal(502, result.Status);
        Assert.Null(result.BodyEncoding);
    }

    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient", Justification = "Each protocol fixture owns one isolated capturing handler and client.")]
    private static async Task<ConnectEnvelope> RoundTripAsync(CaptureHandler handler, ConnectEnvelope request)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This host fixture uses the Windows settings/DPAPI build.");
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-connect-http-body-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(directory);
            var auth = new AuthService(settings, new ApiKeyService(settings));
            using var http = new HttpClient(handler, disposeHandler: false);
            var result = new TaskCompletionSource<ConnectEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new ConnectFrames();
            await using var tunnel = new ConnectTunnel(new ServerBindingInfo(2100, "127.0.0.1"),
                new BrowserPreviewOriginService(2100), new BrowserPreviewRegistry(), auth, http,
                (signal, _) =>
                {
                    var bytes = received.Receive(signal.Span);
                    if (bytes is not null) result.TrySetResult(JsonSerializer.Deserialize(bytes, ConnectJsonContext.Default.ConnectEnvelope)!);
                    return Task.CompletedTask;
                }, ConnectKeySecurityTests.CreateAuthenticatedPeer(), TestContext.Current.CancellationToken);
            var frames = new ConnectFrames();
            foreach (var frame in frames.Encode(JsonSerializer.SerializeToUtf8Bytes(request, ConnectJsonContext.Default.ConnectEnvelope)))
                await tunnel.ReceiveFrameAsync(frame);
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class NonSeekableMemoryStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class CaptureHandler(byte[] response, bool knownLength = true) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public byte[]? Body { get; private set; }
        public string? ContentType { get; private set; }
        [SuppressMessage("Usage", "IDISP004:Don't ignore created IDisposable", Justification = "StreamContent owns the non-seekable stream and transfers disposal to the returned HttpResponseMessage.")]
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            ContentType = request.Content?.Headers.ContentType?.ToString();
            HttpContent content = knownLength ? new ByteArrayContent(response) : new StreamContent(new NonSeekableMemoryStream(response));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/javascript");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
