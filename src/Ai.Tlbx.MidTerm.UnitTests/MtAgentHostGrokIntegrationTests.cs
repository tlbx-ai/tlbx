using System.Diagnostics;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class MtAgentHostGrokIntegrationTests
{
    [Fact]
    public async Task MtAgentHost_CanDriveFakeGrokAcpTurn()
    {
        using var fakeGrok = FakeGrokPathScope.Create();
        var hostDll = ResolveAgentHostDll();
        var sessionId = "session-grok-" + Guid.NewGuid().ToString("N");
        using var process = StartAgentHost(hostDll);
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("grok", hello.Providers);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach",
                SessionId = sessionId,
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = sessionId,
                    Provider = "grok",
                    ExecutablePath = fakeGrok.ExecutablePath,
                    WorkingDirectory = fakeGrok.Root
                }
            });

            var attachResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-attach");
            Assert.True(
                string.Equals("accepted", attachResult.Status, StringComparison.Ordinal),
                attachResult.Message ?? $"Unexpected attach status '{attachResult.Status}'.");
            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => string.Equals(patch.Patch.Session.State, "ready", StringComparison.Ordinal),
                maxPatches: 8,
                timeout: TimeSpan.FromSeconds(10));

            var attachWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                count: 32);
            Assert.Equal("grok", attachWindow.Provider);
            Assert.Equal("grok-build-0.1", attachWindow.QuickSettings.Model);
            Assert.Contains(attachWindow.QuickSettings.ModelOptions, option => option.Value == "grok-4.3");
            var capture = await WaitForFakeGrokLaunchCaptureAsync(
                fakeGrok.CapturePath,
                static launch => launch.Arguments.Length > 0);
            Assert.Equal(["agent", "stdio"], capture.Arguments);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn",
                SessionId = sessionId,
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Inspect the workspace.",
                    Attachments = []
                }
            });

            var turnResult = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "cmd-turn");
            Assert.Equal("accepted", turnResult.Status);
            Assert.Equal("grok", turnResult.TurnStarted!.Provider);

            _ = await AppServerControlHostTestClient.ReadUntilMatchAsync(
                process.StandardOutput,
                pendingPatches,
                patch => string.Equals(patch.Patch.CurrentTurn.State, "completed", StringComparison.Ordinal),
                maxPatches: 40,
                timeout: TimeSpan.FromSeconds(10));

            var turnWindow = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput,
                process.StandardInput,
                pendingPatches,
                sessionId,
                count: 96);
            Assert.Contains("Fake Grok reply.", turnWindow.Streams.AssistantText, StringComparison.Ordinal);
            Assert.Contains("Fake Grok is thinking.", turnWindow.Streams.ReasoningText, StringComparison.Ordinal);
            Assert.Contains(turnWindow.Items, item => item.ItemType == "dynamic_tool_call" && item.Status == "completed");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
    }

    private static string ResolveAgentHostDll()
    {
        return MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory);
    }

    private static async Task<FakeGrokLaunchCapture> WaitForFakeGrokLaunchCaptureAsync(
        string capturePath,
        Func<FakeGrokLaunchCapture, bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(capturePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(capturePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var capture = JsonSerializer.Deserialize<FakeGrokLaunchCapture>(json);
                        if (capture is not null && predicate(capture))
                        {
                            return capture;
                        }
                    }
                }
                catch (JsonException)
                {
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for fake Grok launch capture at '{capturePath}'.");
    }

    private static Process StartAgentHost(string hostDll)
    {
        var dotnetHost = ResolveDotNetHostPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHost,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(hostDll);
        startInfo.ArgumentList.Add("--stdio");
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start mtagenthost.");
        return process;
    }

    private static string ResolveDotNetHostPath()
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(dotnetHost) && File.Exists(dotnetHost))
        {
            return dotnetHost;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }

    private sealed class FakeGrokLaunchCapture
    {
        public string[] Arguments { get; set; } = [];
    }
}
