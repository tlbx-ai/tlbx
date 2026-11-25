using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Startup;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

// A small Web Storage bridge for opaque documents. No native-origin privileges,
// IndexedDB emulation or browser authentication is granted to upstream scripts.
public sealed class WebPreviewStorageService(SessionControlStateService state, WebPreviewService previews)
{
    internal const int MaxUnits = 64 * 1024;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    internal PreviewStorageBootstrap? CreateBootstrap(HttpContext context, string routeKey, Uri documentUri)
    {
        // Fetching another origin's HTML must not disclose that origin's seed.
        // These Fetch Metadata headers cannot be supplied by upstream JavaScript.
        var destination = context.Request.Headers["Sec-Fetch-Dest"].ToString();
        if (context.Request.Headers["Sec-Fetch-Mode"] != "navigate"
            || (destination != "iframe" && destination != "document")
            || documentUri.Scheme is not ("http" or "https")
            || context.Items[PreviewProxyAuthorization.RegistrationItemKey] is not BrowserPreviewRegistration registration
            || string.IsNullOrWhiteSpace(registration.BrowserId)
            || previews.GetPreviewSessionByRouteKey(routeKey) is not { Active: true } preview
            || registration.RouteKey != routeKey || registration.SessionId != preview.SessionId) return null;
        return CreateBootstrap(preview.SessionId, preview.PreviewName, routeKey, preview.TargetRevision,
            documentUri.GetLeftPart(UriPartial.Authority), registration.BrowserId, registration.PreviewId);
    }

    internal PreviewStorageBootstrap? CreateBootstrap(string sessionId, string previewName, string routeKey,
        long revision, string origin, string browserId, string previewId = "")
    {
        lock (_lock)
        {
            foreach (var key in _leases.Where(kv => IsObsolete(kv.Value)).Select(kv => kv.Key).ToArray()) _leases.Remove(key);
            // Active/unattached frames have no safe eviction signal. They can
            // still exhaust this bound (notably subframes without /ws/browser);
            // refuse a new seed rather than invalidate another live document.
            if (_leases.Count >= 512) return null;
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            // Length prefixes keep separators in origin/tab IDs unambiguous.
            var localKey = StorageKey(origin, "local", "");
            var sessionKey = StorageKey(origin, "session", browserId);
            _leases[token] = new Lease(sessionId, previewName, routeKey, revision, localKey, sessionKey, browserId, previewId);
            return new PreviewStorageBootstrap
            {
                Token = token,
                Local = state.ReadPreviewStorage(sessionId, previewName, localKey),
                Session = state.ReadPreviewStorage(sessionId, previewName, sessionKey)
            };
        }
    }

    // A current /ws/browser attachment is an explicit document-replacement
    // signal. Disconnect alone is not: the same document may reconnect later.
    internal void Attach(string? token, string? previewId, string? browserId)
    {
        lock (_lock)
        {
            if (token is null || string.IsNullOrWhiteSpace(previewId)
                || !_leases.TryGetValue(token, out var lease) || IsObsolete(lease)
                || lease.PreviewId != previewId || lease.BrowserId != browserId) return;
            foreach (var key in _leases.Where(kv => kv.Key != token && kv.Value.Attached
                && kv.Value.PreviewId == previewId && kv.Value.BrowserId == browserId
                && kv.Value.RouteKey == lease.RouteKey).Select(kv => kv.Key).ToArray()) _leases.Remove(key);
            lease.Attached = true;
        }
    }

    private bool IsObsolete(Lease lease) => lease.Expires < DateTimeOffset.UtcNow
        || previews.GetPreviewSessionByRouteKey(lease.RouteKey) is not { Active: true } preview
        || preview.TargetRevision != lease.Revision || preview.SessionId != lease.SessionId;

    internal bool Apply(string routeKey, PreviewStorageWrite request)
    {
        lock (_lock)
        {
            if (request.Token is null || !_leases.TryGetValue(request.Token, out var lease)
                || lease.RouteKey != routeKey || lease.Expires < DateTimeOffset.UtcNow
                || previews.GetPreviewSessionByRouteKey(routeKey) is not { Active: true } preview
                || preview.TargetRevision != lease.Revision || preview.SessionId != lease.SessionId
                || request.Operations is not { Count: <= 258 } operations || request.Sequence < 1) return false;
            // A lost acknowledgement may be retried without replaying a clear
            // over writes accepted from a newer document in the meantime.
            var writeHash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, PreviewStorageJsonContext.Default.PreviewStorageWrite));
            if (request.Sequence == lease.Sequence) return lease.WriteHash is not null && CryptographicOperations.FixedTimeEquals(lease.WriteHash, writeHash);
            if (request.Sequence != lease.Sequence + 1) return false;
            var updates = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var operation in operations)
            {
                if (operation is null || operation.Store is not ("local" or "session")) return false;
                var key = operation.Store == "local" ? lease.LocalKey : lease.SessionKey;
                if (!updates.TryGetValue(key, out var values))
                    updates[key] = values = state.ReadPreviewStorage(lease.SessionId, lease.PreviewName, key);
                switch (operation.Action)
                {
                    case "clear": values.Clear(); break;
                    case "remove" when operation.Key is not null: values.Remove(operation.Key); break;
                    case "set" when operation.Key is not null && operation.Value is not null:
                        values[operation.Key] = operation.Value;
                        break;
                    default: return false;
                }
                if (values.Count > 128 || values.Sum(kv => (long)kv.Key.Length + kv.Value.Length + 16) > MaxUnits) return false;
            }
            if (!state.WritePreviewStorage(lease.SessionId, lease.PreviewName, updates)) return false;
            lease.Sequence = request.Sequence;
            lease.WriteHash = writeHash;
            return true;
        }
    }

    private static string StorageKey(string origin, string kind, string tab) =>
        $"{origin.Length}:{origin}{kind.Length}:{kind}{tab}";

    private sealed class Lease(string sessionId, string previewName, string routeKey, long revision,
        string localKey, string sessionKey, string browserId, string previewId)
    {
        public string SessionId { get; } = sessionId;
        public string PreviewName { get; } = previewName;
        public string RouteKey { get; } = routeKey;
        public long Revision { get; } = revision;
        public string LocalKey { get; } = localKey;
        public string SessionKey { get; } = sessionKey;
        public string BrowserId { get; } = browserId;
        public string PreviewId { get; } = previewId;
        public bool Attached { get; set; }
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddHours(8);
        public long Sequence { get; set; }
        public byte[]? WriteHash { get; set; }
    }
}

internal sealed class PreviewStorageBootstrap
{
    public string Token { get; init; } = "";
    public Dictionary<string, string> Local { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Session { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class PreviewStorageWrite
{
    public string? Token { get; init; }
    public long Sequence { get; init; }
    public List<PreviewStorageOperation>? Operations { get; init; }
}

internal sealed class PreviewStorageOperation
{
    public string? Store { get; init; }
    public string? Action { get; init; }
    public string? Key { get; init; }
    public string? Value { get; init; }
}

[JsonSerializable(typeof(PreviewStorageBootstrap))]
[JsonSerializable(typeof(PreviewStorageWrite))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PreviewStorageJsonContext : JsonSerializerContext
{
}
