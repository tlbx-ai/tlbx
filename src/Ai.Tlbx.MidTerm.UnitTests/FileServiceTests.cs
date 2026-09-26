using Ai.Tlbx.MidTerm.Services;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class FileServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FileServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"midterm_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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

        FileService.ResetFileInfoCacheForTests();

        GC.SuppressFinalize(this);
    }

    // =======================================================================
    // ValidatePath
    // =======================================================================

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  ")]
    public void ValidatePath_RejectsEmptyOrWhitespace(string path)
    {
        var result = FileService.ValidatePath(path, out var errorResult);

        Assert.False(result);
        Assert.NotNull(errorResult);
    }

    [Fact]
    public void ValidatePath_RejectsTraversal()
    {
        var result = FileService.ValidatePath(@"C:\foo\..\bar", out var errorResult);

        Assert.False(result);
        Assert.NotNull(errorResult);
    }

    [Fact]
    public void ValidatePath_RejectsRelativePath()
    {
        var result = FileService.ValidatePath("src/main.ts", out var errorResult);

        Assert.False(result);
        Assert.NotNull(errorResult);
    }

    // =======================================================================
    // IsWithinDirectory
    // =======================================================================

    [Fact]
    public void IsWithinDirectory_ChildIsWithinParent()
    {
        var parent = Path.Combine(_tempDir, "parent");
        var child = Path.Combine(parent, "sub", "file.txt");
        Directory.CreateDirectory(Path.Combine(parent, "sub"));

        Assert.True(FileService.IsWithinDirectory(child, parent));
    }

    [Fact]
    public void IsWithinDirectory_SamePathReturnsTrue()
    {
        Assert.True(FileService.IsWithinDirectory(_tempDir, _tempDir));
    }

    [Fact]
    public void IsWithinDirectory_SiblingIsNot()
    {
        var dir1 = Path.Combine(_tempDir, "dir1");
        var dir2 = Path.Combine(_tempDir, "dir2");

        Assert.False(FileService.IsWithinDirectory(dir2, dir1));
    }

    [Fact]
    public void IsWithinDirectory_PrefixAttackBlocked()
    {
        var dir = Path.Combine(_tempDir, "Users");
        var attack = Path.Combine(_tempDir, "Users2", "file.txt");

        Assert.False(FileService.IsWithinDirectory(attack, dir));
    }

    [Fact]
    public void IsWithinDirectory_TrailingSepHandled()
    {
        var dir = _tempDir + Path.DirectorySeparatorChar;
        var child = Path.Combine(_tempDir, "file.txt");

        Assert.True(FileService.IsWithinDirectory(child, dir));
    }

    // =======================================================================
    // GetSlashVariants
    // =======================================================================

    // =======================================================================
    // SearchTree
    // =======================================================================

    private void CreateFile(params string[] pathParts)
    {
        var filePath = Path.Combine(new[] { _tempDir }.Concat(pathParts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "test content");
    }

    private void CreateDir(params string[] pathParts)
    {
        var dirPath = Path.Combine(new[] { _tempDir }.Concat(pathParts).ToArray());
        Directory.CreateDirectory(dirPath);
    }

    [Fact]
    public void SearchTree_FindsFileInSubdirectory()
    {
        CreateFile("src", "main.ts");

        var result = FileService.SearchTree(_tempDir, "src/main.ts", maxDepth: 5);

        Assert.NotNull(result);
        Assert.EndsWith("main.ts", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchTree_SkipsNodeModules()
    {
        CreateFile("node_modules", "pkg", "index.js");

        var result = FileService.SearchTree(_tempDir, "index.js", maxDepth: 5);

        Assert.Null(result);
    }

    [Fact]
    public void SearchTree_SkipsGitDirectory()
    {
        CreateFile(".git", "config");

        var result = FileService.SearchTree(_tempDir, "config", maxDepth: 5);

        Assert.Null(result);
    }

    [Fact]
    public void SearchTree_RespectsMaxDepth()
    {
        // Create file at depth 6 (6 subdirectories deep)
        CreateFile("a", "b", "c", "d", "e", "f", "deep.txt");

        var result = FileService.SearchTree(_tempDir, "deep.txt", maxDepth: 5);

        Assert.Null(result);
    }

    // =======================================================================
    // GetFileInfo
    // =======================================================================

    [Fact]
    public void GetFileInfo_UsesProcessGlobalCacheAcrossCalls()
    {
        FileService.ResetFileInfoCacheForTests();
        var filePath = Path.Combine(_tempDir, "cached.txt");
        File.WriteAllText(filePath, "cached");

        var initial = FileService.GetFileInfo(filePath);
        File.Delete(filePath);
        var cached = FileService.GetFileInfo(filePath);

        Assert.True(initial.Exists);
        Assert.True(cached.Exists);

        FileService.ResetFileInfoCacheForTests();

        var refreshed = FileService.GetFileInfo(filePath);
        Assert.False(refreshed.Exists);
    }
}
