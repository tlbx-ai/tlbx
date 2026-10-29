using System.Collections.Concurrent;
using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Connect;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectRequestQueueTests
{
    [Fact]
    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient",
        Justification = "This test owns one client with a fixture-specific blocking handler to measure actual request concurrency.")]
    public async Task AssetBurstQueues128RequestsRuns16AtOnceAndRejectsOnlyTheExcessRequest()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This managed host fixture uses the Windows settings/DPAPI build.");
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-connect-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(directory);
            var auth = new AuthService(settings, new ApiKeyService(settings));
            using var handler = new BlockingHandler();
            using var http = new HttpClient(handler, disposeHandler: false);
            var replies = new ConcurrentQueue<ConnectEnvelope>();
            var receive = new ConnectFrames();
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var tunnel = new ConnectTunnel(new ServerBindingInfo(2100, "127.0.0.1"),
                new BrowserPreviewOriginService(2100, 2101, true), new BrowserPreviewRegistry(), auth, http,
                (signal, _) =>
                {
                    var bytes = receive.Receive(signal.Span);
                    if (bytes is not null)
                    {
                        replies.Enqueue(JsonSerializer.Deserialize(bytes, ConnectJsonContext.Default.ConnectEnvelope)!);
                        if (replies.Count == 129) complete.TrySetResult();
                    }
                    return Task.CompletedTask;
                }, ConnectKeySecurityTests.CreateAuthenticatedPeer(), TestContext.Current.CancellationToken);
            var send = new ConnectFrames();
            for (var i = 0; i < 129; i++)
            {
                var envelope = new ConnectEnvelope { Type = "http", Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Method = "GET", Path = "/api/version" };
                foreach (var frame in send.Encode(JsonSerializer.SerializeToUtf8Bytes(envelope, ConnectJsonContext.Default.ConnectEnvelope)))
                    await tunnel.ReceiveFrameAsync(frame);
            }
            Assert.Equal(16, handler.Active);
            Assert.Equal(429, Assert.Single(replies).Status);
            handler.Release();
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(128, replies.Count(reply => reply.Status == 200));
            Assert.Equal(1, replies.Count(reply => reply.Status == 429));
            Assert.Equal(16, handler.Peak);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _peak;
        public int Active => Volatile.Read(ref _active);
        public int Peak => Volatile.Read(ref _peak);
        public void Release() => _released.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _peak); } while (active > previous && Interlocked.CompareExchange(ref _peak, active, previous) != previous);
            try
            {
                await _released.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
