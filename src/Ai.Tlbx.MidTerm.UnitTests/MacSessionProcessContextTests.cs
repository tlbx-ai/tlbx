using System.Diagnostics;
using System.Globalization;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MacSessionProcessContextTests
{
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    [Fact(Skip = "Requires actual macOS process syscalls and the system C compiler.", SkipUnless = nameof(IsMacOS))]
    public async Task NativeArgumentsAndParentEpochsBindPrivateChildrenButRejectSharedAncestry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-mcp-native-" + Guid.NewGuid().ToString("N"), "日本 argv");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "probe.c");
            var executable = Path.Combine(directory, "codex");
            // Both processes expire independently, including if the test runner is interrupted.
            await File.WriteAllTextAsync(source, """
                #include <stdio.h>
                #include <unistd.h>
                int main(void) {
                    alarm(60);
                    pid_t child = fork();
                    if (child < 0) return 2;
                    if (child == 0) { alarm(60); for (;;) pause(); }
                    printf("%d\n", (int)child); fflush(stdout);
                    for (;;) pause();
                }
                """, TestContext.Current.CancellationToken);
            var compile = new ProcessStartInfo("/usr/bin/cc") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            compile.ArgumentList.Add(source);
            compile.ArgumentList.Add("-o");
            compile.ArgumentList.Add(executable);
            using (var compiler = Process.Start(compile)!)
            {
                var stdout = compiler.StandardOutput.ReadToEndAsync(CancellationToken.None);
                var stderr = compiler.StandardError.ReadToEndAsync(CancellationToken.None);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await compiler.WaitForExitAsync(timeout.Token); }
                finally { if (!compiler.HasExited) compiler.Kill(entireProcessTree: true); }
                Assert.True(compiler.ExitCode == 0, await stdout + await stderr);
            }

            foreach (var (arguments, shared) in new (string[] Arguments, bool Shared)[]
            {
                (["exec", "日本 app-server --managed-daemon", "", "--", "literal"], false),
                (["app-server", "--listen", "unix:///tmp/日本 socket"], true),
                (["--managed-daemon"], true)
            })
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                // KERN_PROCARGS2 includes these after argv; they must never classify a private CLI as shared.
                start.Environment["TLBX_NATIVE_TEST_HINT"] = "app-server";
                start.Environment["TLBX_NATIVE_TEST_FLAG"] = "--managed-daemon";
                using var process = Process.Start(start)!;
                var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
                Process? child = null;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var childLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    child = Process.GetProcessById(int.Parse(childLine!, CultureInfo.InvariantCulture));
                    Assert.Equal("codex", process.ProcessName);
                    Assert.Equal(shared, SessionProcessContext.IsSharedCodexBackend(process.Id));
                    var parentInfo = SessionProcessContext.ReadProcess(process.Id);
                    var childInfo = SessionProcessContext.ReadProcess(child.Id);
                    Assert.NotNull(parentInfo);
                    Assert.NotNull(childInfo);
                    Assert.Equal(Environment.ProcessId, parentInfo.Value.Parent);
                    Assert.Equal(process.Id, childInfo.Value.Parent);
                    Assert.Equal(process.StartTime.ToUniversalTime(), parentInfo.Value.Started);
                    Assert.True(childInfo.Value.Started >= parentInfo.Value.Started);
                    Assert.Equal("first001", SessionProcessContext.Resolve(child.Id,
                        new Dictionary<int, string> { [process.Id] = "first001" }));
                    using var context = new SessionCliContextService(
                        () => [new("first001", Environment.ProcessId, process.Id)],
                        SessionProcessContext.ReadProcess, TimeProvider.System);
                    const string thread = "01a10b97-2853-75e2-b134-d6fe7f527ae3";
                    var result = context.Resolve(thread, thread, child.Id);
                    if (shared) Assert.Contains("TLBX_CONTEXT_UNBOUND", result, StringComparison.Ordinal);
                    else Assert.StartsWith("first001:", result, StringComparison.Ordinal);
                }
                finally
                {
                    if (child is not null)
                    {
                        using (child) { if (!child.HasExited) child.Kill(); }
                    }
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    Assert.Equal("", await stderr);
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
