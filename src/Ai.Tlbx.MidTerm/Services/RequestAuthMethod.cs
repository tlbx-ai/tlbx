namespace Ai.Tlbx.MidTerm.Services;

public enum RequestAuthMethod
{
    None = 0,
    SessionCookie = 1,
    ApiKey = 2,
    OpenAccess = 3
}

public readonly record struct RequestAuthentication(
    RequestAuthMethod Method,
    string? SessionTokenId = null,
    DateTimeOffset? ExpiresAtUtc = null);
