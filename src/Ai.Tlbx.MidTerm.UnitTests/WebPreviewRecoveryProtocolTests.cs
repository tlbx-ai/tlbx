using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Startup;
using Microsoft.AspNetCore.Http;
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

    [Fact]
    public void StorageSurvivesRestartAndSeparatesOriginsPreviewsSessionsAndExactTabs()
    {
        var state = new SessionControlStateService(_directory);
        var previews = CreatePreviews(state);
        Assert.True(previews.SetTarget("session", "canvas", "https://first.invalid/"));
        var preview = previews.GetPreviewSession("session", "canvas")!;
        var storage = new WebPreviewStorageService(state, previews);
        var first = storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://first.invalid", "profile:tab-a")!;
        Assert.True(storage.Apply(preview.RouteKey, Write(first.Token, 1,
            Set("local", "todos", "[1,2]"), Set("session", "draft", "tab-a"))));
        var sameTab = storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://first.invalid", "profile:tab-a")!;
        var otherTab = storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://first.invalid", "profile:tab-b")!;
        var otherOrigin = storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://second.invalid", "profile:tab-a")!;
        Assert.Equal("[1,2]", sameTab.Local["todos"]);
        Assert.Equal("tab-a", sameTab.Session["draft"]);
        Assert.Equal("[1,2]", otherTab.Local["todos"]);
        Assert.Empty(otherTab.Session);
        Assert.Empty(otherOrigin.Local);
        Assert.Empty(otherOrigin.Session);
        foreach (var (session, name) in new[] { ("other", "canvas"), ("session", "other") })
        {
            Assert.True(previews.SetTarget(session, name, "https://first.invalid/"));
            var separate = previews.GetPreviewSession(session, name)!;
            Assert.Empty(storage.CreateBootstrap(session, name, separate.RouteKey, separate.TargetRevision, "https://first.invalid", "profile:tab-a")!.Local);
        }

        var restartedState = new SessionControlStateService(_directory);
        var restartedPreviews = CreatePreviews(restartedState);
        restartedPreviews.RestoreSession("session");
        var restored = restartedPreviews.GetPreviewSession("session", "canvas")!;
        var restartedStorage = new WebPreviewStorageService(restartedState, restartedPreviews);
        var seeded = restartedStorage.CreateBootstrap("session", "canvas", restored.RouteKey, restored.TargetRevision, "https://first.invalid", "profile:tab-a")!;
        Assert.Equal("[1,2]", seeded.Local["todos"]);
        Assert.Equal("tab-a", seeded.Session["draft"]);
        Assert.NotEqual(first.Token, seeded.Token);
        Assert.False(restartedStorage.Apply(restored.RouteKey, Write(first.Token, 2, Set("local", "todos", "stale"))));
        Assert.DoesNotContain(first.Token, File.ReadAllText(Path.Combine(_directory, "session-control.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void StorageWriteOrderRetryQuotaAndRetargetAreAuthoritative()
    {
        var state = new SessionControlStateService(_directory);
        var previews = CreatePreviews(state);
        Assert.True(previews.SetTarget("session", "canvas", "https://first.invalid/"));
        var preview = previews.GetPreviewSession("session", "canvas")!;
        var storage = new WebPreviewStorageService(state, previews);
        var document = storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://first.invalid", "tab")!;
        var write = Write(document.Token, 1, Set("local", "todos", "original"));
        Assert.False(storage.Apply("wrong-route", write));
        Assert.False(storage.Apply(preview.RouteKey, Write(document.Token, 2, Set("local", "todos", "out-of-order"))));
        Assert.True(storage.Apply(preview.RouteKey, write));
        var sibling = storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://first.invalid", "tab")!;
        Assert.True(storage.Apply(preview.RouteKey, Write(sibling.Token, 1, Set("local", "todos", "newer"))));
        Assert.True(storage.Apply(preview.RouteKey, write)); // duplicate acknowledgement, no replay
        Assert.False(storage.Apply(preview.RouteKey, Write(document.Token, 1, Set("local", "todos", "changed duplicate"))));
        Assert.False(storage.Apply(preview.RouteKey, Write(document.Token, 2, Set("local", "todos", new string('x', WebPreviewStorageService.MaxUnits)))));
        Assert.Equal("newer", storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://first.invalid", "tab")!.Local["todos"]);
        Assert.True(storage.Apply(preview.RouteKey, Write(document.Token, 2,
            new PreviewStorageOperation { Store = "local", Action = "clear" },
            Set("local", "temporary", "remove"),
            new PreviewStorageOperation { Store = "local", Action = "remove", Key = "temporary" },
            Set("local", "todos", "after clear"))));
        Assert.Equal("after clear", storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://first.invalid", "tab")!.Local["todos"]);
        Assert.True(previews.SetTarget("session", "canvas", "https://second.invalid/"));
        Assert.False(storage.Apply(preview.RouteKey, Write(document.Token, 3, Set("local", "todos", "stale document"))));
        previews.ClearTarget("session", "canvas");
        Assert.False(storage.Apply(preview.RouteKey, Write(sibling.Token, 2, Set("local", "todos", "resurrect"))));
        Assert.Empty(state.GetPreviews("session"));
    }

    [Fact]
    public void StorageSeedRequiresRegisteredNavigationAndCannotBeFetchedByAnotherOrigin()
    {
        var state = new SessionControlStateService(_directory);
        var previews = CreatePreviews(state);
        Assert.True(previews.SetTarget("session", "canvas", "https://first.invalid/"));
        var preview = previews.GetPreviewSession("session", "canvas")!;
        var storage = new WebPreviewStorageService(state, previews);
        var context = new DefaultHttpContext();
        context.Items[PreviewProxyAuthorization.RegistrationItemKey] = new BrowserPreviewRegistration
        {
            SessionId = "session", PreviewName = "canvas", RouteKey = preview.RouteKey, BrowserId = "profile:tab"
        };
        var origin = new Uri("https://first.invalid/path");
        Assert.Null(storage.CreateBootstrap(context, preview.RouteKey, origin));
        context.Request.Headers["Sec-Fetch-Mode"] = "cors";
        context.Request.Headers["Sec-Fetch-Dest"] = "empty";
        Assert.Null(storage.CreateBootstrap(context, preview.RouteKey, origin));
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        context.Request.Headers["Sec-Fetch-Dest"] = "iframe";
        Assert.NotNull(storage.CreateBootstrap(context, preview.RouteKey, origin));
        context.Items.Remove(PreviewProxyAuthorization.RegistrationItemKey);
        Assert.Null(storage.CreateBootstrap(context, preview.RouteKey, origin));
    }

    [Fact]
    public void ReplacedBrowserAttachmentRetiresItsPriorDocumentWithoutEvictingOtherFrames()
    {
        var state = new SessionControlStateService(_directory);
        var previews = CreatePreviews(state);
        Assert.True(previews.SetTarget("session", "canvas", "https://first.invalid/"));
        var preview = previews.GetPreviewSession("session", "canvas")!;
        var storage = new WebPreviewStorageService(state, previews);
        var commands = new BrowserCommandService();
        var independent = storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision,
            "https://first.invalid", "profile:tab", "other-frame")!;
        storage.Attach(independent.Token, "other-frame", "profile:tab");
        string? oldToken = null;
        string? oldConnection = null;
        PreviewStorageBootstrap? current = null;
        for (var i = 0; i < 600; i++)
        {
            current = storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision,
                "https://first.invalid", "profile:tab", "same-attachment");
            Assert.NotNull(current);
            var connection = "connection-" + i;
            Assert.True(commands.TryRegisterClient(connection, "session", "canvas", "same-attachment", _ => { }, "profile:tab"));
            commands.AttachStorageForCurrentClient(connection, storage, current.Token);
            if (oldToken is not null)
            {
                // A delayed state message from the replaced connection cannot
                // retire the current document's lease.
                commands.AttachStorageForCurrentClient(oldConnection!, storage, oldToken);
                Assert.False(storage.Apply(preview.RouteKey, Write(oldToken, 1, Set("local", "old", "replaced"))));
            }
            oldToken = current.Token;
            oldConnection = connection;
        }
        Assert.True(storage.Apply(preview.RouteKey, Write(current!.Token, 1, Set("local", "current", "alive"))));
        Assert.True(storage.Apply(preview.RouteKey, Write(independent.Token, 1, Set("local", "other", "still active"))));
    }

    [Fact]
    public void StorageCapacityDoesNotEvictUnreplacedDocumentsAndPrunesObsoleteRevisions()
    {
        var state = new SessionControlStateService(_directory);
        var previews = CreatePreviews(state);
        Assert.True(previews.SetTarget("session", "canvas", "https://first.invalid/"));
        var preview = previews.GetPreviewSession("session", "canvas")!;
        var storage = new WebPreviewStorageService(state, previews);
        for (var i = 0; i < 512; i++)
            Assert.NotNull(storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://first.invalid", "tab"));
        Assert.Null(storage.CreateBootstrap("session", "canvas", preview.RouteKey, preview.TargetRevision, "https://first.invalid", "tab"));
        Assert.True(previews.SetTarget("session", "canvas", "https://second.invalid/"));
        var current = previews.GetPreviewSession("session", "canvas")!;
        Assert.NotNull(storage.CreateBootstrap("session", "canvas", current.RouteKey, current.TargetRevision, "https://second.invalid", "tab"));
        for (var i = 0; i < 511; i++)
            Assert.NotNull(storage.CreateBootstrap("session", "canvas", current.RouteKey, current.TargetRevision, "https://second.invalid", "tab"));
        Assert.True(previews.DeletePreviewSession("session", "canvas"));
        Assert.True(previews.SetTarget("session", "canvas", "https://third.invalid/"));
        var recreated = previews.GetPreviewSession("session", "canvas")!;
        Assert.NotNull(storage.CreateBootstrap("session", "canvas", recreated.RouteKey, recreated.TargetRevision, "https://third.invalid", "tab"));
        Assert.False(WebPreviewProxyMiddleware.IsBlockedRequestHeader("Sec-Fetch-Mode"));
        Assert.False(WebPreviewProxyMiddleware.IsBlockedRequestHeader("Sec-Fetch-Dest"));
        Assert.True(WebPreviewProxyMiddleware.IsBlockedRequestHeader("Sec-Fetch-Site"));
    }

    private static PreviewStorageOperation Set(string store, string key, string value) =>
        new() { Store = store, Action = "set", Key = key, Value = value };

    private static PreviewStorageWrite Write(string token, long sequence, params PreviewStorageOperation[] operations) =>
        new() { Token = token, Sequence = sequence, Operations = operations.ToList() };

    private static WebPreviewService CreatePreviews(SessionControlStateService store, BrowserPreviewOwnerService? owners = null) =>
        new(2000, BrowserPreviewOriginService.Create(2000), controlState: store, owners: owners);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
