using System.Security.Cryptography;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Short, single-use reauthentication grants bound to the current local cookie session.</summary>
public sealed class ConnectProvisioningGate(SettingsService settings, AuthService auth, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, UnlockGrant> _unlocks = new(StringComparer.Ordinal);

    internal int Unlock(HttpContext context, string? password, out ConnectUnlockResponse? response)
    {
        response = null;
        var authentication = auth.AuthenticateRequestWithContext(context.Request);
        if (authentication.Method != RequestAuthMethod.SessionCookie || authentication.SessionTokenId is null) return 401;
        const string rateKey = "connect-local-provisioning";
        if (auth.IsRateLimited(rateKey)) return 429;
        using var operation = auth.TryBeginPasswordOperation();
        if (operation is null) return 429;
        if (password is null || !auth.VerifyPassword(password, settings.Load().PasswordHash))
        {
            auth.RecordFailedAttempt(rateKey);
            return 401;
        }
        auth.ResetAttempts(rateKey);
        var token = ConnectKeyProtocol.Encode(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            foreach (var expired in _unlocks.Where(pair => pair.Value.ExpiresAt <= _time.GetUtcNow()).Select(pair => pair.Key).ToArray()) _unlocks.Remove(expired);
            if (_unlocks.Count >= 64) return 429;
            _unlocks[authentication.SessionTokenId] = new UnlockGrant(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)), _time.GetUtcNow().AddSeconds(120));
        }
        response = new ConnectUnlockResponse(token, 120);
        return 200;
    }

    internal bool Consume(HttpContext context, string? token)
    {
        var authentication = auth.AuthenticateRequestWithContext(context.Request);
        if (authentication.Method != RequestAuthMethod.SessionCookie || authentication.SessionTokenId is null || token is null || token.Length > 128) return false;
        lock (_gate)
        {
            if (!_unlocks.TryGetValue(authentication.SessionTokenId, out var unlock) || unlock.ExpiresAt <= _time.GetUtcNow()) return false;
            if (!CryptographicOperations.FixedTimeEquals(unlock.Hash, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))) return false;
            _unlocks.Remove(authentication.SessionTokenId);
            return true;
        }
    }

    private sealed record UnlockGrant(byte[] Hash, DateTimeOffset ExpiresAt);
}
