using Ai.Tlbx.MidTerm.Services;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class FileEndpointsTests
{
    [Theory]
    [InlineData("https://github.com/tlbx-ai/MidTerm.git", "MidTerm")]
    [InlineData("git@github.com:tlbx-ai/MidTerm.git", "MidTerm")]
    public void TryResolveCloneDirectoryName_ExtractsRepositoryFolderName(string repositoryUrl, string expected)
    {
        var result = FileEndpoints.TryResolveCloneDirectoryName(repositoryUrl, out var directoryName);

        Assert.True(result);
        Assert.Equal(expected, directoryName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://github.com")]
    public void TryResolveCloneDirectoryName_RejectsInvalidRepositoryUrl(string repositoryUrl)
    {
        var result = FileEndpoints.TryResolveCloneDirectoryName(repositoryUrl, out var directoryName);

        Assert.False(result);
        Assert.Equal(string.Empty, directoryName);
    }

    [Theory]
    [InlineData("repo")]
    public void TryValidateLauncherDirectoryName_AcceptsSimpleFolderNames(string name)
    {
        var result = FileEndpoints.TryValidateLauncherDirectoryName(name, out var normalizedName, out var error);

        Assert.True(result);
        Assert.Equal(name, normalizedName);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("nested/path")]
    [InlineData(@"nested\path")]
    public void TryValidateLauncherDirectoryName_RejectsUnsafeFolderNames(string name)
    {
        var result = FileEndpoints.TryValidateLauncherDirectoryName(name, out _, out var error);

        Assert.False(result);
        Assert.NotEmpty(error);
    }
}
