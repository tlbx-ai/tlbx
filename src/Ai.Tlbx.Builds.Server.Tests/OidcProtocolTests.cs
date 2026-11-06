using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.Builds.Server;
using Xunit;
namespace Ai.Tlbx.Builds.Server.Tests;

public sealed class OidcProtocolTests
{
    [Fact]
    public async Task ValidRs256TokenBindsImmutableRepositoryTagWorkflowAndCommit()
    {
        using var f = new Fixture();
        var identity = await f.Auth.AuthenticateAsync(f.Token(), ReleaseIdentity.Parse("v1.2.3-dev"), default);
        Assert.NotNull(identity); Assert.False(identity.IsBackfill); Assert.Equal(new string('a', 40), identity.Sha);
        Assert.Null(await f.Auth.AuthenticateAsync(f.Token(), ReleaseIdentity.Parse("v1.2.4-dev"), default));
        Assert.Equal(1, f.Handler.Requests);
    }
    [Theory]
    [InlineData("iss", "https://attacker.invalid")]
    [InlineData("aud", "https://wrong.invalid")]
    [InlineData("repository_id", "999")]
    [InlineData("repository_owner_id", "999")]
    [InlineData("repository", "other/tlbx")]
    [InlineData("ref", "refs/heads/dev")]
    [InlineData("workflow_ref", "tlbx-ai/tlbx/.github/workflows/other.yml@refs/tags/v1.2.3-dev")]
    [InlineData("event_name", "pull_request")]
    public async Task WrongClaimsNeverAuthenticate(string claim, string value)
    {
        using var f = new Fixture(); var claims = f.Claims(); claims[claim] = value;
        Assert.Null(await f.Auth.AuthenticateAsync(f.Token(claims), ReleaseIdentity.Parse("v1.2.3-dev"), default));
        Assert.Equal(0, f.Handler.Requests);
    }
    [Fact]
    public async Task ExpiredFutureAndBadSignatureTokensNeverAuthenticateAndBackfillIsExplicit()
    {
        using var f = new Fixture(); var release = ReleaseIdentity.Parse("v1.2.3-dev");
        var expired = f.Claims(); expired["exp"] = f.Clock.Now.AddSeconds(-31).ToUnixTimeSeconds();
        Assert.Null(await f.Auth.AuthenticateAsync(f.Token(expired), release, default));
        var future = f.Claims(); future["nbf"] = f.Clock.Now.AddSeconds(31).ToUnixTimeSeconds();
        Assert.Null(await f.Auth.AuthenticateAsync(f.Token(future), release, default));
        var token = f.Token(); var split = token.Split('.'); split[2] = Base64(new byte[256]);
        Assert.Null(await f.Auth.AuthenticateAsync(string.Join('.', split), release, default));
        var backfill = f.Claims(); backfill["workflow_ref"] = "tlbx-ai/tlbx/.github/workflows/mirror-builds.yml@refs/heads/dev";
        backfill["ref"] = "refs/heads/dev"; backfill["event_name"] = "workflow_dispatch";
        Assert.True((await f.Auth.AuthenticateAsync(f.Token(backfill), release, default))!.IsBackfill);
        backfill["event_name"] = "push";
        Assert.True((await f.Auth.AuthenticateAsync(f.Token(backfill), release, default))!.IsBackfill);
        backfill["ref"] = "refs/heads/main";
        Assert.Null(await f.Auth.AuthenticateAsync(f.Token(backfill), release, default));
    }
    [Fact]
    public async Task KeyRotationRefreshesUnknownKidAfterBoundedCooldown()
    {
        using var f = new Fixture(); var r = ReleaseIdentity.Parse("v1.2.3-dev");
        Assert.NotNull(await f.Auth.AuthenticateAsync(f.Token(), r, default));
        using var second = RSA.Create(2048); f.Handler.Key = second; f.Handler.Kid = "second";
        Assert.Null(await f.Auth.AuthenticateAsync(f.Token(key: second, kid: "second"), r, default));
        Assert.Equal(1, f.Handler.Requests);
        f.Clock.Now = f.Clock.Now.AddSeconds(31);
        Assert.NotNull(await f.Auth.AuthenticateAsync(f.Token(key: second, kid: "second"), r, default));
        Assert.Equal(2, f.Handler.Requests);
    }
    [Fact]
    public async Task AbortedUnverifiedCallerCannotCancelSharedJwksRefreshForTheNextPublisher()
    {
        using var f = new Fixture(); using var abort = new CancellationTokenSource();
        f.Handler.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var r = ReleaseIdentity.Parse("v1.2.3-dev");
        var first = f.Auth.AuthenticateAsync(f.Token(kid: "forged"), r, abort.Token);
        await f.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        abort.Cancel();
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(f.Handler.ProviderToken.IsCancellationRequested);
        var valid = f.Auth.AuthenticateAsync(f.Token(), r, default);
        f.Handler.Resume.TrySetResult();
        Assert.NotNull(await valid.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, f.Handler.Requests);
    }    [Fact]
    public async Task SharedRefreshTransportFailureIsObservedAndNextAttemptCanRecover()
    {
        using var f = new Fixture(); var r = ReleaseIdentity.Parse("v1.2.3-dev");
        f.Handler.FailIo = true;
        Assert.Null(await f.Auth.AuthenticateAsync(f.Token(), r, default));
        f.Clock.Now = f.Clock.Now.AddSeconds(31); f.Handler.FailIo = false;
        Assert.NotNull(await f.Auth.AuthenticateAsync(f.Token(), r, default));
        Assert.Equal(2, f.Handler.Requests);
    }    private static string Base64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class KeysHandler(RSA key) : HttpMessageHandler
    {
        public RSA Key = key; public string Kid = "first"; public int Requests;
        public TaskCompletionSource? Started, Resume; public CancellationToken ProviderToken; public bool FailIo;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://token.actions.githubusercontent.com/.well-known/jwks", request.RequestUri!.AbsoluteUri); Requests++;
            ProviderToken = ct; Started?.TrySetResult();
            if (Resume is not null) await Resume.Task.WaitAsync(ct);
            if (FailIo) throw new IOException("Simulated provider connection reset");
            var p = Key.ExportParameters(false);
            var json = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", alg = "RS256", kid = Kid, n = Base64(p.Modulus!), e = Base64(p.Exponent!) } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
    private sealed class Fixture : IDisposable
    {
        public RSA Key = RSA.Create(2048); public Clock Clock = new(); public KeysHandler Handler;
        public HttpClient Client; public GitHubOidc Auth;
        public Fixture()
        {
            Handler = new KeysHandler(Key); Client = new HttpClient(Handler);
            Auth = new GitHubOidc(new BuildOptions(Path.GetTempPath(), new Uri("https://builds.tlbx.ai"), "1124937387", "255465097"), Client, Clock);
        }
        public Dictionary<string, object> Claims() => new(StringComparer.Ordinal)
        {
            ["iss"] = "https://token.actions.githubusercontent.com", ["aud"] = "https://builds.tlbx.ai",
            ["exp"] = Clock.Now.AddMinutes(5).ToUnixTimeSeconds(), ["iat"] = Clock.Now.ToUnixTimeSeconds(), ["nbf"] = Clock.Now.AddSeconds(-1).ToUnixTimeSeconds(),
            ["repository_id"] = "1124937387", ["repository_owner_id"] = "255465097", ["repository"] = "tlbx-ai/tlbx",
            ["workflow_ref"] = "tlbx-ai/tlbx/.github/workflows/release.yml@refs/tags/v1.2.3-dev", ["ref"] = "refs/tags/v1.2.3-dev",
            ["event_name"] = "push", ["sha"] = new string('a', 40)
        };
        public string Token(Dictionary<string, object>? claims = null, RSA? key = null, string kid = "first")
        {
            var header = Base64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid }));
            var payload = Base64(JsonSerializer.SerializeToUtf8Bytes(claims ?? Claims()));
            var data = header + "." + payload;
            return data + "." + Base64((key ?? Key).SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        public void Dispose() { Auth.Dispose(); Client.Dispose(); Key.Dispose(); }
    }
}
