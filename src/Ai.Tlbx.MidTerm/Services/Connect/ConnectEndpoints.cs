using System.Text.Json;

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
        app.MapGet("/api/connect/status", (ConnectService connect) =>
            Results.Json(connect.GetStatus(), ConnectJsonContext.Default.ConnectStatus));
        app.MapPost("/api/connect/pair", async (HttpContext context, ConnectService connect) =>
        {
            if (context.Request.ContentLength > 4096) return Results.StatusCode(413);
            try
            {
                var bytes = new byte[4097];
                var length = 0;
                int read;
                while ((read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), context.RequestAborted)) > 0)
                {
                    length += read;
                    if (length > 4096) return Results.StatusCode(413);
                }
                var request = JsonSerializer.Deserialize(bytes.AsSpan(0, length), ConnectJsonContext.Default.ConnectPairRequest);
                if (request is null) return Results.BadRequest();
                var response = await connect.PairAsync(request, context.RequestAborted);
                return Results.Json(response, ConnectJsonContext.Default.ConnectPairResponse);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                return Results.Problem("The Connect service could not be reached. Try again shortly.", statusCode: 502);
            }
        });
        app.MapPost("/api/connect/disconnect", async (HttpContext context, ConnectService connect) =>
        {
            await connect.DisconnectAsync(context.RequestAborted);
            return Results.NoContent();
        });
    }
}
