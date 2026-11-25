using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Git;
using Ai.Tlbx.MidTerm.Services.Connect;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.WebSockets;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ProtocolTrafficFixesTests
{
    [Fact]
    public void GitStatusToSummaryDropsFileArraysAndPreservesCounts()
    {
        var untracked = Enumerable.Range(0, 5000)
            .Select(i => new GitFileEntry { Path = $"logs/output-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}.txt", Status = "untracked" })
            .ToArray();
        var full = new GitStatusResponse
        {
            RepoRoot = @"Q:\repos\Jpa",
            Branch = "main",
            HeadHash = "abc123",
            Ahead = 1,
            Behind = 2,
            Staged = [new GitFileEntry { Path = "a.cs", Status = "modified", Additions = 10, Deletions = 2 }],
            Modified = [new GitFileEntry { Path = "b.cs", Status = "modified", Additions = 5, Deletions = 1 }],
            Untracked = untracked,
            Conflicted = [],
            RecentCommits = [new GitLogEntry { Hash = "abc123", ShortHash = "abc123", Message = "test" }],
            StashCount = 3,
            TotalAdditions = 15,
            TotalDeletions = 3
        };

        var fullBytes = JsonSerializer.SerializeToUtf8Bytes(full, GitJsonContext.Default.GitStatusResponse).Length;
        var summary = full.ToSummary();
        var summaryBytes = JsonSerializer.SerializeToUtf8Bytes(summary, GitJsonContext.Default.GitStatusResponse).Length;

        Assert.True(summary.FilesOmitted);
        Assert.Empty(summary.Staged);
        Assert.Empty(summary.Modified);
        Assert.Empty(summary.Untracked);
        Assert.Empty(summary.RecentCommits);
        Assert.Equal(1, summary.StagedCount);
        Assert.Equal(1, summary.ModifiedCount);
        Assert.Equal(5000, summary.UntrackedCount);
        Assert.Equal(0, summary.ConflictedCount);
        Assert.Equal(1, summary.RecentCommitsCount);
        Assert.Equal(15, summary.TotalAdditions);
        Assert.Equal(3, summary.TotalDeletions);
        Assert.True(fullBytes > 300_000, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Expected large full status, got {fullBytes}"));
        Assert.True(summaryBytes < 1_000, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Expected compact summary, got {summaryBytes}"));
    }

    [Fact]
    public void MuxCompressionThresholdCoversOneKilobytePayloads()
    {
        Assert.Equal(1024, MuxProtocol.CompressionThreshold);
    }

    [Fact]
    public async Task ConnectFrameSenderUsesLargerFragmentsForBulkChannel()
    {
        var bulkFrameLengths = new List<int>();
        using var sender = new ConnectFrameSender((_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        var payload = new byte[20_000];
        await sender.SendBulkAsync(payload, (frame, _) =>
        {
            bulkFrameLengths.Add(frame.Length);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, bulkFrameLengths.Count); // 8192 + 8192 + 3616 instead of 20 x 1024
        Assert.Equal(ConnectFrames.HeaderSize + ConnectFrameSender.BulkFragmentPayloadSize, bulkFrameLengths[0]);
    }

    [Fact]
    public void CachedHistoryPatchPreservesOmittedMetadataAndAppliesTextDeltas()
    {
        var initial = new AppServerControlHistoryWindowResponse
        {
            SessionId = "s1",
            Provider = "antigravity",
            LatestSequence = 1,
            HistoryCount = 1,
            HistoryWindowStart = 0,
            HistoryWindowEnd = 1,
            Session = new()
            {
                AvailableCommands = [new() { Name = "goal", Description = "Goal" }]
            },
            QuickSettings = new()
            {
                Model = "m1",
                ModelOptions = [new() { Value = "m1", Label = "M1" }]
            },
            Streams = new() { ReasoningText = "prior reasoning" },
            History = [new() { EntryId = "e1", Order = 1, Kind = "assistant", Body = "Hello" }]
        };

        var patch = new AppServerControlHistoryPatch
        {
            SessionId = "s1",
            Provider = "antigravity",
            LatestSequence = 2,
            HistoryCount = 1,
            Session = new() { AvailableCommandsOmitted = true },
            QuickSettings = new() { Model = "m1", OptionsOmitted = true },
            Streams = new() { StreamsOmitted = true },
            HistoryTextDeltas =
            [
                new()
                {
                    EntryId = "e1",
                    Order = 1,
                    Kind = "assistant",
                    Status = "streaming",
                    ExpectedBaseLength = 5,
                    AppendBody = " world",
                    Streaming = true,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ]
        };

        var updated = SessionAppServerControlHostRuntimeService.ApplyCachedHistoryPatch(initial, patch);
        Assert.Equal("Hello world", Assert.Single(updated.History).Body);
        Assert.Single(updated.Session.AvailableCommands);
        Assert.Single(updated.QuickSettings.ModelOptions);
        Assert.Equal("prior reasoning", updated.Streams.ReasoningText);
    }
}
