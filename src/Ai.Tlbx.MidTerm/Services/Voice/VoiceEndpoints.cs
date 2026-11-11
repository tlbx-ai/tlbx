using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Ai.Tlbx.Inference;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Voice;

public static class VoiceEndpoints
{
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    public static void MapVoiceEndpoints(WebApplication app, SettingsService settingsService)
    {
        var settings = new AiProviderSettings(settingsService);
        var group = app.MapGroup("/api/ai");
        group.AddEndpointFilter(async (context, next) => RequestAccessContext.HasFullUserAccess(context.HttpContext)
            ? await next(context) : Results.StatusCode(StatusCodes.Status403Forbidden));
        group.MapGet("/settings", () => Json(settings.Status()));
        group.MapPut("/providers/{provider}", async (string provider, HttpContext context) => {
            try {
                var body = await ReadJsonAsync(context);
                if (!body.ContainsKey("apiKey")) return Results.BadRequest();
                settings.SetKey(provider, body["apiKey"]?.GetValue<string>());
                return Json(settings.Status());
            } catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapPut("/settings", async (HttpContext context) => {
            var body = await ReadJsonAsync(context);
            if (!Enum.TryParse<AiModel>(body["workerModel"]?.GetValue<string>(), out var model)
                || !AiProviderSettings.WorkerModels.Contains(model)) return Results.BadRequest();
            settings.WorkerModel = model;
            return Json(settings.Status());
        });
        group.MapPost("/transcribe", async (HttpContext context) => {
            var key = settings.GetKey("openai");
            if (key == null) return Results.Problem("Configure an OpenAI API key in AI & Voice settings.", statusCode: 409);
            using var pcm = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0) {
                if (pcm.Length + count > 8 * 1024 * 1024) return Results.StatusCode(413);
                pcm.Write(buffer, 0, count);
            }
            if (pcm.Length == 0 || pcm.Length % 2 != 0) return Results.BadRequest();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("gpt-4o-mini-transcribe"), "model");
            var audio = new ByteArrayContent(Wave(pcm.GetBuffer().AsSpan(0, checked((int)pcm.Length))));
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "file", "dictation.wav");
            request.Content = form;
            using var response = await Http.SendAsync(request, context.RequestAborted);
            if (!response.IsSuccessStatusCode) return Results.Problem("Transcription failed. Check OpenAI key and model access.", statusCode: 502);
            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(context.RequestAborted));
            return Json(new JsonObject { ["text"] = result?["text"]?.GetValue<string>() ?? "" });
        });
    }

    internal static async Task HandleVoiceAsync(HttpContext context, SettingsService settingsService)
    {
            var settings = new AiProviderSettings(settingsService);
            if (!RequestAccessContext.HasFullUserAccess(context)) { context.Response.StatusCode = 403; return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            if (settings.GetKey("openai") == null || settings.GetKey(AiProviderSettings.ProviderFor(settings.WorkerModel)) == null) {
                context.Response.StatusCode = 409; return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var session = new VoiceOrchestrationSession(socket, settings);
            await session.RunAsync(context.RequestAborted);
    }

    private static IResult Json(JsonObject value) => Results.Text(value.ToJsonString(), "application/json");
    private static async Task<JsonObject> ReadJsonAsync(HttpContext context)
    {
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0) {
            if (data.Length + count > 16384) throw new BadHttpRequestException("AI settings too large.", 413);
            data.Write(buffer, 0, count);
        }
        return JsonNode.Parse(data.GetBuffer().AsSpan(0, checked((int)data.Length))) as JsonObject ?? throw new BadHttpRequestException("Invalid AI settings.");
    }

    internal static byte[] Wave(ReadOnlySpan<byte> pcm)
    {
        var wav = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 24000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 48000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);
        pcm.CopyTo(wav.AsSpan(44)); return wav;
    }
}
