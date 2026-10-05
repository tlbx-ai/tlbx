using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class CodexCliIdentityServiceTests
{
    [Fact]
    public async Task FullUuidMustMatchCanonicalRolloutMetadataInOwningHome()
    {
        const string root = "01a10b97-2853-75e2-b134-d6fe7f527ae3";
        var home = Path.Combine(Path.GetTempPath(), "tlbx-identity-" + Guid.NewGuid().ToString("N"));
        var timestamp = long.Parse(root[..8] + root.Substring(9, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        var directory = Path.Combine(home, "sessions", date);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout-test-" + root + ".jsonl");
        try
        {
            Assert.False(await CodexCliIdentityService.ExistsAsync(root, home, CancellationToken.None));
            await File.WriteAllTextAsync(path, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"wrong\"}}\n");
            Assert.False(await CodexCliIdentityService.ExistsAsync(root, home, CancellationToken.None));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = root } }) + "\n");
            Assert.True(await CodexCliIdentityService.ExistsAsync(root, home, CancellationToken.None));
            Assert.False(await CodexCliIdentityService.ExistsAsync(root[..29], home, CancellationToken.None));
            Assert.False(await CodexCliIdentityService.ExistsAsync(root, "relative-home", CancellationToken.None));
            Assert.False(await CodexCliIdentityService.ExistsAsync(root, Path.Combine(home, "other"), CancellationToken.None));
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
