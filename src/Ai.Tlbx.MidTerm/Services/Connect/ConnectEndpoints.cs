using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ai.Tlbx.MidTerm.Services.Connect;

public static class ConnectEndpoints
{
    public static void MapConnectEndpoints(WebApplication app)
    {
        app.MapGet("/connect.html", () => Results.Content(ConnectSetupPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/connect-setup.js", () => Results.Content(ConnectSetupPage.JavaScript, "text/javascript; charset=utf-8"));
        app.MapGet("/connect-setup.css", () => Results.Content(ConnectSetupPage.Css, "text/css; charset=utf-8"));
        app.MapGet("/connect-licenses.txt", async (HttpContext context) =>
        {
            using var notices = typeof(ConnectEndpoints).Assembly.GetManifestResourceStream("mtconnect.notices");
            if (notices is null) return Results.NotFound();
            using var reader = new StreamReader(notices);
            return (IResult)Results.Content(await reader.ReadToEndAsync(context.RequestAborted), "text/plain; charset=utf-8");
        });
        app.MapGet("/api/connect/status", (ConnectService connect) => Results.Json(connect.GetStatus(), ConnectJsonContext.Default.ConnectStatus));
        app.MapPost("/api/connect/unlock", async (HttpContext context, ConnectProvisioningGate gate) =>
        {
            try
            {
                var request = await ReadAsync(context, ConnectJsonContext.Default.ConnectUnlockRequest);
                var status = gate.Unlock(context, request.Password, out var response);
                return status == 200 ? Results.Json(response, ConnectJsonContext.Default.ConnectUnlockResponse)
                    : Results.Problem(status == 429 ? "Too many attempts. Try again shortly." : "Confirm your current installation password.", statusCode: status);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { return Results.BadRequest(); }
        });
        app.MapPost("/api/connect/generate", async (HttpContext context, ConnectService connect, ConnectProvisioningGate gate) =>
        {
            try
            {
                var request = await ReadAsync(context, ConnectJsonContext.Default.ConnectGenerateRequest);
                if (!gate.Consume(context, request.UnlockToken)) return Results.Problem("Confirm your installation password again.", statusCode: 403);
                return Results.Json(await connect.GenerateAsync(request, context.RequestAborted), ConnectJsonContext.Default.ConnectGenerateResponse);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
            {
                return Results.Problem("The key request is invalid. Use an HTTPS server origin and a computer name of 1-100 characters.", statusCode: 400);
            }
        });
        app.MapPost("/api/connect/disconnect", async (HttpContext context, ConnectService connect, ConnectProvisioningGate gate) =>
        {
            try
            {
                var request = await ReadAsync(context, ConnectJsonContext.Default.ConnectDisconnectRequest);
                if (!gate.Consume(context, request.UnlockToken)) return Results.Problem("Confirm your installation password again.", statusCode: 403);
                await connect.DisconnectAsync(context.RequestAborted);
                return Results.NoContent();
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { return Results.BadRequest(); }
        });
    }

    private static async Task<T> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
    {
        if (context.Request.ContentLength > 4096) throw new InvalidDataException();
        var bytes = new byte[4097];
        var length = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), context.RequestAborted)) > 0)
        {
            length += read;
            if (length > 4096) throw new InvalidDataException();
        }
        return JsonSerializer.Deserialize(bytes.AsSpan(0, length), typeInfo) ?? throw new InvalidDataException();
    }
}
