using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Ai.Tlbx.MidTerm.AgentHost;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.UnitTests;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class McpHostScopeIpcTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedHandshakeIsFlushedBeforeConnectionCloses(bool wrongOwner)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var name = "tlbx-mcp-reject-" + Guid.NewGuid().ToString("N");
        var endpoint = OperatingSystem.IsWindows() ? name : Path.Combine(Path.GetTempPath(), name + ".sock");
        var server = new AppServerControlAgentHostServer("codex", "owned-instance", "owned-token");
        var serving = server.RunIpcAsync(endpoint);
        try
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                await using var stream = await ConnectAsync(endpoint, timeout.Token);
                using var input = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var rejectionVerified = false;
                try
                {
                    using var output = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                    await AppServerControlHostTestClient.ReadHelloAsync(input);
                    var command = Attach("rejected", prepare: true);
                    if (wrongOwner) command.AttachRuntime!.OwnerToken = "wrong-owner";
                    else command.Type = "history.get";
                    await AppServerControlHostTestClient.WriteCommandAsync(output, command);
                    var result = await AppServerControlHostTestClient.ReadResultAsync(input, new(), command.CommandId);
                    Assert.Equal("rejected", result.Status);
                    Assert.Contains(wrongOwner ? "ownership" : "first command", result.Message!, StringComparison.Ordinal);
                    Assert.Null(await input.ReadLineAsync(timeout.Token));
                    rejectionVerified = true;
                }
                catch (IOException) when (rejectionVerified)
                {
                    // Only disposal after a verified response and EOF can reach
                    // this filter; earlier transport failures must fail the test.
                }
            }
        }
        finally
        {
            await server.DisposeAsync();
            await serving.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task OwnedPrepareAndLiveEpochTravelOverActualHostIpc()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var name = "tlbx-mcp-test-" + Guid.NewGuid().ToString("N");
        var endpoint = OperatingSystem.IsWindows() ? name : Path.Combine(Path.GetTempPath(), name + ".sock");
        var server = new AppServerControlAgentHostServer("codex", "owned-instance", "owned-token");
        var serving = server.RunIpcAsync(endpoint);
        try
        {
            await using (var unauthorized = await ConnectAsync(endpoint, ct))
            using (var reader = new StreamReader(unauthorized, Encoding.UTF8, leaveOpen: true))
            {
                var rejectionVerified = false;
                try
                {
                    using var writer = new StreamWriter(unauthorized, new UTF8Encoding(false), leaveOpen: true);
                    await AppServerControlHostTestClient.ReadHelloAsync(reader);
                    var bad = Attach("first", prepare: true);
                    bad.AttachRuntime!.OwnerToken = "wrong-owner";
                    await AppServerControlHostTestClient.WriteCommandAsync(writer, bad);
                    var denied = await AppServerControlHostTestClient.ReadResultAsync(reader, new(), bad.CommandId);
                    Assert.Equal("rejected", denied.Status);
                    Assert.Contains("ownership", denied.Message!, StringComparison.Ordinal);
                    rejectionVerified = true;
                }
                catch (IOException) when (rejectionVerified)
                {
                    // The server closes this rejected connection before disposal.
                }
            }

            await using var stream = await ConnectAsync(endpoint, ct);
            using var input = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var output = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(input);
            Assert.Contains("mcp.scope.prepare", hello.Capabilities, StringComparer.Ordinal);
            var patches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
            async Task<AppServerControlHostCommandResultEnvelope> Send(AppServerControlHostCommandEnvelope command)
            {
                await AppServerControlHostTestClient.WriteCommandAsync(output, command);
                return await AppServerControlHostTestClient.ReadResultAsync(input, patches, command.CommandId);
            }
            var prepared = await Send(Attach("first", prepare: true));
            Assert.Equal("accepted", prepared.Status);
            Assert.Equal(hello.McpScopeIdentity, prepared.McpScopeIdentity);
            var attached = await Send(Attach("first", prepare: false, prepared.McpScopeIdentity));
            Assert.Equal("first", attached.McpProviderThreadId);
            Assert.Equal(prepared.McpScopeIdentity, attached.McpScopeIdentity);
            var same = await Send(Attach("first", prepare: true));
            Assert.Equal(attached.McpScopeIdentity, same.McpScopeIdentity);
            // A new Web process does not know the provider thread yet. The
            // host must return its live thread without rotating its authority.
            var implicitReattach = await Send(Attach(null, prepare: true));
            Assert.Equal(attached.McpScopeIdentity, implicitReattach.McpScopeIdentity);
            Assert.Equal("first", implicitReattach.McpProviderThreadId);

            // Replacement preparation returns a new authority before any factory
            // issues its launch capability or any provider receives its config.
            var replacement = await Send(Attach("second", prepare: true));
            Assert.NotEqual(same.McpScopeIdentity, replacement.McpScopeIdentity, StringComparer.Ordinal);
            var replaced = await Send(Attach("second", prepare: false, replacement.McpScopeIdentity));
            Assert.Equal(replacement.McpScopeIdentity, replaced.McpScopeIdentity);
            Assert.Equal("second", replaced.McpProviderThreadId);
            await Send(Attach("unexpected", prepare: false, replaced.McpScopeIdentity));
            var unexpected = await AppServerControlHostTestClient.ReadUntilAsync(input, patches,
                patch => patch.Patch.Thread.ThreadId == "unexpected", maxPatches: 30);
            var latest = unexpected[^1];
            Assert.NotEqual(replaced.McpScopeIdentity, latest.McpScopeIdentity, StringComparer.Ordinal);
            Assert.False(string.IsNullOrEmpty(latest.McpScopeIdentity));
        }
        finally
        {
            await server.DisposeAsync();
            await serving.WaitAsync(ct);
        }
    }

    private static AppServerControlHostCommandEnvelope Attach(string? thread, bool prepare, string? epoch = null) => new()
    {
        Type = "runtime.attach", SessionId = "scope-ipc-session", CommandId = Guid.NewGuid().ToString("N"),
        AttachRuntime = new()
        {
            Provider = "codex", WorkingDirectory = Path.GetTempPath(), InstanceId = "owned-instance", OwnerToken = "owned-token",
            ResumeThreadId = thread, McpPrepareOnly = prepare,
            McpServer = prepare ? null : new() { ScopeIdentity = epoch }
        }
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "On success the returned NetworkStream owns the socket; every failed connect or stream construction disposes it in catch.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Correctness", "IDISP001:Dispose created", Justification = "The socket transfers to the returned owning NetworkStream; catch disposes it on every failure.")]
    private static async Task<Stream> ConnectAsync(string endpoint, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(5000, ct); return pipe; }
            catch { await pipe.DisposeAsync(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }
}
