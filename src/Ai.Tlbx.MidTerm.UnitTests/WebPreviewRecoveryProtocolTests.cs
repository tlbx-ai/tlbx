using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewRecoveryProtocolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-preview-recovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RestartRestoresSeparateTargetsAndOfflineExactOwnersWithFreshRoutes()
    {
        var store = new SessionControlStateService(_directory);
        store.SetAgentControlled("surviving", true);
        var owners = new BrowserPreviewOwnerService(controlState: store);
        var previews = CreatePreviews(store, owners);
        Assert.True(previews.SetTarget("surviving", "canvas", "https://example.invalid/a"));
        Assert.True(previews.SetTarget("surviving", "docs", "https://example.invalid/b"));
        Assert.True(previews.SetTarget("other", "canvas", "https://other.invalid/"));
        owners.Claim("surviving", "canvas", "profile:offline-tab");
        owners.Claim("surviving", "docs", "profile:other-tab");
        var before = previews.GetPreviewSession("surviving", "canvas")!;
        var registry = new BrowserPreviewRegistry();
        var oldClient = registry.Create("surviving", "canvas", before.RouteKey, "profile:offline-tab");

        var reloaded = new SessionControlStateService(_directory);
        reloaded.PrunePreviewsToValidSessions(["surviving", "other"]);
        var restoredOwners = new BrowserPreviewOwnerService(controlState: reloaded);
        var restored = CreatePreviews(reloaded, restoredOwners);
        restoredOwners.RestoreSession("surviving");
        restored.RestoreSession("surviving");
        restored.RestoreSession("other");

        var after = restored.GetPreviewSession("surviving", "canvas")!;
        Assert.True(after.Active);
        Assert.Equal(before.Url, after.Url);
        Assert.Equal(before.TargetRevision + 1, after.TargetRevision);
        Assert.NotEqual(before.RouteKey, after.RouteKey);
        Assert.Null(restored.GetPreviewSessionByRouteKey(before.RouteKey));
        Assert.Equal("https://example.invalid/b", restored.GetTargetUrl("surviving", "docs"));
        Assert.Equal("https://other.invalid/", restored.GetTargetUrl("other", "canvas"));
        Assert.Equal("profile:offline-tab", restoredOwners.ResolveOwnerBrowserId("surviving", "canvas", ["new-profile:only-connected-tab"]));
        Assert.Equal("profile:other-tab", restoredOwners.GetOwnerBrowserId("surviving", "docs"));
        Assert.True(reloaded.IsAgentControlled("surviving"));
        var freshRegistry = new BrowserPreviewRegistry();
        var freshClient = freshRegistry.Create("surviving", "canvas", after.RouteKey, "profile:offline-tab");
        Assert.NotEqual(oldClient.PreviewToken, freshClient.PreviewToken);
        Assert.False(freshRegistry.TryValidate(oldClient.PreviewId, oldClient.PreviewToken, out _));
        Assert.True(freshRegistry.TryValidate(freshClient.PreviewId, freshClient.PreviewToken, out _));
        var persistedJson = File.ReadAllText(Path.Combine(_directory, "session-control.json"));
        Assert.DoesNotContain(oldClient.PreviewToken, persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(before.RouteKey, persistedJson, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_directory, "session-control.json.tmp")));
    }

    [Theory]
    [InlineData("default", false)]
    [InlineData("named", true)]
    public void ExplicitCloseDoesNotResurrectTargetOrOwner(string name, bool delete)
    {
        var store = new SessionControlStateService(_directory);
        var owners = new BrowserPreviewOwnerService(controlState: store);
        var previews = CreatePreviews(store, owners);
        Assert.True(previews.SetTarget("session", name, "https://example.invalid/"));
        owners.Claim("session", name, "profile:tab");
        if (delete) Assert.True(previews.DeletePreviewSession("session", name));
        else previews.ClearTarget("session", name);
        Assert.Null(owners.GetOwnerBrowserId("session", name));

        var reloaded = new SessionControlStateService(_directory);
        var restoredOwners = new BrowserPreviewOwnerService(controlState: reloaded);
        var restored = CreatePreviews(reloaded, restoredOwners);
        restoredOwners.RestoreSession("session");
        restored.RestoreSession("session");
        Assert.Empty(reloaded.GetPreviews("session"));
        Assert.Null(restored.GetPreviewSession("session", name));
        Assert.Null(restoredOwners.GetOwnerBrowserId("session", name));
    }

    [Fact]
    public void SessionRemovalAndStartupPruningKeepOnlySurvivingSessionState()
    {
        var store = new SessionControlStateService(_directory);
        foreach (var session in new[] { "alive", "closed", "missing" })
        {
            store.SetPreviewTarget(session, "canvas", "https://example.invalid/", null, 4);
            store.SetPreviewOwner(session, "canvas", "profile:tab");
        }
        store.RemoveSession("closed");
        var reloaded = new SessionControlStateService(_directory);
        reloaded.PrunePreviewsToValidSessions(["alive"]);
        var final = new SessionControlStateService(_directory);
        Assert.Single(final.GetPreviews("alive"));
        Assert.Empty(final.GetPreviews("closed"));
        Assert.Empty(final.GetPreviews("missing"));
    }

    [Fact]
    public void LocalDirectoryBoundaryAndCanonicalTargetSurviveRestart()
    {
        var store = new SessionControlStateService(_directory);
        var previews = CreatePreviews(store);
        var root = Path.GetFullPath(Path.Combine(_directory, "site"));
        var fileUrl = new Uri(Path.Combine(root, "index.html")).AbsoluteUri;
        Assert.True(previews.SetTarget("session", "local", fileUrl, localRoot: root));
        Assert.True(previews.SetTarget("session", "web", "https://old.invalid/docs/"));
        var revision = previews.GetPreviewSession("session", "web")!.TargetRevision;
        Assert.True(previews.SetTarget("session", "web", "https://canonical.invalid/docs/", preserveCookies: true, preserveTargetRevision: true));

        var restored = CreatePreviews(new SessionControlStateService(_directory));
        restored.RestoreSession("session");
        Assert.Equal(root, restored.GetLocalRootByRouteKey(restored.GetPreviewSession("session", "local")!.RouteKey));
        Assert.Equal(fileUrl, restored.GetTargetUrl("session", "local"));
        Assert.Equal("https://canonical.invalid/docs/", restored.GetTargetUrl("session", "web"));
        Assert.Equal(revision + 1, restored.GetPreviewSession("session", "web")!.TargetRevision);
    }

    [Fact]
    public void RestoreRevalidatesPersistedTargetsAndDoesNotOverwriteNewerRuntimeState()
    {
        var store = new SessionControlStateService(_directory);
        store.SetPreviewTarget("session", "recursive", "https://localhost:2000/webpreview/stale", null, 3);
        store.SetPreviewTarget("session", "remote-file", "file://remote.invalid/share/index.html", null, 3);
        store.SetPreviewTarget("session", "current", "https://old.invalid/", null, 3);
        var previews = CreatePreviews(store);
        Assert.True(previews.SetTarget("session", "current", "https://new.invalid/"));
        previews.RestoreSession("session");
        Assert.False(previews.IsActive("session", "recursive"));
        Assert.False(previews.IsActive("session", "remote-file"));
        Assert.Equal("https://new.invalid/", previews.GetTargetUrl("session", "current"));
    }

    [Fact]
    public void ClearingOneSessionKeepsOtherSessionsTargetsAndOwners()
    {
        var store = new SessionControlStateService(_directory);
        var owners = new BrowserPreviewOwnerService(controlState: store);
        var previews = CreatePreviews(store, owners);
        foreach (var session in new[] { "first", "second" })
        {
            Assert.True(previews.SetTarget(session, "canvas", "https://example.invalid/"));
            Assert.Equal("profile:" + session, owners.ResolveOwnerBrowserId(session, "canvas", ["profile:" + session]));
        }
        previews.ClearSession("first");
        Assert.Empty(new SessionControlStateService(_directory).GetPreviews("first"));
        Assert.Null(owners.GetOwnerBrowserId("first", "canvas"));
        Assert.Equal("profile:second", owners.GetOwnerBrowserId("second", "canvas"));
        Assert.Single(new SessionControlStateService(_directory).GetPreviews("second"));
    }

    private static WebPreviewService CreatePreviews(SessionControlStateService store, BrowserPreviewOwnerService? owners = null) =>
        new(2000, BrowserPreviewOriginService.Create(2000), controlState: store, owners: owners);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
