using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class CodexCliIdentityServiceTests
{
    [Fact]
    public async Task MissingSocketReportsCauseWithoutExecutingACommand()
    {
        var result = await CodexCliIdentityService.EmitProofAsync("01a10b97-2853-75e2-b134-d6fe7f527ae3",
            Path.Combine(Path.GetTempPath(), "tlbx-missing-" + Guid.NewGuid().ToString("N")),
            "TLBXCTX:0123456789abcdef0123456789abcdef", CancellationToken.None);
        Assert.False(result.Submitted);
        Assert.Equal("no local Codex control socket in CODEX_HOME", result.Diagnostic);
    }

    [Theory]
    [InlineData("TLBXCTX:0123456789abcdef0123456789abcde;")]
    [InlineData("TLBXCTX:0123456789abcdef0123456789abcdef; echo unsafe")]
    public async Task ChallengeMustContainOnlyServerNonce(string challenge)
    {
        var result = await CodexCliIdentityService.EmitProofAsync("01a10b97-2853-75e2-b134-d6fe7f527ae3",
            Path.GetTempPath(), challenge, CancellationToken.None);
        Assert.False(result.Submitted);
        Assert.Equal("invalid challenge", result.Diagnostic);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task FullUuidMustMatchCanonicalRolloutMetadataInOwningHome(bool activeWriter, int dayOffset)
    {
        const string root = "01a10b97-2853-75e2-b134-d6fe7f527ae3";
        var home = Path.Combine(Path.GetTempPath(), "tlbx-identity-" + Guid.NewGuid().ToString("N"));
        var timestamp = long.Parse(string.Concat(root.AsSpan(0, 8), root.AsSpan(9, 4)), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).AddDays(dayOffset).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        var directory = Path.Combine(home, "sessions", date);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout-test-" + root + ".jsonl");
        try
        {
            Assert.False(await CodexCliIdentityService.ExistsAsync(root, home, CancellationToken.None));
            await File.WriteAllTextAsync(path, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"wrong\"}}\n");
            Assert.False(await CodexCliIdentityService.ExistsAsync(root, home, CancellationToken.None));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = root } }) + "\n");
            // Codex keeps resumed rollouts open for append. Readers must share write access on Windows.
            using var writer = activeWriter
                ? new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)
                : null;
            Assert.True(await CodexCliIdentityService.ExistsAsync(root, home, CancellationToken.None));
            Assert.False(await CodexCliIdentityService.ExistsAsync(root[..29], home, CancellationToken.None));
            Assert.False(await CodexCliIdentityService.ExistsAsync(root, "relative-home", CancellationToken.None));
            Assert.False(await CodexCliIdentityService.ExistsAsync(root, Path.Combine(home, "other"), CancellationToken.None));
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
