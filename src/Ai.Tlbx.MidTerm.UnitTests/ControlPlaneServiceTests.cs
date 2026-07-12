using Ai.Tlbx.MidTerm.Models.ControlPlane;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Settings;
using System.Globalization;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ControlPlaneServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ControlPlaneServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"midterm_control_plane_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CreatesIdempotentWorkItemsAndAppliesExplicitUpdates()
    {
        using var service = CreateService();
        var request = new CreateControlPlaneWorkItemRequest
        {
            Kind = "mail",
            Title = "Answer customer thread",
            Summary = "The customer asked for the rollout date.",
            NextAction = "Draft the answer for Johannes.",
            Priority = ControlPlanePriorities.High,
            SessionId = "session-a",
            Source = "jpa",
            DedupeKey = "gmail:thread-42"
        };

        var created = service.CreateWorkItem(request);
        var duplicate = service.CreateWorkItem(request);
        var updated = service.UpdateWorkItem(created.Id, new UpdateControlPlaneWorkItemRequest
        {
            State = ControlPlaneWorkItemStates.Active,
            NextAction = "Wait for Johannes' approval."
        });

        Assert.Equal(created.Id, duplicate.Id);
        Assert.NotNull(updated);
        Assert.Equal(ControlPlaneWorkItemStates.Active, updated!.State);
        Assert.Equal("Wait for Johannes' approval.", updated.NextAction);
        Assert.Equal(2, updated.Revision);
        Assert.Single(service.GetWorkItems(null, null, null, null, 100).Items);
    }

    [Fact]
    public void StoresOnlyAgentPublishedSessionMeaning()
    {
        using var service = CreateService();

        var published = service.PublishSessionStatus("session-a", new PublishControlPlaneSessionStatusRequest
        {
            State = ControlPlaneSessionStates.NeedsInput,
            Summary = "Implementation is verified; release choice is needed.",
            CurrentTask = "Prepare the release",
            NextAction = "Johannes chooses dev or stable.",
            Source = "codex"
        });
        service.CreateCheckpoint(new CreateControlPlaneCheckpointRequest
        {
            SessionId = "session-a",
            Kind = "verified",
            Summary = "Focused tests pass",
            Details = "14 backend and 5 frontend tests",
            Source = "codex"
        });

        var status = Assert.Single(service.GetSessionStatuses("session-a").Statuses);
        var checkpoint = Assert.Single(service.GetCheckpoints("session-a", "verified", 10).Checkpoints);
        Assert.Equal(published.Revision, status.Revision);
        Assert.Equal(ControlPlaneSessionStates.NeedsInput, status.State);
        Assert.Equal("Focused tests pass", checkpoint.Summary);
    }

    [Fact]
    public void PersistsAndBoundsEveryCollection()
    {
        using (var service = CreateService())
        {
            for (var index = 0; index < ControlPlaneService.MaxWorkItems + 20; index++)
            {
                service.CreateWorkItem(new CreateControlPlaneWorkItemRequest
                {
                    Title = $"work-{index.ToString(CultureInfo.InvariantCulture)}",
                    Source = "test"
                });
            }
            for (var index = 0; index < ControlPlaneService.MaxSessionStatuses + 20; index++)
            {
                service.PublishSessionStatus($"session-{index.ToString(CultureInfo.InvariantCulture)}", new PublishControlPlaneSessionStatusRequest
                {
                    Summary = $"status-{index.ToString(CultureInfo.InvariantCulture)}",
                    Source = "test"
                });
            }
            for (var index = 0; index < ControlPlaneService.MaxCheckpoints + 20; index++)
            {
                service.CreateCheckpoint(new CreateControlPlaneCheckpointRequest
                {
                    SessionId = "session-a",
                    Summary = $"checkpoint-{index.ToString(CultureInfo.InvariantCulture)}",
                    Source = "test"
                });
            }
        }

        using var reloaded = CreateService();
        Assert.Equal(ControlPlaneService.MaxWorkItems, reloaded.GetWorkItems(null, null, null, null, 1000).TotalCount);
        Assert.Equal(ControlPlaneService.MaxSessionStatuses, reloaded.GetSessionStatuses(null).Statuses.Count);
        Assert.Equal(ControlPlaneService.MaxCheckpoints, reloaded.GetCheckpoints(null, null, 1000).TotalCount);
        Assert.Equal(ControlPlaneService.MaxEvents, reloaded.GetEvents(0, 1000).Events.Count);
    }

    [Fact]
    public void RejectsUnknownSemanticStatesInsteadOfGuessing()
    {
        using var service = CreateService();

        Assert.Throws<ArgumentException>(() => service.PublishSessionStatus(
            "session-a",
            new PublishControlPlaneSessionStatusRequest
            {
                State = "probablyBusy",
                Summary = "This must not be inferred."
            }));
        Assert.Throws<ArgumentException>(() => service.CreateWorkItem(new CreateControlPlaneWorkItemRequest
        {
            State = "maybeDone",
            Title = "Ambiguous state"
        }));
    }

    [Fact]
    public void EmitsOrderedEventsOnlyForExplicitMutations()
    {
        using var service = CreateService();
        var created = service.CreateWorkItem(new CreateControlPlaneWorkItemRequest
        {
            Title = "Verify release",
            DedupeKey = "release:verify",
            Source = "codex"
        });
        service.CreateWorkItem(new CreateControlPlaneWorkItemRequest
        {
            Title = "Verify release",
            DedupeKey = "release:verify",
            Source = "codex"
        });
        service.UpdateWorkItem(created.Id, new UpdateControlPlaneWorkItemRequest
        {
            State = ControlPlaneWorkItemStates.Done,
            Source = "codex"
        });
        service.PublishSessionStatus("session-a", new PublishControlPlaneSessionStatusRequest
        {
            State = ControlPlaneSessionStates.NeedsInput,
            Summary = "Choose the release channel.",
            Source = "codex"
        });
        service.ClearSessionStatus("session-a");
        service.RemoveWorkItem(created.Id);

        var events = service.GetEvents(0, 100);
        Assert.Equal(5, events.Events.Count);
        Assert.Equal(ControlPlaneEventTypes.WorkItemCreated, events.Events[0].Type);
        Assert.Equal(ControlPlaneEventTypes.WorkItemUpdated, events.Events[1].Type);
        Assert.Equal(ControlPlaneEventTypes.SessionStatusPublished, events.Events[2].Type);
        Assert.Equal(ControlPlaneSessionStates.NeedsInput, events.Events[2].State);
        Assert.Equal(ControlPlaneEventTypes.SessionStatusCleared, events.Events[3].Type);
        Assert.Equal(ControlPlaneEventTypes.WorkItemDeleted, events.Events[4].Type);
        Assert.Equal(events.Events[^1].Sequence, events.LatestSequence);
        Assert.Equal(3, service.GetEvents(events.Events[1].Sequence, 100).Events.Count);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private ControlPlaneService CreateService() => new(new SettingsService(_tempDir));
}
