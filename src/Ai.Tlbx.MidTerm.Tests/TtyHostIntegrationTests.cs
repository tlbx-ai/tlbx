using Xunit;

namespace Ai.Tlbx.MidTerm.Tests;

public class TtyHostIntegrationTests
{
    [Fact]
    public void ZZZ_NoOrphanMthostProcesses_AfterAllTests()
    {
        var orphans = TestCleanupHelper.GetOrphanTestMthostProcesses();

        if (orphans.Count > 0)
        {
            var paths = orphans.Select(p =>
            {
                try { return p.MainModule?.FileName ?? "unknown"; }
                catch { return "access denied"; }
            });

            TestCleanupHelper.KillOrphanTestProcesses();

            Assert.Fail($"Found {orphans.Count} orphan mthost processes from test builds: {string.Join(", ", paths)}");
        }
    }
}
