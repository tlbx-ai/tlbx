using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionLayoutStateServiceTests
{

    [Fact]
    public void PruneToValidSessions_RemovesStaleLeavesAndClearsSingleSessionLayouts()
    {
        var stateDir = CreateTempDirectory();
        var service = new SessionLayoutStateService(stateDir);
        try
        {
            service.UpdateLayout(
                new LayoutNode
                {
                    Type = "split",
                    Direction = "horizontal",
                    Children =
                    [
                        new LayoutNode { Type = "leaf", SessionId = "s1" },
                        new LayoutNode
                        {
                            Type = "split",
                            Direction = "vertical",
                            Children =
                            [
                                new LayoutNode { Type = "leaf", SessionId = "s2" },
                                new LayoutNode { Type = "leaf", SessionId = "s3" }
                            ]
                        }
                    ]
                },
                focusedSessionId: "s3",
                validSessionIds: ["s1", "s2", "s3"]);

            var snapshot = service.PruneToValidSessions(["s3"]);

            Assert.Null(snapshot.Root);
            Assert.Null(snapshot.FocusedSessionId);
        }
        finally
        {
            Directory.Delete(stateDir, recursive: true);
        }
    }

    [Fact]
    public void RemoveSession_ReclaimsSpaceByCollapsingSingleChildSplits()
    {
        var stateDir = CreateTempDirectory();
        try
        {
            var service = new SessionLayoutStateService(stateDir);
            service.UpdateLayout(
                new LayoutNode
                {
                    Type = "split",
                    Direction = "horizontal",
                    Children =
                    [
                        new LayoutNode
                        {
                            Type = "split",
                            Direction = "vertical",
                            Children =
                            [
                                new LayoutNode { Type = "leaf", SessionId = "s1" },
                                new LayoutNode { Type = "leaf", SessionId = "s2" }
                            ]
                        },
                        new LayoutNode { Type = "leaf", SessionId = "s3" }
                    ]
                },
                focusedSessionId: "s2",
                validSessionIds: ["s1", "s2", "s3"]);

            var snapshot = service.RemoveSession("s2");

            Assert.NotNull(snapshot.Root);
            Assert.Equal("split", snapshot.Root!.Type);
            Assert.Equal("horizontal", snapshot.Root.Direction);
            Assert.Equal(["s1", "s3"], GetLeafSessionIds(snapshot.Root));
        }
        finally
        {
            Directory.Delete(stateDir, recursive: true);
        }
    }

    [Fact]
    public void TryUpdateLayout_RejectsStaleRevisionAndReturnsCurrentSnapshot()
    {
        var stateDir = CreateTempDirectory();
        try
        {
            var service = new SessionLayoutStateService(stateDir);
            var first = service.UpdateLayout(
                new LayoutNode
                {
                    Type = "split",
                    Direction = "horizontal",
                    Children =
                    [
                        new LayoutNode { Type = "leaf", SessionId = "s1" },
                        new LayoutNode { Type = "leaf", SessionId = "s2" }
                    ]
                },
                focusedSessionId: "s2",
                validSessionIds: ["s1", "s2"]);

            var second = service.TryUpdateLayout(
                new LayoutNode
                {
                    Type = "split",
                    Direction = "vertical",
                    Children =
                    [
                        new LayoutNode { Type = "leaf", SessionId = "s1" },
                        new LayoutNode { Type = "leaf", SessionId = "s2" }
                    ]
                },
                focusedSessionId: "s1",
                expectedRevision: first.Revision - 1,
                validSessionIds: ["s1", "s2"]);

            Assert.False(second.Applied);
            Assert.True(second.Conflict);
            Assert.Equal(first.Revision, second.Snapshot.Revision);
            Assert.Equal("horizontal", second.Snapshot.Root!.Direction);
            Assert.Equal("s2", second.Snapshot.FocusedSessionId);
        }
        finally
        {
            Directory.Delete(stateDir, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "midterm-layout-state-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static IReadOnlyList<string> GetLeafSessionIds(LayoutNode? node)
    {
        var ids = new List<string>();
        Collect(node, ids);
        return ids;
    }

    private static void Collect(LayoutNode? node, List<string> ids)
    {
        if (node is null)
        {
            return;
        }

        if (string.Equals(node.Type, "leaf", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(node.SessionId))
            {
                ids.Add(node.SessionId);
            }

            return;
        }

        if (node.Children is null)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            Collect(child, ids);
        }
    }
}
