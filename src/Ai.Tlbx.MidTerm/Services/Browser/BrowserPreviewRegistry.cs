using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Ai.Tlbx.MidTerm.Models.Browser;

namespace Ai.Tlbx.MidTerm.Services.Browser;

public sealed class BrowserPreviewRegistry
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<string, RegisteredPreview> _previews = new(StringComparer.Ordinal);

    public BrowserPreviewClientResponse Create(
        string? sessionId,
        string? previewName,
        string? routeKey,
        string? browserId = null,
        long ownershipGeneration = 0)
    {
        CleanupExpired();

        var preview = new RegisteredPreview
        {
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            PreviewName = string.IsNullOrWhiteSpace(previewName) ? WebPreview.WebPreviewService.DefaultPreviewName : previewName,
            RouteKey = routeKey ?? "",
            PreviewId = Guid.NewGuid().ToString("N"),
            PreviewToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
            BrowserId = string.IsNullOrWhiteSpace(browserId) ? null : browserId,
            OwnershipGeneration = ownershipGeneration,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(PreviewLifetime)
        };

        _previews[preview.PreviewId] = preview;

        return new BrowserPreviewClientResponse
        {
            SessionId = preview.SessionId,
            PreviewName = preview.PreviewName,
            RouteKey = preview.RouteKey,
            PreviewId = preview.PreviewId,
            OwnershipGeneration = preview.OwnershipGeneration,
            PreviewToken = preview.PreviewToken
        };
    }

    public bool TryValidate(
        string? previewId,
        string? previewToken,
        [NotNullWhen(true)] out BrowserPreviewRegistration? preview)
    {
        CleanupExpired();
        preview = null;

        if (string.IsNullOrWhiteSpace(previewId) || string.IsNullOrWhiteSpace(previewToken))
            return false;

        if (!_previews.TryGetValue(previewId, out var registered))
            return false;

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(registered.PreviewToken), Encoding.UTF8.GetBytes(previewToken)))
            return false;

        preview = new BrowserPreviewRegistration
        {
            SessionId = registered.SessionId,
            PreviewName = registered.PreviewName,
            RouteKey = registered.RouteKey,
            PreviewId = registered.PreviewId,
            OwnershipGeneration = registered.OwnershipGeneration,
            PreviewToken = registered.PreviewToken,
            BrowserId = registered.BrowserId
        };
        return true;
    }

    public int Remove(string sessionId, string? previewName = null)
    {
        var normalizedPreviewName = string.IsNullOrWhiteSpace(previewName)
            ? WebPreview.WebPreviewService.DefaultPreviewName
            : previewName;
        return RemoveMatching(entry =>
            string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)
            && string.Equals(entry.PreviewName, normalizedPreviewName, StringComparison.Ordinal));
    }

    public int ClearSession(string sessionId)
    {
        return RemoveMatching(entry => string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal));
    }

    private int RemoveMatching(Func<RegisteredPreview, bool> predicate)
    {
        var removed = 0;
        foreach (var entry in _previews)
        {
            if (predicate(entry.Value) && _previews.TryRemove(entry.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _previews)
        {
            if (entry.Value.ExpiresAtUtc <= now)
            {
                _previews.TryRemove(entry.Key, out _);
            }
        }
    }

    private sealed class RegisteredPreview
    {
        public string? SessionId { get; init; }
        public string PreviewName { get; init; } = WebPreview.WebPreviewService.DefaultPreviewName;
        public string RouteKey { get; init; } = "";
        public long OwnershipGeneration { get; init; }
        public string PreviewId { get; init; } = "";
        public string PreviewToken { get; init; } = "";
        public string? BrowserId { get; init; }
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }
}
