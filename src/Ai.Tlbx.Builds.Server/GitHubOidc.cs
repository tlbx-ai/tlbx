using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Ai.Tlbx.Builds.Server;

public interface IPublisherAuthenticator
{
    Task<PublisherIdentity?> AuthenticateAsync(string token, ReleaseIdentity release, CancellationToken cancellationToken);
}

public sealed class GitHubOidc(BuildOptions options, HttpClient http, TimeProvider clock) : IPublisherAuthenticator, IDisposable
{
    private const string Issuer = "https://token.actions.githubusercontent.com";
    private sealed record KeyCache(Dictionary<string, RSAParameters> Keys, DateTimeOffset LoadedAt);
    private readonly object _refreshLock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private KeyCache _cache = new(new(StringComparer.Ordinal), DateTimeOffset.MinValue);
    private Task? _refreshTask;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private bool _disposed;
    public void Dispose()
    {
        lock (_refreshLock)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }
    public async Task<PublisherIdentity?> AuthenticateAsync(string token, ReleaseIdentity release, CancellationToken cancellationToken)
    {
        try
        {
            if (token.Length is < 32 or > 16384) return null;
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            using var header = JsonDocument.Parse(Decode(parts[0]));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            if (!Unique(header.RootElement) || !Unique(payload.RootElement)) return null;
            var h = header.RootElement; var p = payload.RootElement;
            if (Text(h, "alg") != "RS256" || Text(h, "typ") != "JWT") return null;
            var kid = Text(h, "kid");
            if (kid.Length is < 1 or > 128 || !kid.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
            var now = clock.GetUtcNow();
            var expiry = DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("exp").GetInt64());
            var notBefore = DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("nbf").GetInt64());
            var issued = DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("iat").GetInt64());
            if (expiry < now.AddSeconds(-30) || notBefore > now.AddSeconds(30) || issued > now.AddSeconds(30) ||
                expiry <= issued || expiry - issued > TimeSpan.FromMinutes(15) || now - issued > TimeSpan.FromMinutes(15)) return null;
            if (Text(p, "iss") != Issuer || Text(p, "aud") != options.PublicOrigin.GetLeftPart(UriPartial.Authority) ||
                Text(p, "repository_id") != options.RepositoryId || Text(p, "repository_owner_id") != options.RepositoryOwnerId ||
                Text(p, "repository") != "tlbx-ai/tlbx") return null;
            var workflow = Text(p, "workflow_ref");
            var backfill = workflow == "tlbx-ai/tlbx/.github/workflows/mirror-builds.yml@refs/heads/dev" &&
                Text(p, "ref") == "refs/heads/dev" && Text(p, "event_name") is "workflow_dispatch" or "push";
            var normal = workflow == $"tlbx-ai/tlbx/.github/workflows/release.yml@refs/tags/{release.Tag}" &&
                Text(p, "ref") == $"refs/tags/{release.Tag}" && Text(p, "event_name") == "push";
            if (!normal && !backfill) return null;
            var sha = Text(p, "sha");
            if (sha.Length != 40 || !sha.All(char.IsAsciiHexDigit)) return null;
            await EnsureKeyAsync(kid, now, cancellationToken);
            var cache = Volatile.Read(ref _cache);
            now = clock.GetUtcNow();
            if (expiry < now.AddSeconds(-30) || now - cache.LoadedAt > TimeSpan.FromHours(1) || !cache.Keys.TryGetValue(kid, out var key)) return null;
            using var rsa = RSA.Create();
            rsa.ImportParameters(key);
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return null;
            return new(sha.ToLowerInvariant(), backfill);
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or InvalidOperationException or KeyNotFoundException or ArgumentException or HttpRequestException or IOException or OperationCanceledException)
        { return null; }
    }

    private async Task EnsureKeyAsync(string kid, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Task refresh;
        lock (_refreshLock)
        {
            var cache = Volatile.Read(ref _cache);
            if (_disposed || (cache.Keys.ContainsKey(kid) && now - cache.LoadedAt < TimeSpan.FromMinutes(30))) return;
            if (_refreshTask is { IsCompleted: false }) refresh = _refreshTask;
            else
            {
                if (now - _lastAttempt < TimeSpan.FromSeconds(30)) return;
                _lastAttempt = now;
                refresh = _refreshTask = RefreshKeysAsync();
            }
        }
        // The unverified caller may stop waiting, but cannot cancel the shared
        // issuer refresh or poison its cooldown for subsequent valid publishers.
        await refresh.WaitAsync(cancellationToken);
    }
    private async Task RefreshKeysAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var cancellationToken = deadline.Token;
        try
        {
            using var response = await http.GetAsync(Issuer + "/.well-known/jwks", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 1024 * 1024) return;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bytes = new MemoryStream();
            var buffer = new byte[16384]; int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (bytes.Length + read > 1024 * 1024) return;
                bytes.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(bytes.ToArray());
            var items = document.RootElement.GetProperty("keys");
            if (items.GetArrayLength() is < 1 or > 32) return;
            var result = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
            using var entries = items.EnumerateArray();
            foreach (var item in entries)
            {
                if (Text(item, "kty") != "RSA" || (item.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256")) continue;
                var modulus = Decode(Text(item, "n")); var exponent = Decode(Text(item, "e"));
                if (modulus.Length is < 256 or > 512 || exponent.Length is < 1 or > 8) continue;
                var id = Text(item, "kid");
                if (id.Length is < 1 or > 128 || !result.TryAdd(id, new RSAParameters { Modulus = modulus, Exponent = exponent })) return;
            }
            Volatile.Write(ref _cache, new(result, clock.GetUtcNow()));
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or InvalidOperationException or KeyNotFoundException or ArgumentException or HttpRequestException or IOException or OperationCanceledException)
        {
            // A provider failure is shared by waiters and bounded by our own
            // deadline. No detached task is left faulted or without a timeout.
        }
    }
    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";
    private static bool Unique(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var properties = element.EnumerateObject();
        while (properties.MoveNext()) if (!names.Add(properties.Current.Name)) return false;
        return true;
    }
    private static byte[] Decode(string text)
    {
        if (!text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new FormatException("Invalid base64url");
        return Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
    }
}
