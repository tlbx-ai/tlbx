using Ai.Tlbx.MidTerm.Services.Hosting;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MidTermInstanceIdentityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "midterm-instance-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SettingsGuardName_IsStableAcrossPortsForSameSettingsDirectory()
    {
        Directory.CreateDirectory(_tempDir);

        var first = MidTermInstanceIdentity.Load(_tempDir, 2000);
        var second = MidTermInstanceIdentity.Load(_tempDir, 3000);

        Assert.Equal(first.SettingsGuardName, second.SettingsGuardName);
        Assert.NotEqual(first.PortGuardName, second.PortGuardName, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
