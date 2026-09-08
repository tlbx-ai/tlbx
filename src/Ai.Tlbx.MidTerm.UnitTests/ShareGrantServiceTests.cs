using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Share;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Share;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ShareGrantServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsService _settingsService;
    private readonly FakeTimeProvider _timeProvider;
    private readonly ShareGrantService _service;

    public ShareGrantServiceTests()
    {
        if (!OperatingSystem.IsWindows())
        {
            _tempDir = string.Empty;
            _settingsService = null!;
            _timeProvider = null!;
            _service = null!;
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"midterm_share_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsService = new SettingsService(_tempDir);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-10T12:00:00Z", CultureInfo.InvariantCulture));
        _service = new ShareGrantService(_settingsService, _timeProvider);
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

    [Fact]
    public void CreateGrant_HashesSecret_WithoutPersistingRawSecret()
    {
        if (!OperatingSystem.IsWindows()) return;

        var grant = _service.CreateGrant("session-1", ShareAccessMode.FullControl);

        var path = Path.Combine(_tempDir, "shared-links.json");
        Assert.True(File.Exists(path));

        var json = File.ReadAllText(path);
        Assert.DoesNotContain(grant.Secret, json, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var grants = doc.RootElement.GetProperty("grants");
        Assert.Equal(1, grants.GetArrayLength());

        var persisted = grants[0];
        Assert.Equal(grant.GrantId, persisted.GetProperty("grantId").GetString());
        Assert.Equal("session-1", persisted.GetProperty("sessionId").GetString());
        Assert.NotEqual(string.Empty, persisted.GetProperty("secretHash").GetString(), StringComparer.Ordinal);
    }

    [Fact]
    public void TryClaim_AndResolveCookie_Succeed_BeforeExpiry()
    {
        if (!OperatingSystem.IsWindows()) return;

        var grant = _service.CreateGrant("session-1", ShareAccessMode.ViewOnly);

        var claimed = _service.TryClaim(grant.GrantId, grant.Secret, out var access, out var cookieValue);

        Assert.True(claimed);
        Assert.Equal(grant.GrantId, access.GrantId);
        Assert.Equal("session-1", access.SessionId);
        Assert.Equal(ShareAccessMode.ViewOnly, access.Mode);
        Assert.Equal($"{grant.GrantId}.{grant.Secret}", cookieValue);

        var resolved = _service.TryResolveCookie(cookieValue, out var cookieAccess);

        Assert.True(resolved);
        Assert.Equal(access.GrantId, cookieAccess.GrantId);
        Assert.Equal(access.SessionId, cookieAccess.SessionId);
        Assert.Equal(access.Mode, cookieAccess.Mode);
    }

    [Fact]
    public void TryClaim_Fails_AfterExpiry()
    {
        if (!OperatingSystem.IsWindows()) return;

        var grant = _service.CreateGrant("session-1", ShareAccessMode.FullControl);

        _timeProvider.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        var claimed = _service.TryClaim(grant.GrantId, grant.Secret, out _, out _);

        Assert.False(claimed);
    }

    [Fact]
    public void CreateGrant_RevokesExistingGrant_ForSameSession()
    {
        if (!OperatingSystem.IsWindows()) return;

        var revokedGrantIds = new List<string>();
        _service.OnGrantRevoked += grantId => revokedGrantIds.Add(grantId);

        var first = _service.CreateGrant("session-1", ShareAccessMode.ViewOnly);
        var second = _service.CreateGrant("session-1", ShareAccessMode.FullControl);

        Assert.Contains(first.GrantId, revokedGrantIds, StringComparer.Ordinal);
        Assert.DoesNotContain(second.GrantId, revokedGrantIds, StringComparer.Ordinal);
        Assert.False(_service.TryClaim(first.GrantId, first.Secret, out _, out _));
        Assert.True(_service.TryClaim(second.GrantId, second.Secret, out var access, out _));
        Assert.Equal(ShareAccessMode.FullControl, access.Mode);
    }

    [Fact]
    public void RevokeBySession_InvalidatesActiveGrant()
    {
        if (!OperatingSystem.IsWindows()) return;

        var grant = _service.CreateGrant("session-1", ShareAccessMode.FullControl);

        _service.RevokeBySession("session-1");

        Assert.False(_service.TryClaim(grant.GrantId, grant.Secret, out _, out _));
    }

    [Fact]
    public void GetActiveGrants_ReturnsNewestActiveGrants_AndFiltersExpiredEntries()
    {
        if (!OperatingSystem.IsWindows()) return;

        _service.CreateGrant("session-1", ShareAccessMode.ViewOnly);
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        var second = _service.CreateGrant("session-2", ShareAccessMode.FullControl);
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        var third = _service.CreateGrant("session-3", ShareAccessMode.ViewOnly);

        var active = _service.GetActiveGrants(2);

        Assert.Collection(active,
            first =>
            {
                Assert.Equal(third.GrantId, first.GrantId);
                Assert.Equal("session-3", first.SessionId);
            },
            next =>
            {
                Assert.Equal(second.GrantId, next.GrantId);
                Assert.Equal("session-2", next.SessionId);
            });
    }

    [Fact]
    public void RevokeGrant_InvalidatesOnlyMatchingGrant()
    {
        if (!OperatingSystem.IsWindows()) return;

        var first = _service.CreateGrant("session-1", ShareAccessMode.FullControl);
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        var second = _service.CreateGrant("session-2", ShareAccessMode.ViewOnly);

        var revoked = _service.RevokeGrant(first.GrantId);

        Assert.True(revoked);
        Assert.False(_service.TryClaim(first.GrantId, first.Secret, out _, out _));
        Assert.True(_service.TryClaim(second.GrantId, second.Secret, out _, out _));
    }

    [Fact]
    public void CanWrite_OnlyAllowsFullControl()
    {
        var readOnlyAccess = new ShareAccessContext
        {
            GrantId = "grant-1",
            SessionId = "session-1",
            Mode = ShareAccessMode.ViewOnly,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        };
        var writableAccess = new ShareAccessContext
        {
            GrantId = "grant-2",
            SessionId = "session-1",
            Mode = ShareAccessMode.FullControl,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        };

        Assert.False(ShareGrantService.CanWrite(readOnlyAccess));
        Assert.True(ShareGrantService.CanWrite(writableAccess));
    }
}
