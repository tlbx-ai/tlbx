using System.Buffers.Binary;
using System.Diagnostics;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class NativeHistoryTransportTests
{
    [Fact]
    public async Task IndependentNativeReadUsesALengthBoundedFrameAfterTheJsonHello()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new()
            {
                FileName = "dotnet", ArgumentList = { MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory), "--stdio", "--synthetic", "codex" },
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("history.window.binary.read", hello.Capabilities, StringComparer.Ordinal);
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new()
            {
                CommandId = "native-read", SessionId = "independent", Type = "history.window.binary.get",
                HistoryWindow = new() { StartIndex = 0, Count = 80 }
            });
            var header = new byte[4];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(header, timeout.Token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            Assert.InRange(length, 5, 1024 * 1024);
            var payload = new byte[length];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(payload, timeout.Token);
            var result = NativeHistoryCodec.Decode(payload, AppServerControlHostJsonContext.Default.AppServerControlHostCommandResultEnvelope);
            Assert.Equal("native-read", result.CommandId);
            Assert.Equal("independent", result.SessionId);
            Assert.Equal("accepted", result.Status);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
        }
    }
}
