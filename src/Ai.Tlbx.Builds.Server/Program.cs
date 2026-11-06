using System.Reflection;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
namespace Ai.Tlbx.Builds.Server;

public static class BuildServer
{
    public static WebApplication Create(BuildOptions options, Action<IServiceCollection>? configure = null, string? url = null)
    {
        options.Validate();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:5018");
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = BuildOptions.MaxAssetBytes;
            k.Limits.MaxConcurrentConnections = 64;
            k.Limits.MaxRequestHeadersTotalSize = 32768;
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) });
        builder.Services.AddSingleton<IPublisherAuthenticator, GitHubOidc>();
        builder.Services.AddSingleton<IReleaseSignatureVerifier, ReleaseSignatureVerifier>();
        builder.Services.AddSingleton<ArchiveVerifier>();
        builder.Services.AddSingleton<ReleaseStore>();
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        var store = app.Services.GetRequiredService<ReleaseStore>();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (BuildProtocolException e) { if (!context.Response.HasStarted) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new { error = e.Message }, context.RequestAborted); } }
            catch (Exception e) when (e is JsonException or InvalidDataException or BadHttpRequestException)
            { if (!context.Response.HasStarted) { context.Response.StatusCode = e is BadHttpRequestException bad ? bad.StatusCode : 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid request or archive" }, context.RequestAborted); } }
        });
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = typeof(BuildServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion }));
        app.MapPut("/api/v1/releases/{tag}/assets/{name}", async (HttpContext context, string tag, string name, IPublisherAuthenticator auth) =>
        {
            var release = ReleaseIdentity.Parse(tag);
            if (await Authenticate(context, release, auth) is null) return Results.Unauthorized();
            var result = await store.UploadAsync(release, name, context.Request.Body, context.Request.ContentLength, context.RequestAborted);
            return Results.Json(result.Asset, statusCode: result.Exists ? 200 : 201);
        });
        app.MapPost("/api/v1/releases/{tag}/publish", async (HttpContext context, string tag, IPublisherAuthenticator auth) =>
        {
            var release = ReleaseIdentity.Parse(tag);
            var identity = await Authenticate(context, release, auth);
            if (identity is null) return Results.Unauthorized();
            if (context.Request.ContentLength > 128 * 1024) throw new BuildProtocolException(413, "Publication metadata exceeds limit");
            using var bytes = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
            {
                if (bytes.Length + count > 128 * 1024) throw new BuildProtocolException(413, "Publication metadata exceeds limit");
                bytes.Write(buffer, 0, count);
            }
            var publication = JsonSerializer.Deserialize<PublishRequest>(bytes.ToArray(), ReleaseStore.Json)
                ?? throw new BuildProtocolException(400, "Missing publication metadata");
            var exists = await store.PublishAsync(release, publication, identity, context.RequestAborted);
            return Results.Json(new { tag, published = true }, statusCode: exists ? 200 : 201);
        });
        app.MapMethods("/v1/channels/{channel}/releases.json", ["GET", "HEAD"], async (HttpContext context, string channel) =>
        {
            PublicHeaders(context);
            context.Response.Headers.CacheControl = "public,max-age=30";
            return Results.Json(await store.CatalogAsync(channel, context.RequestAborted));
        });
        app.MapMethods("/v1/releases/{tag}/{rid}/version.json", ["GET", "HEAD"], (HttpContext context, string tag, string rid) =>
        {
            PublicHeaders(context);
            var path = store.Manifest(ReleaseIdentity.Parse(tag), rid);
            return path is null ? Results.NotFound() : FileResult(context, path, null, "application/json");
        });
        app.MapMethods("/{channel}/{version}/{rid}/{name}", ["GET", "HEAD"], (HttpContext context, string channel, string version, string rid, string name) =>
        {
            PublicHeaders(context);
            var file = store.PublicAsset(channel, version, rid, name);
            return file is null ? Results.NotFound() : FileResult(context, file.Value.Path, file.Value.Digest, "application/octet-stream");
        });
        return app;
    }
    private static void PublicHeaders(HttpContext context)
    {
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlExposeHeaders = "ETag, Accept-Ranges, Content-Range, Content-Length";
    }
    private static IResult FileResult(HttpContext context, string path, string? digest, string contentType)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
            context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(stream, contentType, enableRangeProcessing: true,
                entityTag: digest is null ? null : new EntityTagHeaderValue('"' + digest + '"'));
        }
        catch (FileNotFoundException) { return Results.NotFound(); }
        catch (DirectoryNotFoundException) { return Results.NotFound(); }
    }
    private static Task<PublisherIdentity?> Authenticate(HttpContext context, ReleaseIdentity release, IPublisherAuthenticator auth)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.Ordinal) && authorization.Length < 16400
            ? auth.AuthenticateAsync(authorization[7..], release, context.RequestAborted) : Task.FromResult<PublisherIdentity?>(null);
    }
}
public static class Program
{
    public static async Task Main()
    {
        await using var app = BuildServer.Create(BuildOptions.FromEnvironment());
        await app.RunAsync();
    }
}
