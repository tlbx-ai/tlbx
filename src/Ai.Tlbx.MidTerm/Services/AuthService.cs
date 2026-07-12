using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Ai.Tlbx.MidTerm.Models.Security;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services;

/// <summary>
/// Provides authentication services including password hashing, session token management, and rate limiting.
/// Uses PBKDF2 (100K iterations, SHA256) for password hashing and HMAC-SHA256 for session tokens.
/// </summary>
public sealed class AuthService
{
    public const string SessionCookieName = "mm-session";
    public const string AuthRequiredHeaderName = "X-MidTerm-Auth-Required";

    private const int Iterations = 100_000;
    private const int SaltSize = 32;
    private const int HashSize = 32;
    public const int SessionTokenValidityDays = 8;
    public static readonly TimeSpan SessionTokenValidity = TimeSpan.FromDays(SessionTokenValidityDays);

    private readonly SettingsService _settingsService;
    private readonly ApiKeyService _apiKeyService;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, RateLimitEntry> _rateLimits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedSessionTokens = new(StringComparer.Ordinal);
    private event Action<string>? SessionTokenRevoked;
    private event Action<string, DateTimeOffset>? SessionTokenRenewed;
    private event Action? AllSessionTokensInvalidated;

    public AuthService(
        SettingsService settingsService,
        ApiKeyService apiKeyService,
        TimeProvider? timeProvider = null)
    {
        _settingsService = settingsService;
        _apiKeyService = apiKeyService;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var settings = _settingsService.Load();
        var dirty = false;

        if (string.IsNullOrEmpty(settings.SessionSecret))
        {
            settings.SessionSecret = GenerateSessionSecret();
            dirty = true;
        }

        if (settings.PasswordHash is not null && settings.PasswordHash.StartsWith("__PENDING__:", StringComparison.Ordinal))
        {
            var pendingPassword = settings.PasswordHash["__PENDING__:".Length..];
            settings.PasswordHash = HashPassword(pendingPassword);
            dirty = true;
        }

        if (dirty)
        {
            _settingsService.Save(settings);
        }
    }

    public RequestAuthMethod AuthenticateRequest(HttpRequest request)
    {
        return AuthenticateRequestWithContext(request).Method;
    }

    public static void MarkAuthenticationRequired(HttpResponse response)
    {
        response.Headers[AuthRequiredHeaderName] = "true";
    }

    public RequestAuthentication AuthenticateRequestWithContext(HttpRequest request)
    {
        var settings = _settingsService.Load();
        if (!settings.AuthenticationEnabled || string.IsNullOrEmpty(settings.PasswordHash))
        {
            return new RequestAuthentication(RequestAuthMethod.OpenAccess);
        }

        var sessionToken = request.Cookies[SessionCookieName];
        if (sessionToken is not null &&
            TryValidateSessionToken(sessionToken, out var expiresAtUtc, out var sessionTokenId))
        {
            return new RequestAuthentication(
                RequestAuthMethod.SessionCookie,
                sessionTokenId,
                expiresAtUtc);
        }

        var apiKey = ExtractApiKey(request);
        if (apiKey is not null && _apiKeyService.TryValidateApiKey(apiKey, out _))
        {
            return new RequestAuthentication(RequestAuthMethod.ApiKey);
        }

        return new RequestAuthentication(RequestAuthMethod.None);
    }

    /// <summary>
    /// Hashes a password using PBKDF2 with a random salt.
    /// </summary>
    public string HashPassword(string password) => HashPasswordStatic(password);

    /// <summary>
    /// Static version for CLI use without needing full AuthService initialization.
    /// </summary>
    public static string HashPasswordStatic(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return $"$PBKDF2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 5 || parts[1] != "PBKDF2")
        {
            return false;
        }

        if (!int.TryParse(parts[2], CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expectedHash = Convert.FromBase64String(parts[4]);
        }
        catch
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    /// <summary>
    /// Creates a new HMAC-signed session token with an eight-day sliding validity window.
    /// </summary>
    public string CreateSessionToken()
    {
        return CreateSessionTokenCore(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
    }

    public string RenewSessionToken(string sessionTokenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionTokenId);
        return CreateSessionTokenCore(sessionTokenId);
    }

    private string CreateSessionTokenCore(string sessionTokenId)
    {
        var settings = _settingsService.Load();
        EnsureSessionSecret(settings);

        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var payload = string.Create(CultureInfo.InvariantCulture, $"{timestamp}:{sessionTokenId}");
        var signature = ComputeHmac(payload, settings.SessionSecret!);
        var expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp) + SessionTokenValidity;

        SessionTokenRenewed?.Invoke(sessionTokenId, expiresAtUtc);

        return string.Create(CultureInfo.InvariantCulture, $"{payload}:{signature}");
    }

    /// <summary>
    /// Validates a session token's signature and expiration.
    /// </summary>
    public bool ValidateSessionToken(string? token)
    {
        return TryValidateSessionToken(token, out _, out _);
    }

    public bool TryValidateSessionToken(string? token, out DateTimeOffset expiresAtUtc)
    {
        return TryValidateSessionToken(token, out expiresAtUtc, out _);
    }

    private bool TryValidateSessionToken(
        string? token,
        out DateTimeOffset expiresAtUtc,
        out string sessionTokenId)
    {
        expiresAtUtc = default;
        sessionTokenId = "";
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var parts = token.Split(':');
        if ((parts.Length != 2 && parts.Length != 3) ||
            !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var timestamp))
        {
            return false;
        }

        sessionTokenId = parts.Length == 3 ? parts[1] : GetLegacySessionTokenId(token);
        if (string.IsNullOrWhiteSpace(sessionTokenId))
        {
            return false;
        }

        DateTimeOffset tokenTime;
        try
        {
            tokenTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            expiresAtUtc = tokenTime + SessionTokenValidity;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        if (_timeProvider.GetUtcNow() > expiresAtUtc)
        {
            return false;
        }

        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.SessionSecret))
        {
            return false;
        }

        var signedPayload = parts.Length == 3
            ? string.Create(CultureInfo.InvariantCulture, $"{timestamp}:{sessionTokenId}")
            : timestamp.ToString(CultureInfo.InvariantCulture);
        var signature = parts[^1];
        var expectedSignature = ComputeHmac(signedPayload, settings.SessionSecret);
        var signatureValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature),
            Encoding.UTF8.GetBytes(expectedSignature));
        if (!signatureValid)
        {
            return false;
        }

        if (_revokedSessionTokens.TryGetValue(sessionTokenId, out var revokedUntil))
        {
            if (revokedUntil > _timeProvider.GetUtcNow())
            {
                return false;
            }

            _revokedSessionTokens.TryRemove(sessionTokenId, out _);
        }

        return true;
    }

    public void RevokeSessionToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var revoked in _revokedSessionTokens)
        {
            if (revoked.Value <= now)
            {
                _revokedSessionTokens.TryRemove(revoked.Key, out _);
            }
        }

        if (!TryValidateSessionToken(token, out var expiresAtUtc, out var sessionTokenId))
        {
            return;
        }

        _revokedSessionTokens[sessionTokenId] = expiresAtUtc;
        SessionTokenRevoked?.Invoke(sessionTokenId);
    }

    public IDisposable TrackWebSocketAuthentication(RequestAuthentication authentication, WebSocket webSocket)
    {
        if (authentication.Method != RequestAuthMethod.SessionCookie ||
            string.IsNullOrWhiteSpace(authentication.SessionTokenId) ||
            authentication.ExpiresAtUtc is null)
        {
            return NoopDisposable.Instance;
        }

        return new SessionWebSocketLease(
            this,
            webSocket,
            authentication.SessionTokenId,
            authentication.ExpiresAtUtc.Value,
            _timeProvider);
    }

    /// <summary>
    /// Checks if an IP address is currently rate-limited.
    /// </summary>
    public bool IsRateLimited(string ip)
    {
        if (!_rateLimits.TryGetValue(ip, out var entry))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow().DateTime > entry.BlockedUntil)
        {
            _rateLimits.TryRemove(ip, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records a failed login attempt. After 5 failures: 30s lockout. After 10: 5min lockout.
    /// </summary>
    public void RecordFailedAttempt(string ip)
    {
        var entry = _rateLimits.GetOrAdd(ip, _ => new RateLimitEntry());
        var attempts = Interlocked.Increment(ref entry.FailedAttempts);

        if (attempts >= 10)
        {
            entry.BlockedUntil = _timeProvider.GetUtcNow().DateTime.AddMinutes(5);
        }
        else if (attempts >= 5)
        {
            entry.BlockedUntil = _timeProvider.GetUtcNow().DateTime.AddSeconds(30);
        }
    }

    /// <summary>
    /// Clears all failed login attempts for an IP address.
    /// </summary>
    public void ResetAttempts(string ip)
    {
        _rateLimits.TryRemove(ip, out _);
    }

    /// <summary>
    /// Gets the remaining lockout time for an IP address, or null if not locked out.
    /// </summary>
    public TimeSpan? GetRemainingLockout(string ip)
    {
        if (!_rateLimits.TryGetValue(ip, out var entry))
        {
            return null;
        }

        var remaining = entry.BlockedUntil - _timeProvider.GetUtcNow().DateTime;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    /// <summary>
    /// Invalidates all existing sessions by rotating the session secret.
    /// </summary>
    public void InvalidateAllSessions()
    {
        var settings = _settingsService.Load();
        settings.SessionSecret = GenerateSessionSecret();
        _settingsService.Save(settings);
        _revokedSessionTokens.Clear();
        AllSessionTokensInvalidated?.Invoke();
    }

    private void EnsureSessionSecret(MidTermSettings settings)
    {
        if (string.IsNullOrEmpty(settings.SessionSecret))
        {
            settings.SessionSecret = GenerateSessionSecret();
            _settingsService.Save(settings);
        }
    }

    private static string GenerateSessionSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    private static string ComputeHmac(string data, string secret)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash);
    }

    private static string GetLegacySessionTokenId(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static string? ExtractApiKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString();
            const string prefix = "Bearer ";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var token = value[prefix.Length..].Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    return token;
                }
            }
        }

        if (request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader))
        {
            var token = apiKeyHeader.ToString().Trim();
            if (!string.IsNullOrEmpty(token))
            {
                return token;
            }
        }

        return null;
    }

    private sealed class RateLimitEntry
    {
        public int FailedAttempts;
        private long _blockedUntilTicks;

        public DateTime BlockedUntil
        {
            get => new(Interlocked.Read(ref _blockedUntilTicks));
            set => Interlocked.Exchange(ref _blockedUntilTicks, value.Ticks);
        }
    }

    private sealed class SessionWebSocketLease : IDisposable
    {
        private readonly AuthService _owner;
        private readonly WebSocket _webSocket;
        private readonly string _sessionTokenId;
        private readonly ITimer _expiryTimer;
        private int _closeStarted;
        private int _disposed;

        public SessionWebSocketLease(
            AuthService owner,
            WebSocket webSocket,
            string sessionTokenId,
            DateTimeOffset expiresAtUtc,
            TimeProvider timeProvider)
        {
            _owner = owner;
            _webSocket = webSocket;
            _sessionTokenId = sessionTokenId;
            _owner.SessionTokenRevoked += OnSessionTokenRevoked;
            _owner.SessionTokenRenewed += OnSessionTokenRenewed;
            _owner.AllSessionTokensInvalidated += OnAllSessionTokensInvalidated;

            var delay = expiresAtUtc - timeProvider.GetUtcNow();
            _expiryTimer = timeProvider.CreateTimer(
                static state => ((SessionWebSocketLease)state!).Expire("Authentication expired"),
                this,
                delay > TimeSpan.Zero ? delay : TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);
        }

        private void OnSessionTokenRevoked(string sessionTokenId)
        {
            if (string.Equals(_sessionTokenId, sessionTokenId, StringComparison.Ordinal))
            {
                Expire("Authentication revoked");
            }
        }

        private void OnSessionTokenRenewed(string sessionTokenId, DateTimeOffset expiresAtUtc)
        {
            if (!string.Equals(_sessionTokenId, sessionTokenId, StringComparison.Ordinal))
            {
                return;
            }

            var delay = expiresAtUtc - _owner._timeProvider.GetUtcNow();
            try
            {
                _expiryTimer.Change(
                    delay > TimeSpan.Zero ? delay : TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void OnAllSessionTokensInvalidated()
        {
            Expire("Authentication invalidated");
        }

        private void Expire(string reason)
        {
            if (Interlocked.Exchange(ref _closeStarted, 1) != 0)
            {
                return;
            }

            _ = CloseAuthenticationExpiredAsync(reason);
        }

        private async Task CloseAuthenticationExpiredAsync(string reason)
        {
            try
            {
                if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _webSocket.CloseOutputAsync(
                        (WebSocketCloseStatus)4401,
                        reason,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                try
                {
                    _webSocket.Abort();
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.SessionTokenRevoked -= OnSessionTokenRevoked;
            _owner.SessionTokenRenewed -= OnSessionTokenRenewed;
            _owner.AllSessionTokensInvalidated -= OnAllSessionTokensInvalidated;
            _expiryTimer.Dispose();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
