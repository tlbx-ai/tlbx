using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpUpdateHttpRecoveryTests
{
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "IDISP001:Dispose created",
        Justification = "The replacement server requires the first server's dynamic port; its tracked lifetime is disposed in finally even when shutdown fails.")]
    public async Task RealHttpClientRecoversAcrossLostApplyResponseAndReplacementServerWithoutMutationReplay()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var ct = timeout.Token;
        var records = new ConcurrentQueue<string>();
        var artifact = Path.Combine(Path.GetTempPath(), "tlbx-update-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifact);
        var log = Path.Combine(artifact, "recovery.log");
        var state = new RestartState(records);
        using var logger = new RecoveryLogProvider(records);
        await using var first = BuildServer(0, "original", "1", abortApply: true, state, logger);
        WebApplication? replacement = null;
        Task? restarting = null;
        try
        {
            await first.StartAsync(ct);
            var address = first.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var port = new Uri(address).Port;
            replacement = BuildServer(port, "replacement", "2", abortApply: false, state, logger);
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(address + "/api/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
                MaxReconnectionAttempts = 0
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            restarting = RestartAsync();
            using var arguments = JsonDocument.Parse("""{"timeoutSeconds":30}""");
            using var properties = arguments.RootElement.EnumerateObject();
            var result = await TlbxMcpUpdateWorkflow.InvokeAsync(new CallToolRequestParams
            {
                Name = "update_apply", Arguments = properties.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal)
            }, (name, values, token) => TlbxMcpNativeWorkflow.CallUpstreamAsync(client, name, values, token), ct, TimeSpan.FromMilliseconds(50));
            await restarting.WaitAsync(ct);
            records.Enqueue("workflow-result " + result.StructuredContent?.GetRawText());
            Assert.False(result.IsError, "Recovery failed; see " + log + ": " + result.StructuredContent?.GetRawText());
            var outcome = result.StructuredContent!.Value;
            Assert.Equal("updated", outcome.GetProperty("outcome").GetString());
            Assert.False(outcome.GetProperty("acknowledged").GetBoolean());
            Assert.True(outcome.GetProperty("restartVerified").GetBoolean());
            Assert.True(outcome.GetProperty("expectedVersionVerified").GetBoolean());
            Assert.Equal("replacement", outcome.GetProperty("serverInstanceId").GetString());
            Assert.Equal("2", outcome.GetProperty("serverVersion").GetString());
            Assert.Equal(1, state.ApplyCount);
            Assert.True(state.ReplacementReads >= 2, "Both replacement context and status must be read.");

            async Task RestartAsync()
            {
                await state.Dispatched.Task.WaitAsync(ct);
                records.Enqueue("stopping-original");
                await first.StopAsync(ct);
                // Keep the endpoint unavailable briefly so the same HTTP MCP client must recover.
                await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
                await replacement.StartAsync(ct);
                records.Enqueue("replacement-started " + address);
            }
        }
        finally
        {
            await timeout.CancelAsync();
            if (restarting is not null)
            {
                try { await restarting; }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
                { records.Enqueue("restart-cleanup " + ex); }
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (replacement is not null)
                {
                    try { await replacement.StopAsync(cleanup.Token); }
                    finally { await replacement.DisposeAsync(); }
                }
                await first.StopAsync(cleanup.Token);
            }
            finally
            {
                await File.WriteAllLinesAsync(log, records, CancellationToken.None);
                TestContext.Current.TestOutputHelper?.WriteLine("HTTP update recovery log: " + log);
            }
        }
    }

    private static WebApplication BuildServer(int port, string instance, string version, bool abortApply, RestartState state, ILoggerProvider logger)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logger);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(new[] { "context_get", "update_status", "update_apply" }
                .Select(name => new RecoveryTool(name, instance, version, abortApply, state)));
        var app = builder.Build();
        app.MapMcp("/api/mcp");
        return app;
    }

    private sealed class RestartState(ConcurrentQueue<string> records)
    {
        internal readonly ConcurrentQueue<string> Records = records;
        internal readonly TaskCompletionSource Dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ApplyCount;
        internal int ReplacementReads;
    }

    private sealed class RecoveryTool(string name, string instance, string version, bool abortApply, RestartState state) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = name,
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object" }, McpJsonContext.Default.JsonObject)
        };
        public override IReadOnlyList<object> Metadata => [];
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            state.Records.Enqueue(instance + " " + name);
            if (instance == "replacement" && name != "update_apply") Interlocked.Increment(ref state.ReplacementReads);
            if (name == "context_get")
                return ValueTask.FromResult(McpApiResult.Structured(new JsonObject { ["serverInstanceId"] = instance, ["serverVersion"] = version }));
            if (name == "update_status")
                return ValueTask.FromResult(McpApiResult.Structured(new JsonObject
                {
                    ["statusCode"] = 200, ["data"] = new JsonObject { ["available"] = true, ["latestVersion"] = "2" }
                }));
            Interlocked.Increment(ref state.ApplyCount);
            Assert.True(request.Params!.Arguments!["detached"].GetBoolean());
            if (abortApply)
            {
                request.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext!.Abort();
                state.Dispatched.TrySetResult();
            }
            return ValueTask.FromResult(McpApiResult.Structured(new JsonObject { ["statusCode"] = 202 }));
        }
    }

    private sealed class RecoveryLogProvider(ConcurrentQueue<string> records) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecoveryLogger(categoryName, records);
        public void Dispose() { }
    }

    private sealed class RecoveryLogger(string category, ConcurrentQueue<string> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => records.Enqueue(category + " " + logLevel + " " + formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
