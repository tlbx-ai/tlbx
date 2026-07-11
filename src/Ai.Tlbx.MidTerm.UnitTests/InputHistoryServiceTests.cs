using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.InputHistory;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class InputHistoryServiceTests : IDisposable
{
    private readonly string _tempDir;

    public InputHistoryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"midterm_input_history_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void RecordsAndFiltersExactTerminalEvents()
    {
        using var service = CreateService();

        var prompt = service.RecordPrompt(
            "session-a",
            "Codex",
            @"Q:\repo",
            InputHistorySources.CommandBay,
            InputHistorySurfaces.Terminal,
            new AppServerControlTurnRequest { Text = "inspect the failing test" });
        service.RecordPaste(
            "session-a",
            "Codex",
            @"Q:\repo",
            "Q:/repo/screenshot.png",
            bracketedPaste: true,
            isFilePath: true);
        service.RecordPaste(
            "session-b",
            "Shell",
            @"Q:\other",
            "git status",
            bracketedPaste: false,
            isFilePath: false);

        var response = service.GetEntries("session-a", InputHistoryKinds.Prompt, 20);

        var entry = Assert.Single(response.Entries);
        Assert.Equal(1, response.TotalCount);
        Assert.Equal(prompt.Id, entry.Id);
        Assert.Equal("inspect the failing test", entry.Text);
        Assert.True(entry.Submit);
        Assert.Equal(InputHistorySurfaces.Terminal, entry.Surface);
    }

    [Fact]
    public void PersistsUploadsAndPromptReplayPayloads()
    {
        string entryId;
        using (var service = CreateService())
        {
            var entry = service.RecordPrompt(
                "session-a",
                "Codex",
                @"Q:\repo",
                InputHistorySources.CommandBay,
                InputHistorySurfaces.Terminal,
                new AppServerControlTurnRequest
                {
                    Text = "review this",
                    TerminalReplay =
                    [
                        new AppServerControlTerminalReplayStep
                        {
                            Kind = "image",
                            Path = @"Q:\repo\.midterm\uploads\screen.png",
                            MimeType = "image/png"
                        }
                    ]
                });
            entryId = entry.Id;
            service.RecordUpload(
                "session-a",
                "Codex",
                @"Q:\repo",
                @"Q:\repo\.midterm\uploads\screen.png",
                "screen.png",
                "image/png",
                2048,
                InputHistorySources.Clipboard);
        }

        using var reloaded = CreateService();
        var prompt = reloaded.GetEntry(entryId);
        var images = reloaded.GetEntries("session-a", InputHistoryKinds.ImagePaste, 20);

        Assert.NotNull(prompt);
        Assert.Equal("review this", prompt!.Turn?.Text);
        Assert.Equal(@"Q:\repo\.midterm\uploads\screen.png", Assert.Single(prompt.Turn!.TerminalReplay).Path);
        var image = Assert.Single(images.Entries);
        Assert.Equal("screen.png", image.DisplayName);
        Assert.Equal(2048, image.SizeBytes);
    }

    [Fact]
    public void BoundsEntryAndAggregateMemory()
    {
        using var service = CreateService();
        var oversized = new string('x', InputHistoryService.MaxEntryTextCharacters + 500);

        service.RecordPaste(
            "session-a",
            null,
            null,
            oversized,
            bracketedPaste: true,
            isFilePath: false);
        for (var index = 0; index < InputHistoryService.MaxEntries + 20; index++)
        {
            service.RecordPaste(
                "session-a",
                null,
                null,
                $"paste-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                bracketedPaste: true,
                isFilePath: false);
        }

        var response = service.GetEntries("session-a", null, InputHistoryService.MaxEntries);

        Assert.Equal(InputHistoryService.MaxEntries, response.TotalCount);
        Assert.Equal(InputHistoryService.MaxEntries, response.Entries.Count);
        Assert.DoesNotContain(response.Entries, entry => entry.Text?.Length > InputHistoryService.MaxEntryTextCharacters);
    }

    [Fact]
    public void BoundsNestedReplayPayloadsAndAggregateMemory()
    {
        using var service = CreateService();
        var replayText = new string('r', InputHistoryService.MaxEntryTextCharacters);

        for (var index = 0; index < 100; index++)
        {
            service.RecordPrompt(
                "session-a",
                null,
                null,
                InputHistorySources.CommandBay,
                InputHistorySurfaces.Terminal,
                new AppServerControlTurnRequest
                {
                    Text = $"prompt-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    Attachments = Enumerable.Range(0, InputHistoryService.MaxAttachmentsPerEntry + 10)
                        .Select(attachmentIndex => new AppServerControlAttachmentReference
                        {
                            Path = $"file-{attachmentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                        })
                        .ToList(),
                    TerminalReplay = Enumerable.Range(0, InputHistoryService.MaxReplayStepsPerEntry + 10)
                        .Select(_ => new AppServerControlTerminalReplayStep { Text = replayText })
                        .ToList()
                });
        }

        var response = service.GetEntries("session-a", null, InputHistoryService.MaxEntries);
        var storedCharacters = response.Entries.Sum(entry =>
            (entry.Text?.Length ?? 0) +
            (entry.Turn?.TerminalReplay.Sum(step => step.Text?.Length ?? 0) ?? 0));

        Assert.InRange(storedCharacters, 1, InputHistoryService.MaxTotalTextCharacters);
        Assert.All(response.Entries, entry =>
        {
            Assert.True(entry.Turn!.Attachments.Count <= InputHistoryService.MaxAttachmentsPerEntry);
            Assert.True(entry.Turn.TerminalReplay.Count <= InputHistoryService.MaxReplayStepsPerEntry);
        });
    }

    [Fact]
    public void ReturnedEntriesDoNotMutateStoredState()
    {
        using var service = CreateService();
        var recorded = service.RecordPrompt(
            "session-a",
            "Codex",
            @"Q:\repo",
            InputHistorySources.CommandBay,
            InputHistorySurfaces.Terminal,
            new AppServerControlTurnRequest { Text = "original" });

        recorded.Text = "changed";
        recorded.Turn!.Text = "changed";
        var loaded = service.GetEntry(recorded.Id);

        Assert.NotNull(loaded);
        Assert.Equal("original", loaded!.Text);
        Assert.Equal("original", loaded.Turn!.Text);
    }

    [Fact]
    public void ListingRequiresAnOwningSession()
    {
        using var service = CreateService();

        Assert.Throws<ArgumentException>(() => service.GetEntries("", null, 20));
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

    private InputHistoryService CreateService()
    {
        return new InputHistoryService(new SettingsService(_tempDir));
    }
}
