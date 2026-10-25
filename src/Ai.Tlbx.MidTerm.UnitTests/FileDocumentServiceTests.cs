using System.Text;
using Ai.Tlbx.MidTerm.Services;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

// The large-file test measures process-wide allocations, including other tests.
[Collection(TimingSensitiveCollection.Name)]
public sealed class FileDocumentServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"file_document_test_{Guid.NewGuid():N}");
    private readonly FileDocumentService _documents = new();

    public FileDocumentServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_folder);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("file_document_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test fixture path.");
        Directory.Delete(resolved, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ChangedOnDisk_IsRejectedWithoutTouchingEitherVersion()
    {
        var path = Path.Combine(_folder, "notes.md");
        await File.WriteAllTextAsync(path, "base\n", Xunit.TestContext.Current.CancellationToken);
        var original = await _documents.ReadAsync(path, true, default);
        await File.WriteAllTextAsync(path, "external agent\n", Xunit.TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<FileDocumentException>(() =>
            _documents.SaveAsync(path, "my draft\n", original.Version!, default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("changed", error.Code);
        Assert.Equal("external agent\n", await File.ReadAllTextAsync(path, Xunit.TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_folder, ".tlbx-*"));
    }

    [UnixFact]
    public async Task LinkTargetWithAnAliasedParentReadsAndSavesThePhysicalDocument()
    {
        var physical = Path.Combine(_folder, "physical"); var documentFolder = Path.Combine(physical, "documents");
        Directory.CreateDirectory(documentFolder);
        var parentAlias = Path.Combine(_folder, "parent-alias"); Directory.CreateSymbolicLink(parentAlias, physical);
        var targetAlias = Path.Combine(_folder, "target-alias"); Directory.CreateSymbolicLink(targetAlias, Path.Combine(parentAlias, "documents"));
        var actual = Path.Combine(documentFolder, "notes.txt"); var logical = Path.Combine(targetAlias, "notes.txt");
        await File.WriteAllTextAsync(actual, "Physical baseline\n", Xunit.TestContext.Current.CancellationToken);
        var original = await _documents.ReadAsync(logical, true, default);
        Assert.Equal("Physical baseline\n", original.Content);
        await _documents.SaveAsync(logical, "Saved through aliases\n", original.Version!, default);
        Assert.Equal("Saved through aliases\n", await File.ReadAllTextAsync(actual, Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentClients_OnlyOneDistinctDraftCanCommitTheSameBase()
    {
        var path = Path.Combine(_folder, "shared.md");
        await File.WriteAllTextAsync(path, "base", Xunit.TestContext.Current.CancellationToken);
        var original = await _documents.ReadAsync(path, true, default);
        async Task<bool> Save(string text)
        {
            try { await _documents.SaveAsync(path, text, original.Version!, default); return true; }
            catch (FileDocumentException ex) when (ex.StatusCode == 409) { return false; }
        }
        var commits = await Task.WhenAll(Save("client one"), Save("client two"));
        Assert.Single(commits.Where(static committed => committed));
        Assert.Contains(await File.ReadAllTextAsync(path, Xunit.TestContext.Current.CancellationToken), new[] { "client one", "client two" }, StringComparer.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_folder, ".tlbx-*"));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public async Task Save_PreservesEncodingBomAndCrLf(string kind)
    {
        Encoding encoding = kind switch
        {
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true),
            "utf16-be" => new UnicodeEncoding(true, true),
            "utf32-le" => new UTF32Encoding(false, true),
            "utf32-be" => new UTF32Encoding(true, true),
            _ => new UTF8Encoding(false)
        };
        var path = Path.Combine(_folder, kind + ".txt");
        await File.WriteAllTextAsync(path, "Hällo 🎸\r\nsecond\r\n", encoding, Xunit.TestContext.Current.CancellationToken);
        var original = await _documents.ReadAsync(path, true, default);
        Assert.Equal("Hällo 🎸\nsecond\n", original.Content);
        Assert.Equal("crlf", original.LineEnding);
        var saved = await _documents.SaveAsync(path, "Changed 🎸\nsecond\n", original.Version!, default);
        var wanted = encoding.GetPreamble().Concat(encoding.GetBytes("Changed 🎸\r\nsecond\r\n")).ToArray();
        Assert.Equal(wanted, await File.ReadAllBytesAsync(path, Xunit.TestContext.Current.CancellationToken));
        var current = await _documents.ReadAsync(path, true, default);
        Assert.Equal(current.Version, saved.Version);
        Assert.Equal(original.Encoding, saved.Encoding);
    }

    [Fact]
    public async Task DeletedDocument_IsNotSilentlyRecreated()
    {
        var path = Path.Combine(_folder, "deleted.txt");
        await File.WriteAllTextAsync(path, "base", Xunit.TestContext.Current.CancellationToken);
        var original = await _documents.ReadAsync(path, true, default);
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _documents.SaveAsync(path, "draft", original.Version!, default));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task VersionFromAnotherPath_CannotAuthorizeAWrite()
    {
        var first = Path.Combine(_folder, "A.txt");
        var second = Path.Combine(_folder, "B.txt");
        await File.WriteAllTextAsync(first, "same base", Xunit.TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "same base", Xunit.TestContext.Current.CancellationToken);
        var original = await _documents.ReadAsync(first, true, default);
        var error = await Assert.ThrowsAsync<FileDocumentException>(() => _documents.SaveAsync(second, "wrong target", original.Version!, default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("same base", await File.ReadAllTextAsync(second, Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARepeatedCompletedSave_IsIdempotent()
    {
        var path = Path.Combine(_folder, "retry.txt");
        await File.WriteAllTextAsync(path, "base", Xunit.TestContext.Current.CancellationToken);
        var original = await _documents.ReadAsync(path, true, default);
        var first = await _documents.SaveAsync(path, "draft", original.Version!, default);
        var modified = File.GetLastWriteTimeUtc(path);
        var repeated = await _documents.SaveAsync(path, "draft", original.Version!, default);
        Assert.Equal(first.Version, repeated.Version);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task CancellationBeforePublication_LeavesOriginalUntouched()
    {
        var path = Path.Combine(_folder, "cancelled.txt");
        await File.WriteAllTextAsync(path, "base", Xunit.TestContext.Current.CancellationToken);
        var original = await _documents.ReadAsync(path, true, default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _documents.SaveAsync(path, "draft", original.Version!, cancelled.Token));
        Assert.Equal("base", await File.ReadAllTextAsync(path, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_folder, ".tlbx-*"));
    }

    [Fact]
    public async Task MissingVersion_NeverChangesTheFile()
    {
        var path = Path.Combine(_folder, "blind.txt");
        await File.WriteAllTextAsync(path, "base", Xunit.TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<FileDocumentException>(() => _documents.SaveAsync(path, "draft", "", default));
        Assert.Equal(428, error.StatusCode);
        Assert.Equal("base", await File.ReadAllTextAsync(path, Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadOnlyFile_IsNotChangedEvenWithAValidVersion()
    {
        var path = Path.Combine(_folder, "readonly.txt");
        await File.WriteAllTextAsync(path, "base", Xunit.TestContext.Current.CancellationToken);
        var original = await _documents.ReadAsync(path, true, default);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _documents.SaveAsync(path, "draft", original.Version!, default));
            Assert.Equal("base", await File.ReadAllTextAsync(path, Xunit.TestContext.Current.CancellationToken));
            Assert.True((await _documents.ReadAsync(path, true, default)).ReadOnly);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public async Task PreviewDoesNotReplaceAnIncompleteUnicodeCodePoint()
    {
        var path = Path.Combine(_folder, "unicode-boundary.txt");
        await File.WriteAllTextAsync(path, new string('x', FileDocumentService.PreviewBytes - 2) + "🎸end", Xunit.TestContext.Current.CancellationToken);
        var preview = await _documents.ReadAsync(path, false, default);
        Assert.True(preview.IsTruncated);
        Assert.DoesNotContain('\uFFFD', preview.Content);
        Assert.DoesNotContain("🎸", preview.Content, StringComparison.Ordinal);
        Assert.Equal(FileDocumentService.PreviewBytes - 2, preview.Content.Length);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    public async Task PagesReconstructUnicodeAndCrLfWithoutMissingOrDuplicatedCharacters(string kind)
    {
        var path = Path.Combine(_folder, "paged-" + kind + ".txt");
        var encoding = kind == "utf16" ? (Encoding)new UnicodeEncoding(false, true) : new UTF8Encoding(true);
        var text = new string('a', FileDocumentService.PreviewBytes - 8) + "🎸\r\nsecond " + new string('b', FileDocumentService.PreviewBytes) + "Ω end";
        await File.WriteAllTextAsync(path, text, encoding, Xunit.TestContext.Current.CancellationToken);
        var combined = new StringBuilder();
        long offset = 0;
        var pages = 0;
        do
        {
            var page = await _documents.ReadAsync(path, false, default, offset);
            Assert.True(page.ReadOnly);
            Assert.Null(page.Version);
            Assert.InRange(page.PreviewBytes, 1, FileDocumentService.PreviewBytes);
            Assert.DoesNotContain('\uFFFD', page.Content);
            Assert.True(page.NextOffset > offset);
            combined.Append(page.Content);
            offset = page.NextOffset;
            pages++;
        } while (offset < new FileInfo(path).Length);
        Assert.True(pages > 1);
        Assert.Equal(text.Replace("\r\n", "\n", StringComparison.Ordinal), combined.ToString());
    }

    [Fact]
    public async Task CrLfSplitAtByteBoundaryIsKeptOnTheFollowingPage()
    {
        var path = Path.Combine(_folder, "crlf-boundary.txt");
        await File.WriteAllTextAsync(path, new string('x', FileDocumentService.PreviewBytes - 1) + "\r\nend", new UTF8Encoding(false), Xunit.TestContext.Current.CancellationToken);
        var first = await _documents.ReadAsync(path, false, default);
        Assert.Equal(FileDocumentService.PreviewBytes - 1, first.NextOffset);
        var second = await _documents.ReadAsync(path, false, default, first.NextOffset);
        Assert.Equal("\nend", second.Content);
    }

    [Fact]
    public async Task LargeFilePreviewDoesNotAllocateTheWholeFile()
    {
        var path = Path.Combine(_folder, "bounded.txt");
        await using (var output = File.Create(path))
        {
            var chunk = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024));
            for (var i = 0; i < 64; i++) await output.WriteAsync(chunk, Xunit.TestContext.Current.CancellationToken);
        }
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var preview = await _documents.ReadAsync(path, false, default);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(preview.ReadOnly);
        Assert.False(preview.CanLoadFull);
        Assert.Equal(FileDocumentService.PreviewBytes, preview.Content.Length);
        Assert.InRange(allocated, 0, 12 * 1024 * 1024);
        var later = await _documents.ReadAsync(path, false, default, preview.NextOffset);
        Assert.Equal(preview.NextOffset, later.Offset);
        Assert.True(later.NextOffset > later.Offset);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public async Task InvalidPageOffsetsDoNotReadAnotherDocument(long offset)
    {
        var path = Path.Combine(_folder, "offset.txt");
        await File.WriteAllTextAsync(path, "short", Xunit.TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<FileDocumentException>(() => _documents.ReadAsync(path, false, default, offset));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task PartialPreview_IsReadOnlyAndCarriesNoWritableVersion()
    {
        var path = Path.Combine(_folder, "large.txt");
        await File.WriteAllTextAsync(path, new string('x', FileDocumentService.PreviewBytes) + "🎸tail", Xunit.TestContext.Current.CancellationToken);
        var preview = await _documents.ReadAsync(path, false, default);
        Assert.True(preview.ReadOnly);
        Assert.True(preview.IsTruncated);
        Assert.True(preview.CanLoadFull);
        Assert.Null(preview.Version);
        Assert.DoesNotContain('\uFFFD', preview.Content);
        var complete = await _documents.ReadAsync(path, true, default);
        Assert.False(complete.IsTruncated);
        Assert.NotNull(complete.Version);
        Assert.EndsWith("🎸tail", complete.Content, StringComparison.Ordinal);
    }
}
