using System.Text.Json;
using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public static class SessionWorkspaceEndpoints
{
    public static void MapSessionWorkspaceEndpoints(WebApplication app, TtyHostSessionManager sessions,
        GitWatcherService git, SettingsService settings)
    {
        var state = new SessionUpdateStateService(settings.SettingsDirectory, "saved-workspace.json");
        var layout = app.Services.GetRequiredService<SessionLayoutStateService>();
        // A browser disconnect must not cancel an already confirmed save/close operation.
#pragma warning disable IDISP001, CA2000 // Disposed on application shutdown below; closures own it for the app lifetime.
        var gate = new SemaphoreSlim(1, 1);
#pragma warning restore IDISP001, CA2000
        app.Lifetime.ApplicationStopped.Register(gate.Dispose);
        var stopping = app.Lifetime.ApplicationStopping;

        async Task<IResult> RunAsync(string action)
        {
            if (!await gate.WaitAsync(0, stopping).ConfigureAwait(false))
                return Results.Conflict("A session save or restore is already running.");
            try
            {
                var saved = await state.ReadSavedStateAsync(stopping).ConfigureAwait(false);
                var current = SessionUpdateStateService.CaptureSessionDecorations(sessions, git);
                var blocked = current.Where(s => !SessionUpdateStateService.CanSaveWorkspaceSession(s))
                    .Select(s => s.Name ?? s.Topic ?? s.SessionId).ToArray();
                if (action == "save")
                {
                    if (saved is not null) return Results.Conflict("Restore the existing saved sessions before saving another workspace.");
                    if (current.Count == 0) return Results.BadRequest("There are no sessions to save.");
                    if (blocked.Length > 0) return Results.Conflict("These sessions cannot be resumed safely yet: " + string.Join(", ", blocked));
                    await state.CaptureAsync(sessions, git, fullUpdate: true,
                        tryResumeNonAiAgentProcesses: false,
                        layout: layout.GetSnapshot(current.Select(static session => session.SessionId)), ct: stopping).ConfigureAwait(false);
                    if (sessions.GetAllSessions().Any(s => current.Any(c => c.SessionId == s.Id)))
                        return Results.Conflict("The workspace was saved, but some sessions could not be closed. Close them before restarting.");
                }
                else if (action == "restore")
                {
                    if (saved is null) return Results.NotFound("There are no saved sessions.");
                    await state.RestoreAsync(sessions, git, layout, stopping).ConfigureAwait(false);
                    if (await state.ReadSavedStateAsync(stopping).ConfigureAwait(false) is not null)
                        return Results.Conflict("Some sessions could not be restored. Their resume hints are still saved; try restoring again.");
                }
                saved = await state.ReadSavedStateAsync(stopping).ConfigureAwait(false);
                return Results.Json(new SessionWorkspaceStatus
                {
                    SavedAt = saved?.SavedAt,
                    SavedSessionCount = saved?.Sessions.Count ?? 0,
                    LiveSessionCount = sessions.GetAllSessions().Count,
                    UnsupportedSessions = action == "status" ? blocked : []
                }, SessionWorkspaceJsonContext.Default.SessionWorkspaceStatus);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or UnauthorizedAccessException or TimeoutException)
            {
                return Results.Conflict(ex.Message);
            }
            finally { gate.Release(); }
        }

        app.MapGet("/api/sessions/workspace", () => RunAsync("status"));
        app.MapPost("/api/sessions/workspace/save", () => RunAsync("save"));
        app.MapPost("/api/sessions/workspace/restore", () => RunAsync("restore"));
    }
}

public sealed class SessionWorkspaceStatus
{
    public DateTimeOffset? SavedAt { get; set; }
    public int SavedSessionCount { get; set; }
    public int LiveSessionCount { get; set; }
    public string[] UnsupportedSessions { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionWorkspaceStatus))]
internal sealed partial class SessionWorkspaceJsonContext : JsonSerializerContext;
