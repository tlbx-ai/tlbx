using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Connect;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectSignalingRecoveryTests
{
    [Fact]
    public async Task TransientSignalingFailuresRetryAutomaticallyAndDisableInterruptsBackoff()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This host fixture uses the Windows settings/DPAPI build.");
        var ct = TestContext.Current.CancellationToken;
        var attempts = Channel.CreateUnbounded<long>();
        var requests = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapPost("/api/keys/challenge", async context =>
        {
            Interlocked.Increment(ref requests);
            await attempts.Writer.WriteAsync(Stopwatch.GetTimestamp(), context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        });
        await app.StartAsync(ct);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        var origin = Assert.Single(addresses.Addresses);
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-connect-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(directory);
            var (configuration, _) = ConnectKeyProtocol.Generate(origin, "recovery-fixture");
            settings.SecretStorage.SetSecret("connect.keys.v1",
                JsonSerializer.Serialize(configuration, ConnectJsonContext.Default.ConnectDeviceConfiguration));
            var auth = new AuthService(settings, new ApiKeyService(settings));
            using var service = new ConnectService(settings, auth, new ServerBindingInfo(2100, "127.0.0.1"),
                new BrowserPreviewOriginService(2100), new BrowserPreviewRegistry());
            await service.StartAsync(ct);
            try
            {
                var first = await attempts.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct);
                var second = await attempts.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct);
                var third = await attempts.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct);
                Assert.InRange(Stopwatch.GetElapsedTime(first, third), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(4));
                Assert.True(Stopwatch.GetElapsedTime(first, second) >= TimeSpan.FromMilliseconds(100), "A failed portal must not trigger a busy authentication loop.");
                Assert.False(service.GetStatus().Online);
                // Let the failed response enter its next backoff, then exercise
                // the public disable operation rather than stopping the fixture.
                await Task.Delay(50, ct);
                var disable = Stopwatch.StartNew();
                await service.DisconnectAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(750), ct);
                Assert.True(disable.Elapsed < TimeSpan.FromMilliseconds(750));
                Assert.False(service.GetStatus().Configured);
                var disabledRequests = Volatile.Read(ref requests);
                await Task.Delay(350, ct);
                Assert.Equal(disabledRequests, Volatile.Read(ref requests));
            }
            finally { await service.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentReceiverKeepsPongsFlowingAndBoundsPendingSignals(bool overflow)
    {
        var ct = TestContext.Current.CancellationToken;
        const int startupSignalsPerPeer = 128 + 2;
        const int concurrentPeerStartupSignals = 8 * startupSignalsPerPeer;
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/signal", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                var count = overflow ? ConnectService.MaximumPendingSignals + 1 : concurrentPeerStartupSignals;
                for (var i = 0; i < count; i++)
                {
                    var position = i % startupSignalsPerPeer;
                    var signal = new ConnectSignal
                    {
                        Type = position == 0 ? "connect" : position == startupSignalsPerPeer - 1 ? "offer" : "candidate",
                        ConnectionId = "peer-" + (i / startupSignalsPerPeer).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Candidate = position > 0 && position < startupSignalsPerPeer - 1 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                        Sdp = position == startupSignalsPerPeer - 1 ? "fixture offer" : null
                    };
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(signal, ConnectJsonContext.Default.ConnectSignal),
                        WebSocketMessageType.Text, true, ct);
                }
                sent.TrySetResult();
                // ReceiveAsync must stay pending to answer the client's PINGs.
                await socket.ReceiveAsync(new byte[64], context.RequestAborted);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { sent.TrySetResult(); }
        });
        await app.StartAsync(ct);
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var endpoint = new UriBuilder(address) { Scheme = "ws", Path = "/signal" }.Uri;
        using var client = new ClientWebSocket();
        client.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(100);
        client.Options.KeepAliveTimeout = TimeSpan.FromMilliseconds(500);
        await client.ConnectAsync(endpoint, ct);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var messages = Channel.CreateBounded<ConnectSignal>(new BoundedChannelOptions(ConnectService.MaximumPendingSignals)
        {
            SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false
        });
        var receive = ConnectService.ReceiveSignalsAsync(client, messages.Writer, lifetime);
        try
        {
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            if (overflow)
            {
                await receive.WaitAsync(TimeSpan.FromSeconds(3), ct);
                Assert.True(lifetime.IsCancellationRequested);
                Assert.Equal(ConnectService.MaximumPendingSignals, messages.Reader.Count);
                await Assert.ThrowsAsync<IOException>(async () =>
                {
                    await foreach (var message in messages.Reader.ReadAllAsync(ct)) { }
                });
            }
            else
            {
                // Emulate native peer startup occupying the application consumer
                // beyond multiple PING/PONG deadlines. The reader must stay live.
                await Task.Delay(1750, ct);
                Assert.False(lifetime.IsCancellationRequested);
                Assert.Equal(WebSocketState.Open, client.State);
                Assert.Equal(concurrentPeerStartupSignals, messages.Reader.Count);
                for (var i = 0; i < concurrentPeerStartupSignals; i++)
                {
                    var signal = await messages.Reader.ReadAsync(ct);
                    var position = i % startupSignalsPerPeer;
                    Assert.Equal(position == 0 ? "connect" : position == startupSignalsPerPeer - 1 ? "offer" : "candidate", signal.Type);
                    Assert.Equal("peer-" + (i / startupSignalsPerPeer).ToString(System.Globalization.CultureInfo.InvariantCulture), signal.ConnectionId);
                    if (signal.Type == "candidate") Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), signal.Candidate);
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            client.Abort();
            await receive.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        }
    }

}
