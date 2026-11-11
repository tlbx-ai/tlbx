using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Share;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Share;

internal sealed class ShareGrantStoreFile
{
    public List<ShareGrantRecord> Grants { get; set; } = [];
}

internal sealed class ShareGrantRecord
{
    public string GrantId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public ShareAccessMode Mode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string SecretHash { get; set; } = "";
}

public sealed class ShareGrantIssueResult
{
    public string GrantId { get; init; } = "";
    public string Secret { get; init; } = "";
    public string SessionId { get; init; } = "";
    public ShareAccessMode Mode { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed class ShareGrantService
{
    public const string ShareCookieName = "mm-share-session";

    private readonly string _storagePath;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    public event Action<string>? OnGrantRevoked;

    public ShareGrantService(SettingsService settingsService, TimeProvider? timeProvider = null)
    {
        _storagePath = Path.Combine(settingsService.SettingsDirectory, "shared-links.json");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ShareGrantIssueResult CreateGrant(string sessionId, ShareAccessMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAtUtc = now.AddHours(1);
        var grantId = Guid.NewGuid().ToString("N");
        var secret = CreateSecret();
        var record = new ShareGrantRecord
        {
            GrantId = grantId,
            SessionId = sessionId,
            Mode = mode,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc,
            SecretHash = ComputeSecretHash(secret)
        };

        List<string> revokedGrantIds = [];

        lock (_lock)
        {
            var store = LoadStoreNoLock();
            CleanupExpiredNoLock(store, now);

            foreach (var existing in store.Grants)
            {
                if (existing.RevokedAtUtc is null
                    && string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal))
                {
                    existing.RevokedAtUtc = now;
                    revokedGrantIds.Add(existing.GrantId);
                }
            }

            store.Grants.Add(record);
            SaveStoreNoLock(store);
        }

        foreach (var revokedGrantId in revokedGrantIds)
        {
            OnGrantRevoked?.Invoke(revokedGrantId);
        }

        return new ShareGrantIssueResult
        {
            GrantId = grantId,
            Secret = secret,
            SessionId = sessionId,
            Mode = mode,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public bool TryClaim(string grantId, string secret, out ShareAccessContext access, out string cookieValue)
    {
        access = null!;
        cookieValue = "";

        if (string.IsNullOrWhiteSpace(grantId) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        if (!TryResolveGrant(grantId, secret, out access))
        {
            return false;
        }

        cookieValue = $"{grantId}.{secret}";
        return true;
    }

    public bool TryResolveCookie(string? cookieValue, out ShareAccessContext access)
    {
        access = null!;

        if (string.IsNullOrWhiteSpace(cookieValue))
        {
            return false;
        }

        var separator = cookieValue.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == cookieValue.Length - 1)
        {
            return false;
        }

        var grantId = cookieValue[..separator];
        var secret = cookieValue[(separator + 1)..];
        return TryResolveGrant(grantId, secret, out access);
    }

    public IReadOnlyList<ShareGrantSummary> GetActiveGrants(int maxCount = int.MaxValue)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        lock (_lock)
        {
            var store = LoadStoreNoLock();
            CleanupExpiredNoLock(store, now);

            var grants = store.Grants
                .Where(grant => grant.RevokedAtUtc is null && grant.ExpiresAtUtc > now)
                .OrderByDescending(grant => grant.CreatedAtUtc)
                .Take(maxCount)
                .Select(grant => new ShareGrantSummary
                {
                    GrantId = grant.GrantId,
                    SessionId = grant.SessionId,
                    Mode = grant.Mode,
                    CreatedAtUtc = grant.CreatedAtUtc,
                    ExpiresAtUtc = grant.ExpiresAtUtc
                })
                .ToArray();

            SaveStoreNoLock(store);
            return grants;
        }
    }

    public void RevokeBySession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        List<string> revokedGrantIds = [];
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        lock (_lock)
        {
            var store = LoadStoreNoLock();
            CleanupExpiredNoLock(store, now);

            foreach (var grant in store.Grants)
            {
                if (grant.RevokedAtUtc is null
                    && string.Equals(grant.SessionId, sessionId, StringComparison.Ordinal))
                {
                    grant.RevokedAtUtc = now;
                    revokedGrantIds.Add(grant.GrantId);
                }
            }

            if (revokedGrantIds.Count > 0)
            {
                SaveStoreNoLock(store);
            }
        }

        foreach (var revokedGrantId in revokedGrantIds)
        {
            OnGrantRevoked?.Invoke(revokedGrantId);
        }
    }

    public bool RevokeGrant(string grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId))
        {
            return false;
        }

        string? revokedGrantId = null;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        lock (_lock)
        {
            var store = LoadStoreNoLock();
            CleanupExpiredNoLock(store, now);

            var grant = store.Grants.FirstOrDefault(existing =>
                existing.RevokedAtUtc is null &&
                string.Equals(existing.GrantId, grantId, StringComparison.Ordinal));

            if (grant is null)
            {
                SaveStoreNoLock(store);
                return false;
            }

            grant.RevokedAtUtc = now;
            revokedGrantId = grant.GrantId;
            SaveStoreNoLock(store);
        }

        OnGrantRevoked?.Invoke(revokedGrantId);
        return true;
    }

    public static bool CanWrite(ShareAccessContext access)
    {
        return access.Mode == ShareAccessMode.FullControl;
    }

    private bool TryResolveGrant(string grantId, string secret, out ShareAccessContext access)
    {
        access = null!;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var secretHash = ComputeSecretHash(secret);

        lock (_lock)
        {
            var store = LoadStoreNoLock();
            CleanupExpiredNoLock(store, now);

            var grant = store.Grants.FirstOrDefault(g =>
                string.Equals(g.GrantId, grantId, StringComparison.Ordinal));

            if (grant is null || grant.RevokedAtUtc is not null || grant.ExpiresAtUtc <= now)
            {
                SaveStoreNoLock(store);
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(secretHash),
                    Encoding.UTF8.GetBytes(grant.SecretHash)))
            {
                SaveStoreNoLock(store);
                return false;
            }

            SaveStoreNoLock(store);
            access = new ShareAccessContext
            {
                GrantId = grant.GrantId,
                SessionId = grant.SessionId,
                Mode = grant.Mode,
                ExpiresAtUtc = grant.ExpiresAtUtc
            };
            return true;
        }
    }

    private ShareGrantStoreFile LoadStoreNoLock()
    {
        if (!File.Exists(_storagePath))
        {
            return new ShareGrantStoreFile();
        }

        try
        {
            var json = File.ReadAllText(_storagePath);
            return JsonSerializer.Deserialize(json, ShareJsonContext.Default.ShareGrantStoreFile)
                ?? new ShareGrantStoreFile();
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to load share grant store: {ex.Message}");
            return new ShareGrantStoreFile();
        }
    }

    private void SaveStoreNoLock(ShareGrantStoreFile store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            var json = JsonSerializer.Serialize(store, ShareJsonContext.Default.ShareGrantStoreFile);
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to save share grant store: {ex.Message}");
        }
    }

    private static void CleanupExpiredNoLock(ShareGrantStoreFile store, DateTime nowUtc)
    {
        store.Grants.RemoveAll(grant =>
            grant.ExpiresAtUtc <= nowUtc || grant.RevokedAtUtc is not null);
    }

    private static string CreateSecret()
    {
        return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    }

    private static string ComputeSecretHash(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes);
    }

}
