using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class HistoryServiceTests
{
    [Fact]
    public void PlainShellBookmarkRequiresAllowShellOnlyAndClearsCommandLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlbx-history-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService(dir);
            using var service = new HistoryService(settings);

            var skipped = service.RecordEntry(
                shellType: "Pwsh",
                executable: "pwsh.exe",
                commandLine: "pwsh.exe",
                workingDirectory: @"Q:\repos\Jpa",
                allowShellOnly: false);
            Assert.Null(skipped);

            var id = service.RecordEntry(
                shellType: "Pwsh",
                executable: "pwsh.exe",
                commandLine: "pwsh.exe",
                workingDirectory: @"Q:\repos\Jpa",
                label: "Jpa",
                dedupeKey: "shell|pwsh|q:/repos/jpa",
                allowShellOnly: true);

            Assert.NotNull(id);
            Assert.True(service.SetStarred(id!, true));

            var saved = service.GetEntry(id!);
            Assert.NotNull(saved);
            Assert.Equal("pwsh", saved!.Executable);
            Assert.Null(saved.CommandLine);
            Assert.Equal("Jpa", saved.Label);
            Assert.True(saved.IsStarred);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RepinningLegacyEquivalentBookmarkUpdatesInPlaceWhileDistinctFlagsCreateSeparateEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlbx-history-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService(dir);
            using var service = new HistoryService(settings);

            // Legacy bookmark created with old tuple key (processIdentity|cwd only)
            var legacyId = service.RecordEntry(
                shellType: "Pwsh",
                executable: "codex",
                commandLine: "codex --full-auto",
                workingDirectory: @"Q:\repos\Jpa",
                label: "My Codex",
                dedupeKey: "codex|q:/repos/jpa",
                allowShellOnly: true);
            Assert.NotNull(legacyId);
            service.SetStarred(legacyId!, true);

            // Re-pinning the exact same command with the new richer dedupe key updates the legacy bookmark in place
            // and preserves its custom label when label is omitted.
            var updatedId = service.RecordEntry(
                shellType: "Pwsh",
                executable: "codex",
                commandLine: "codex --full-auto",
                workingDirectory: @"Q:\repos\Jpa",
                label: null,
                dedupeKey: "term|pwsh|codex|codex --full-auto|q:/repos/jpa",
                allowShellOnly: true);
            Assert.Equal(legacyId, updatedId);
            Assert.Single(service.GetEntries());
            Assert.Equal("My Codex", service.GetEntry(legacyId!)!.Label);

            // Pinning a different flag variant in the same directory creates a distinct bookmark instead of overwriting.
            var secondId = service.RecordEntry(
                shellType: "Pwsh",
                executable: "codex",
                commandLine: "codex",
                workingDirectory: @"Q:\repos\Jpa",
                label: "Jpa",
                dedupeKey: "term|pwsh|codex|codex|q:/repos/jpa",
                allowShellOnly: true);
            Assert.NotNull(secondId);
            Assert.NotEqual(legacyId, secondId, StringComparer.Ordinal);
            service.SetStarred(secondId!, true);
            Assert.Equal(2, service.GetEntries().Count);

            // Explicit non-empty label updates existing entry on overwrite.
            var renamedId = service.RecordEntry(
                shellType: "Pwsh",
                executable: "codex",
                commandLine: "codex --full-auto",
                workingDirectory: @"Q:\repos\Jpa",
                label: "Renamed Codex",
                dedupeKey: "term|pwsh|codex|codex --full-auto|q:/repos/jpa",
                allowShellOnly: true);
            Assert.Equal(legacyId, renamedId);
            Assert.Equal("Renamed Codex", service.GetEntry(legacyId!)!.Label);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
