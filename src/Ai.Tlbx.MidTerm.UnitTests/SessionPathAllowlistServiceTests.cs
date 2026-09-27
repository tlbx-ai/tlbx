using System.Globalization;
using Ai.Tlbx.MidTerm.Services;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class SessionPathAllowlistServiceTests
{
    private readonly SessionPathAllowlistService _service = new();

    [Fact]
    public void PathOutsideWorkingDir_AndNotRegistered_ReturnsFalse()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "midterm_test_wd");
        var outside = Path.Combine(Path.GetTempPath(), "other_project", "file.cs");

        Assert.False(_service.IsPathAllowed("s1", outside, workDir));
    }

    [Fact]
    public void FifoEviction_OldestPathRemoved()
    {
        for (var i = 0; i < 1001; i++)
        {
            var fileName = string.Create(CultureInfo.InvariantCulture, $"file_{i:D4}.txt");
            _service.RegisterPath("s1", Path.Combine(Path.GetTempPath(), fileName));
        }

        // First path should have been evicted
        Assert.False(_service.IsPathAllowed("s1", Path.Combine(Path.GetTempPath(), "file_0000.txt"), null));
        // Last path should still be allowed
        Assert.True(_service.IsPathAllowed("s1", Path.Combine(Path.GetTempPath(), "file_1000.txt"), null));
    }

    [Fact]
    public void DuplicateRegistration_DoesNotCountTowardCapacity()
    {
        var path = Path.Combine(Path.GetTempPath(), "dupe.txt");
        _service.RegisterPath("s1", path);
        _service.RegisterPath("s1", path);

        Assert.True(_service.IsPathAllowed("s1", path, null));
    }

    [Fact]
    public void ParentDirectory_InAllowlist_AllowsChildPath()
    {
        var parent = Path.Combine(Path.GetTempPath(), "midterm_parent");
        _service.RegisterPath("s1", parent);

        var child = Path.Combine(parent, "sub", "file.txt");
        Assert.True(_service.IsPathAllowed("s1", child, null));
    }

    [Fact]
    public void ChildInAllowlist_DoesNotAllowParent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "midterm_parent2");
        var child = Path.Combine(parent, "sub", "file.txt");
        _service.RegisterPath("s1", child);

        Assert.False(_service.IsPathAllowed("s1", parent, null));
    }

    [Fact]
    public void NullOrEmptyPath_ReturnsFalse()
    {
        Assert.False(_service.IsPathAllowed("s1", "", null));
        Assert.False(_service.IsPathAllowed("s1", " ", null));
    }

    [Fact]
    public void PrefixAttack_Blocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "midterm_lo");
        _service.RegisterPath("s1", dir);

        var attack = Path.Combine(Path.GetTempPath(), "midterm_log", "secret.txt");
        Assert.False(_service.IsPathAllowed("s1", attack, null));
    }

    [Fact]
    public void DifferentSessions_AreIsolated()
    {
        _service.RegisterPath("session_a", Path.Combine(Path.GetTempPath(), "a_file.txt"));

        Assert.False(_service.IsPathAllowed("session_b", Path.Combine(Path.GetTempPath(), "a_file.txt"), null));
    }
}
