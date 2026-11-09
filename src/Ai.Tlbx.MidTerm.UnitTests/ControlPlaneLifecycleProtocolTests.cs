using Ai.Tlbx.MidTerm.Models.ControlPlane;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ControlPlaneLifecycleProtocolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-control-lifecycle-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("active", "open", null)]
    [InlineData("open", "open", null)]
    [InlineData("waiting", "waiting", null)]
    [InlineData("blocked", "blocked", null)]
    [InlineData("done", "done", "closed")]
    [InlineData("dismissed", "dismissed", "closed")]
    public void CloseReleasesAssignmentsWithoutInventingCompletion(string state, string expectedState, string? expectedOwner)
    {
        using var service = new ControlPlaneService(new SettingsService(_directory));
        var item = service.CreateWorkItem(new()
        {
            Kind = "checkout", State = state, Title = "Own checkout", SessionId = "closed",
            RepositoryPath = "/workspace/checkout", NextAction = "Finish and push", DedupeKey = "checkout-1"
        });
        var other = service.CreateWorkItem(new() { Title = "Other checkout", State = "active", SessionId = "live" });
        service.PublishSessionStatus("closed", new() { Summary = "Own checkout", RepositoryPath = item.RepositoryPath });
        service.PublishSessionStatus("live", new() { Summary = "Other work" });
        service.CreateCheckpoint(new() { SessionId = "closed", Summary = "Saved progress" });

        service.ReleaseSession("closed");
        var released = service.GetWorkItems(null, null, null, null, 100).Items.Single(candidate => candidate.Id == item.Id);
        Assert.Equal(expectedState, released.State);
        Assert.Equal(expectedOwner, released.SessionId);
        Assert.Equal(item.RepositoryPath, released.RepositoryPath);
        Assert.Equal(item.NextAction, released.NextAction);
        Assert.Equal(item.DedupeKey, released.DedupeKey);
        Assert.Empty(service.GetSessionStatuses("closed").Statuses);
        Assert.Single(service.GetSessionStatuses("live").Statuses);
        Assert.Single(service.GetCheckpoints("closed", null, 100).Checkpoints);
        Assert.Equal(other.Revision, service.GetWorkItems(null, null, "live", null, 100).Items.Single().Revision);
        var events = service.GetEvents(0, 100).Events.Count;
        service.ReleaseSession("closed");
        Assert.Equal(events, service.GetEvents(0, 100).Events.Count);

        if (expectedOwner is null)
        {
            var reassigned = service.UpdateWorkItem(item.Id, new() { SessionId = "next", State = "active" })!;
            Assert.Equal("next", reassigned.SessionId);
        }
    }

    [Fact]
    public void RecoveryReleasesMissingOwnersButPreservesLiveSessionsAndPersistsRelease()
    {
        using (var service = new ControlPlaneService(new SettingsService(_directory)))
        {
            service.CreateWorkItem(new() { Title = "Old owner", SessionId = "missing", State = "active" });
            service.CreateWorkItem(new() { Title = "Preserved host", SessionId = "live", State = "active" });
            service.PublishSessionStatus("missing", new() { Summary = "Old ownership" });
            service.PublishSessionStatus("live", new() { Summary = "Current ownership" });
            service.ReleaseMissingSessions(id => id == "live");
        }
        using var reloaded = new ControlPlaneService(new SettingsService(_directory));
        var items = reloaded.GetWorkItems(null, null, null, null, 100).Items;
        Assert.Null(items.Single(item => item.Title == "Old owner").SessionId);
        Assert.Equal("open", items.Single(item => item.Title == "Old owner").State);
        Assert.Equal("live", items.Single(item => item.Title == "Preserved host").SessionId);
        Assert.Equal("active", items.Single(item => item.Title == "Preserved host").State);
        Assert.Equal("live", Assert.Single(reloaded.GetSessionStatuses(null).Statuses).SessionId);
    }

    [Fact]
    public void ExplicitReleaseClearsAssignmentAndPreservesCompletedRecord()
    {
        using var service = new ControlPlaneService(new SettingsService(_directory));
        var item = service.CreateWorkItem(new() { Title = "Pushed fix", State = "active", SessionId = "owner" });
        var released = service.UpdateWorkItem(item.Id, new() { State = "done", SessionId = "" })!;
        Assert.Equal("done", released.State);
        Assert.Null(released.SessionId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
