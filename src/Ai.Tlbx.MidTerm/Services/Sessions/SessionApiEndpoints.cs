using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Common.Shells;

using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Tmux;
using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Microsoft.AspNetCore.Mvc;
namespace Ai.Tlbx.MidTerm.Services.Sessions;

public static partial class SessionApiEndpoints
{
    private static readonly HashSet<string> ClipboardImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".bmp",
        ".webp",
        ".tif",
        ".tiff"
    };

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathName(string lpszLongPath, char[] lpszShortPath, uint cchBuffer);

    private static string ToShortPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        var buffer = new char[260];
        var length = GetShortPathName(path, buffer, (uint)buffer.Length);
        return length > 0 ? new string(buffer, 0, (int)length) : path;
    }

    public static void MapSessionEndpoints(
        WebApplication app,
        TtyHostSessionManager sessionManager,
        SessionLayoutStateService layoutStateService,
        ManagerBarQueueService managerBarQueueService,
        ClipboardService clipboardService,
        UpdateService updateService,
        WebPreviewService webPreviewService,
        SessionTelemetryService sessionTelemetry,
        SessionAgentFeedService agentFeed,
        SessionSupervisorService sessionSupervisor,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        SessionCodexHandoffService codexHandoff,
        ProviderResumeCatalogService providerResumeCatalog,
        SessionAgentVibeService agentVibe,
        AiCliProfileService aiCliProfileService,
        WorkerSessionRegistryService workerSessionRegistry)
    {
        app.MapGet("/api/state", () =>
        {
            var response = new StateUpdate
            {
                Sessions = GetSessionListDto(sessionManager, sessionSupervisor, appServerControlRuntime),
                Update = updateService.LatestUpdate,
                Layout = layoutStateService.GetSnapshot(sessionManager.GetAllSessions().Select(s => s.Id)),
                ManagerBarQueue = managerBarQueueService.GetSnapshot(sessionManager.GetAllSessions().Select(s => s.Id)).ToList()
            };
            return Results.Json(response, AppJsonContext.Default.StateUpdate);
        });

        async Task<IResult> EnqueueCommandBayQueueItem(ManagerBarQueueEnqueueRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return Results.BadRequest("sessionId required");
            }

            if (sessionManager.GetSession(request.SessionId) is null)
            {
                return Results.NotFound();
            }

            ManagerBarQueueEntryDto? entry;
            if (request.Action is not null)
            {
                var (accepted, queuedEntry) = await managerBarQueueService
                    .SubmitActionAsync(request.SessionId, request.Action, ct)
                    .ConfigureAwait(false);
                if (!accepted)
                {
                    return Results.BadRequest("Only queued command-bay items can be enqueued.");
                }

                if (queuedEntry is null)
                {
                    return Results.Ok();
                }

                entry = queuedEntry;
            }
            else if (request.Turn is not null)
            {
                var (accepted, queuedEntry) = await managerBarQueueService
                    .SubmitPromptAsync(request.SessionId, request.Turn, ct)
                    .ConfigureAwait(false);
                if (!accepted)
                {
                    return Results.BadRequest("Only queued command-bay items can be enqueued.");
                }

                if (queuedEntry is null)
                {
                    return Results.Ok();
                }

                entry = queuedEntry;
            }
            else
            {
                return Results.BadRequest("action or turn required");
            }

            if (entry is null)
            {
                return Results.BadRequest("Only queued command-bay items can be enqueued.");
            }

            return Results.Json(entry, AppJsonContext.Default.ManagerBarQueueEntryDto);
        }

        app.MapPost("/api/manager-bar/queue", EnqueueCommandBayQueueItem);
        app.MapPost("/api/command-bay/queue", EnqueueCommandBayQueueItem);

        IResult RemoveCommandBayQueueItem(string queueId)
        {
            return managerBarQueueService.Remove(queueId)
                ? Results.Ok()
                : Results.NotFound();
        }

        app.MapDelete("/api/manager-bar/queue/{queueId}", RemoveCommandBayQueueItem);
        app.MapDelete("/api/command-bay/queue/{queueId}", RemoveCommandBayQueueItem);

        app.MapGet("/api/sessions", () =>
        {
            return Results.Json(GetSessionListDto(sessionManager, sessionSupervisor, appServerControlRuntime), AppJsonContext.Default.SessionListDto);
        });

        app.MapGet("/api/sessions/attention", (bool agentOnly = true) =>
        {
            var response = sessionSupervisor.DescribeFleet(GetSessionListDto(sessionManager, sessionSupervisor, appServerControlRuntime).Sessions, agentOnly);
            return Results.Json(response, AppJsonContext.Default.SessionAttentionResponse);
        });

        app.MapPost("/api/sessions", async (CreateSessionRequest? request, CancellationToken ct) =>
        {
            var cols = request?.Cols ?? 120;
            var rows = request?.Rows ?? 30;

            ShellType? shellType = null;
            if (!string.IsNullOrEmpty(request?.Shell) && Enum.TryParse<ShellType>(request.Shell, true, out var parsed))
            {
                shellType = parsed;
            }

            var creation = await sessionManager.CreateSessionDetailedAsync(
                shellType?.ToString(), cols, rows, request?.WorkingDirectory, ct);

            if (!creation.Succeeded)
            {
                return CreateSessionLaunchProblem(creation.Failure);
            }

            var sessionInfo = creation.Session!;
            ApplySessionSpaceMetadata(
                sessionManager,
                sessionInfo.Id,
                request?.SpaceId,
                request?.WorkspacePath,
                request?.Surface,
                string.IsNullOrWhiteSpace(request?.SpaceId)
                    ? SessionLaunchOrigins.AdHoc
                    : SessionLaunchOrigins.Space);
            return Results.Json(GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, sessionInfo.Id), AppJsonContext.Default.SessionInfoDto);
        });

        app.MapPost("/api/workers/bootstrap", async (WorkerBootstrapRequest request, CancellationToken ct) =>
        {
            var creation = await sessionManager.CreateSessionDetailedAsync(
                request.Shell,
                request.Cols,
                request.Rows,
                request.WorkingDirectory,
                applyTerminalEnvironmentVariables: false,
                ct);

            if (!creation.Succeeded)
            {
                return CreateSessionLaunchProblem(creation.Failure, "Worker session launch failed");
            }

            var sessionInfo = creation.Session!;
            var sessionId = sessionInfo.Id;
            ApplySessionSpaceMetadata(
                sessionManager,
                sessionId,
                request.SpaceId,
                request.WorkspacePath,
                request.Surface,
                string.IsNullOrWhiteSpace(request.SpaceId)
                    ? SessionLaunchOrigins.AdHoc
                    : SessionLaunchOrigins.Space);

            if (request.AgentControlled)
            {
                sessionManager.SetAgentControlled(sessionId, true);
            }

            var requestedProfile = aiCliProfileService.NormalizeProfile(request.Profile);
            if (requestedProfile != AiCliProfileService.UnknownProfile)
            {
                sessionManager.SetProfileHint(sessionId, requestedProfile);
            }

            if (!string.IsNullOrWhiteSpace(request.ResumeThreadId))
            {
                sessionManager.SetAppServerControlResumeThreadId(sessionId, request.ResumeThreadId);
            }

            if (request.AppServerControlOnly)
            {
                sessionManager.SetAppServerControlOnly(sessionId, true);
            }

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                await sessionManager.SetSessionNameAsync(sessionId, request.Name, isManual: true, ct);
            }

            var workerSession = GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, sessionId);
            var resolvedProfile = aiCliProfileService.NormalizeProfile(request.Profile, workerSession);
            var launchCommand = request.AppServerControlOnly
                ? null
                : string.IsNullOrWhiteSpace(request.LaunchCommand)
                    ? aiCliProfileService.GetDefaultLaunchCommand(resolvedProfile)
                    : request.LaunchCommand.Trim();

            var guidanceInjected = false;
            string? midtermDir = null;
            var targetDirectory = workerSession.CurrentDirectory ?? request.WorkingDirectory;
            if (request.InjectGuidance &&
                !string.IsNullOrWhiteSpace(targetDirectory) &&
                Directory.Exists(targetDirectory))
            {
                midtermDir = MidtermDirectory.TryEnsureForCwd(targetDirectory);
                guidanceInjected = midtermDir is not null;
            }

            if (!string.IsNullOrWhiteSpace(launchCommand))
            {
                await SendInputAndRecordAsync(sessionManager, sessionTelemetry, sessionId, Encoding.UTF8.GetBytes(launchCommand + "\r"), ct);
                if (request.LaunchDelayMs > 0)
                {
                    await Task.Delay(request.LaunchDelayMs, ct);
                }
            }

            var slashCommands = request.AppServerControlOnly
                ? []
                : aiCliProfileService.NormalizeSlashCommands(resolvedProfile, request.SlashCommands);
            workerSessionRegistry.Register(
                sessionId,
                resolvedProfile,
                launchCommand,
                slashCommands,
                request.LaunchDelayMs,
                request.SlashCommandDelayMs);
            agentFeed.NoteWorkerBootstrap(
                sessionId,
                resolvedProfile,
                launchCommand,
                slashCommands,
                guidanceInjected);
            foreach (var slashCommand in slashCommands)
            {
                var currentSession = GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, sessionId);
                if (!TryBuildPromptExecutionPlan(
                        new SessionPromptRequest
                        {
                            Text = slashCommand,
                            Mode = "auto",
                            Profile = resolvedProfile,
                            SubmitDelayMs = request.SlashCommandDelayMs
                        },
                        currentSession,
                        aiCliProfileService,
                        out var plan,
                        out var error))
                {
                    return Results.BadRequest(error);
                }

                await ExecutePromptPlanAsync(sessionManager, sessionTelemetry, sessionId, plan, ct);
            }

            return Results.Json(new WorkerBootstrapResponse
            {
                Session = GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, sessionId),
                Profile = resolvedProfile,
                LaunchCommand = launchCommand,
                SlashCommands = slashCommands,
                GuidanceInjected = guidanceInjected,
                MidtermDir = midtermDir
            }, AppJsonContext.Default.WorkerBootstrapResponse);
        });

        app.MapGet("/api/providers/{provider}/resume-candidates", (
            string provider,
            string? workingDirectory,
            string? scope,
            CancellationToken ct) =>
        {
            var normalizedProvider = aiCliProfileService.NormalizeProfile(provider);
            if (normalizedProvider is not AiCliProfileService.CodexProfile and not AiCliProfileService.ClaudeProfile)
            {
                return Results.BadRequest("Only Codex and Claude resume catalogs are supported.");
            }

            var includeAllDirectories = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);
            var candidates = providerResumeCatalog.GetCandidates(
                normalizedProvider,
                workingDirectory,
                includeAllDirectories,
                ct);
            return Results.Json(candidates, AppJsonContext.Default.ListProviderResumeCatalogEntryDto);
        });

        app.MapPost("/api/sessions/reorder", (SessionReorderRequest request) =>
        {
            if (request.SessionIds.Count == 0)
            {
                return Results.BadRequest("sessionIds required");
            }

            return sessionManager.ReorderSessions(request.SessionIds)
                ? Results.Ok()
                : Results.BadRequest("Invalid session IDs");
        });

        app.MapDelete("/api/sessions/{id}", async (string id, CancellationToken ct) =>
        {
            workerSessionRegistry.Forget(id);
            agentFeed.Forget(id);
            await sessionManager.CloseSessionAsync(id, ct);
            return Results.Ok();
        });

        app.MapPost("/api/sessions/{id}/resize", async (string id, ResizeRequest request, CancellationToken ct) =>
        {
            var success = await sessionManager.ResizeSessionAsync(id, request.Cols, request.Rows, ct);
            if (!success)
            {
                return Results.NotFound();
            }
            return Results.Json(new ResizeResponse
            {
                Accepted = true,
                Cols = request.Cols,
                Rows = request.Rows
            }, AppJsonContext.Default.ResizeResponse);
        });

        app.MapGet("/api/sessions/{id}/state", async (string id, bool includeBuffer = true, bool includeBufferBase64 = false, CancellationToken ct = default) =>
        {
            if (sessionManager.GetSession(id) is null)
            {
                return Results.NotFound();
            }

            await sessionManager.GetSessionFreshAsync(id, ct).ConfigureAwait(false);

            var response = new SessionStateResponse
            {
                Session = GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, id),
                Previews = webPreviewService.ListPreviewSessions(id).Previews.ToArray(),
                TerminalTransport = BuildTerminalTransportDiagnostics(sessionManager, id)
            };

            if (includeBuffer)
            {
                var snapshot = await sessionManager.GetBufferAsync(id, ct: ct);
                if (snapshot is not null)
                {
                    response.BufferByteLength = snapshot.Data.Length;
                    response.BufferText = Encoding.UTF8.GetString(snapshot.Data);
                    response.BufferBase64 = includeBufferBase64
                        ? Convert.ToBase64String(snapshot.Data)
                        : null;
                }
            }

            return Results.Json(response, AppJsonContext.Default.SessionStateResponse);
        });

        app.MapPost("/api/sessions/{id}/input/text", async (string id, SessionInputRequest request, CancellationToken ct) =>
        {
            if (sessionManager.GetSession(id) is null)
            {
                return Results.NotFound();
            }

            if (!TryGetInputBytes(request, out var data, out var error))
            {
                return Results.BadRequest(error);
            }

            await SendInputAndRecordAsync(sessionManager, sessionTelemetry, id, data, ct);
            return Results.Ok();
        });

        app.MapPost("/api/sessions/{id}/input/keys", async (string id, SessionKeyInputRequest request, CancellationToken ct) =>
        {
            if (sessionManager.GetSession(id) is null)
            {
                return Results.NotFound();
            }

            if (!TryGetKeyInputBytes(request, out var data, out var error))
            {
                return Results.BadRequest(error);
            }

            await SendInputAndRecordAsync(sessionManager, sessionTelemetry, id, data, ct);
            agentFeed.NoteKeyInput(id, request);
            return Results.Ok();
        });

        app.MapPost("/api/sessions/{id}/input/prompt", async (string id, SessionPromptRequest request, CancellationToken ct) =>
        {
            if (sessionManager.GetSession(id) is null)
            {
                return Results.NotFound();
            }

            var session = await EnsureWorkerReadyForPromptAsync(
                sessionManager,
                sessionTelemetry,
                sessionSupervisor,
                appServerControlRuntime,
                aiCliProfileService,
                workerSessionRegistry,
                id,
                request,
                ct);

            if (await appServerControlRuntime.TrySendPromptAsync(id, request, ct).ConfigureAwait(false))
            {
                var promptProfile = aiCliProfileService.NormalizeProfile(request.Profile, session);
                agentFeed.NotePrompt(id, promptProfile, request);
                return Results.Ok();
            }

            if (!TryBuildPromptExecutionPlan(
                    request,
                    session,
                    aiCliProfileService,
                    out var plan,
                    out var error))
            {
                return Results.BadRequest(error);
            }

            await ExecutePromptPlanAsync(sessionManager, sessionTelemetry, id, plan, ct);
            var resolvedProfile = aiCliProfileService.NormalizeProfile(request.Profile, session);
            agentFeed.NotePrompt(id, resolvedProfile, request);
            return Results.Ok();
        });

        app.MapGet("/api/sessions/{id}/buffer/text", async (string id, bool includeBase64 = false, CancellationToken ct = default) =>
        {
            if (sessionManager.GetSession(id) is null)
            {
                return Results.NotFound();
            }

            var snapshot = await sessionManager.GetBufferAsync(id, ct: ct);
            if (snapshot is null)
            {
                return Results.NotFound();
            }

            var response = new SessionBufferTextResponse
            {
                SessionId = id,
                ByteLength = snapshot.Data.Length,
                Text = Encoding.UTF8.GetString(snapshot.Data),
                Base64 = includeBase64 ? Convert.ToBase64String(snapshot.Data) : null
            };

            return Results.Json(response, AppJsonContext.Default.SessionBufferTextResponse);
        });

        app.MapGet("/api/sessions/{id}/buffer/tail", async (string id, int lines = 120, bool stripAnsi = true, CancellationToken ct = default) =>
        {
            if (sessionManager.GetSession(id) is null)
            {
                return Results.NotFound();
            }

            var snapshot = await sessionManager.GetBufferAsync(id, ct: ct);
            if (snapshot is null)
            {
                return Results.NotFound();
            }

            var text = TerminalOutputSanitizer.Decode(snapshot.Data);
            if (stripAnsi)
            {
                text = TerminalOutputSanitizer.StripEscapeSequences(text);
            }

            text = TerminalOutputSanitizer.TailLines(text, lines, out _, out _);
            return Results.Text(text, "text/plain", Encoding.UTF8);
        });

        app.MapGet("/api/sessions/{id}/activity", (string id, int seconds = 120, int bellLimit = 25) =>
        {
            if (sessionManager.GetSession(id) is null)
            {
                return Results.NotFound();
            }

            var response = sessionTelemetry.GetActivity(id, seconds, bellLimit);
            return Results.Json(response, AppJsonContext.Default.SessionActivityResponse);
        });

        app.MapGet("/api/sessions/{id}/agent", async (
            string id,
            int tailLines = 80,
            int activitySeconds = 90,
            int bellLimit = 8,
            CancellationToken ct = default) =>
        {
            var response = await agentVibe.BuildVibeAsync(id, tailLines, activitySeconds, bellLimit, ct);
            return response is null
                ? Results.NotFound()
                : Results.Json(response, AppJsonContext.Default.AgentSessionVibeResponse);
        });

        app.MapGet("/api/sessions/{id}/agent/feed", async (
            string id,
            int tailLines = 80,
            int activitySeconds = 90,
            int bellLimit = 8,
            CancellationToken ct = default) =>
        {
            var vibe = await agentVibe.BuildVibeAsync(id, tailLines, activitySeconds, bellLimit, ct);
            if (vibe is null)
            {
                return Results.NotFound();
            }

            var feed = agentFeed.GetFeed(id, vibe.Source, vibe.Activities, vibe.GeneratedAt);
            return Results.Json(feed, AppJsonContext.Default.AgentSessionFeedResponse);
        });

        app.MapPut("/api/sessions/{id}/name", async (string id, RenameSessionRequest request, bool auto = false, CancellationToken ct = default) =>
        {
            if (!await sessionManager.SetSessionNameAsync(id, request.Name, isManual: !auto, ct))
            {
                return Results.NotFound();
            }
            return Results.Ok();
        });

        app.MapPut("/api/sessions/{id}/bookmark", (string id, SetBookmarkRequest request) =>
        {
            if (!sessionManager.SetBookmarkId(id, request.BookmarkId))
            {
                return Results.NotFound();
            }
            return Results.Ok();
        });

        app.MapPut("/api/sessions/{id}/notes", (string id, SetSessionNotesRequest request) =>
        {
            if (!sessionManager.SetSessionNotes(id, request.Notes))
            {
                return Results.NotFound();
            }
            return Results.Json(GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, id), AppJsonContext.Default.SessionInfoDto);
        });

        app.MapPut("/api/sessions/{id}/topic", (string id, SetSessionTopicRequest request) =>
        {
            if (!sessionManager.SetSessionTopic(id, request.Topic))
            {
                return Results.NotFound();
            }
            return Results.Json(GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, id), AppJsonContext.Default.SessionInfoDto);
        });

        app.MapPut("/api/sessions/{id}/control", (string id, SetSessionControlRequest request) =>
        {
            if (!sessionManager.SetAgentControlled(id, request.AgentControlled))
            {
                return Results.NotFound();
            }

            return Results.Json(GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, id), AppJsonContext.Default.SessionInfoDto);
        });

        app.MapPost("/api/sessions/{id}/upload", async (string id, IFormFile file, CancellationToken ct) =>
        {
            var session = sessionManager.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            if (file is null || file.Length == 0)
            {
                return Results.BadRequest("No file provided");
            }

            var targetPath = await SaveUploadedFileAsync(sessionManager, id, file, ct);

            // To make Johannes happy
            if (!File.Exists(targetPath))
            {
                return Results.Problem("File write succeeded but file not found");
            }

            // Use 8.3 short path on Windows for compatibility with legacy apps
            var responsePath = ToShortPath(targetPath);

            return Results.Json(new FileUploadResponse { Path = responsePath }, AppJsonContext.Default.FileUploadResponse);
        }).DisableAntiforgery();

        app.MapPost("/api/sessions/{id}/paste-clipboard-image", async (string id, IFormFile file, CancellationToken ct) =>
        {
            var session = sessionManager.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            if (file is null || file.Length == 0)
            {
                return Results.BadRequest("No file provided");
            }

            var targetPath = await SaveUploadedFileAsync(sessionManager, id, file, ct);

            var success = await TrySetClipboardImageAsync(
                sessionManager,
                clipboardService,
                session,
                id,
                targetPath,
                file.ContentType,
                ct);
            if (!success)
            {
                return Results.Problem("Failed to set clipboard");
            }

            await sessionManager.SendInputAsync(id, new byte[] { 0x1b, 0x76 }, ct);

            return Results.Ok();
        }).DisableAntiforgery();

        app.MapPost("/api/sessions/{id}/inject-guidance", (string id) =>
        {
            var session = sessionManager.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            var cwd = session.CurrentDirectory;
            if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            {
                return Results.BadRequest("Session has no valid working directory");
            }

            var midtermDir = MidtermDirectory.Ensure(cwd);

            return Results.Json(new InjectGuidanceResponse
            {
                MidtermDir = midtermDir,
                MtcliShellPath = Path.Combine(midtermDir, "mtcli.sh"),
                MtcliPowerShellPath = Path.Combine(midtermDir, "mtcli.ps1"),
                ClaudeMdUpdated = false,
                AgentsMdUpdated = false,
            }, AppJsonContext.Default.InjectGuidanceResponse);
        });
    }

    internal static bool TryGetInputBytes(
        SessionInputRequest request,
        out byte[] data,
        out string error)
    {
        data = [];
        error = "";

        var hasText = !string.IsNullOrEmpty(request.Text);
        var hasBase64 = !string.IsNullOrEmpty(request.Base64);

        if (hasText == hasBase64)
        {
            error = "Provide exactly one of text or base64.";
            return false;
        }

        if (hasText)
        {
            var text = request.Text!;
            if (request.AppendNewline)
            {
                text += "\r";
            }

            data = Encoding.UTF8.GetBytes(text);
            return true;
        }

        try
        {
            data = Convert.FromBase64String(request.Base64!);
            if (request.AppendNewline)
            {
                Array.Resize(ref data, data.Length + 1);
                data[^1] = (byte)'\r';
            }
            return true;
        }
        catch (FormatException)
        {
            error = "base64 is invalid.";
            return false;
        }
    }

    internal static bool TryGetKeyInputBytes(
        SessionKeyInputRequest request,
        out byte[] data,
        out string error)
    {
        data = [];
        error = "";

        if (request.Keys is null || request.Keys.Count == 0)
        {
            error = "Provide at least one key.";
            return false;
        }

        if (!request.Literal && request.Keys.Any(string.IsNullOrWhiteSpace))
        {
            error = "Keys cannot be empty.";
            return false;
        }

        data = TmuxKeyTranslator.TranslateKeys(request.Keys, request.Literal);
        return true;
    }

    internal static bool TryGetPromptInputSequence(
        SessionPromptRequest request,
        bool interruptFirst,
        out byte[]? interruptData,
        out byte[] promptData,
        out byte[] submitData,
        out int interruptDelayMs,
        out int submitDelayMs,
        out string error)
    {
        interruptData = null;
        promptData = [];
        submitData = [];
        error = "";

        if (request.InterruptDelayMs < 0 || request.SubmitDelayMs < 0 || request.FollowupSubmitDelayMs < 0)
        {
            interruptDelayMs = 0;
            submitDelayMs = 0;
            error = "Delay values cannot be negative.";
            return false;
        }

        interruptDelayMs = request.InterruptDelayMs;
        submitDelayMs = request.SubmitDelayMs;

        if (!TryGetInputBytes(new SessionInputRequest
            {
                Text = request.Text,
                Base64 = request.Base64,
                AppendNewline = false
            },
            out promptData,
            out error))
        {
            return false;
        }

        if (!TryGetKeyInputBytes(new SessionKeyInputRequest
            {
                Keys = request.SubmitKeys,
                Literal = request.LiteralSubmitKeys
            },
            out submitData,
            out error))
        {
            error = error == "Provide at least one key."
                ? "Provide at least one submit key."
                : error;
            return false;
        }

        if (!interruptFirst)
        {
            return true;
        }

        if (!TryGetKeyInputBytes(new SessionKeyInputRequest
            {
                Keys = request.InterruptKeys,
                Literal = request.LiteralInterruptKeys
            },
            out var translatedInterruptData,
            out error))
        {
            error = error == "Provide at least one key."
                ? "Provide at least one interrupt key."
                : error;
            return false;
        }

        interruptData = translatedInterruptData;
        return true;
    }

    internal static bool TryGetPromptInputSequence(
        SessionPromptRequest request,
        out byte[]? interruptData,
        out byte[] promptData,
        out byte[] submitData,
        out int interruptDelayMs,
        out int submitDelayMs,
        out string error)
    {
        return TryGetPromptInputSequence(
            request,
            request.InterruptFirst,
            out interruptData,
            out promptData,
            out submitData,
            out interruptDelayMs,
            out submitDelayMs,
            out error);
    }

    internal static bool TryBuildPromptExecutionPlan(
        SessionPromptRequest request,
        SessionInfoDto session,
        AiCliProfileService aiCliProfileService,
        out SessionPromptExecutionPlan plan,
        out string error)
    {
        plan = new SessionPromptExecutionPlan(null, [], [], 0, 0, 0, 0);
        error = "";

        var mode = NormalizePromptMode(request.Mode);
        if (mode is null)
        {
            error = "Mode must be auto, append, or interrupt-first.";
            return false;
        }

        var supervisor = session.Supervisor ?? new SessionSupervisorInfoDto();
        var profile = aiCliProfileService.NormalizeProfile(request.Profile, session);
        var interruptFirst = mode switch
        {
            "interrupt-first" => true,
            "append" => false,
            _ => supervisor.State == SessionSupervisorService.BusyTurnState
        };

        if (!TryGetPromptInputSequence(
                request,
                interruptFirst,
                out var interruptData,
                out var promptData,
                out var submitData,
                out var interruptDelayMs,
                out var submitDelayMs,
                out error))
        {
            return false;
        }

        var followupSubmitCount = request.FollowupSubmitCount;
        if (followupSubmitCount <= 0 &&
            request.Text?.AsSpan().Contains("\n", StringComparison.Ordinal) == true &&
            aiCliProfileService.IsInteractiveAi(profile))
        {
            followupSubmitCount = 1;
        }

        plan = new SessionPromptExecutionPlan(
            interruptData,
            promptData,
            submitData,
            interruptDelayMs,
            submitDelayMs,
            followupSubmitCount,
            request.FollowupSubmitDelayMs);
        return true;
    }

    private static string? NormalizePromptMode(string? mode)
    {
        return (mode ?? "auto").Trim().ToLowerInvariant() switch
        {
            "auto" => "auto",
            "append" => "append",
            "interrupt-first" or "interruptfirst" => "interrupt-first",
            _ => null
        };
    }

    private static async Task ExecutePromptPlanAsync(
        TtyHostSessionManager sessionManager,
        SessionTelemetryService sessionTelemetry,
        string sessionId,
        SessionPromptExecutionPlan plan,
        CancellationToken ct)
    {
        await SessionPromptPlanExecutor.ExecuteAsync(
            plan,
            (data, cancellationToken) => SendInputAndRecordAsync(sessionManager, sessionTelemetry, sessionId, data, cancellationToken),
            static (delayMs, cancellationToken) => Task.Delay(delayMs, cancellationToken),
            ct).ConfigureAwait(false);
    }

    private static async Task<SessionInfoDto> EnsureWorkerReadyForPromptAsync(
        TtyHostSessionManager sessionManager,
        SessionTelemetryService sessionTelemetry,
        SessionSupervisorService sessionSupervisor,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        AiCliProfileService aiCliProfileService,
        WorkerSessionRegistryService workerSessionRegistry,
        string sessionId,
        SessionPromptRequest request,
        CancellationToken ct)
    {
        var session = GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, sessionId);
        if (session.Supervisor?.State != SessionSupervisorService.ShellState)
        {
            return session;
        }

        if (!TryBuildWorkerAutoResumePlan(sessionId, request, session, aiCliProfileService, workerSessionRegistry, out var resumePlan))
        {
            return session;
        }

        await SendInputAndRecordAsync(
            sessionManager,
            sessionTelemetry,
            sessionId,
            Encoding.UTF8.GetBytes(resumePlan.LaunchCommand + "\r"),
            ct);

        if (resumePlan.LaunchDelayMs > 0)
        {
            await Task.Delay(resumePlan.LaunchDelayMs, ct);
        }

        foreach (var slashCommand in resumePlan.SlashCommands)
        {
            var currentSession = GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, sessionId);
            if (!TryBuildPromptExecutionPlan(
                    new SessionPromptRequest
                    {
                        Text = slashCommand,
                        Mode = "auto",
                        Profile = resumePlan.Profile,
                        SubmitDelayMs = resumePlan.SlashCommandDelayMs
                    },
                    currentSession,
                    aiCliProfileService,
                    out var slashPlan,
                    out _))
            {
                continue;
            }

            await ExecutePromptPlanAsync(sessionManager, sessionTelemetry, sessionId, slashPlan, ct);
        }

        return GetSessionDto(sessionManager, sessionSupervisor, appServerControlRuntime, sessionId);
    }

    internal static bool TryBuildWorkerAutoResumePlan(
        string sessionId,
        SessionPromptRequest request,
        SessionInfoDto session,
        AiCliProfileService aiCliProfileService,
        WorkerSessionRegistryService workerSessionRegistry,
        out WorkerAutoResumePlan plan)
    {
        plan = new WorkerAutoResumePlan(string.Empty, AiCliProfileService.UnknownProfile, [], 0, 0);

        if (session.Supervisor?.State != SessionSupervisorService.ShellState)
        {
            return false;
        }

        if (session.AppServerControlOnly)
        {
            return false;
        }

        var hasRegistry = workerSessionRegistry.TryGet(sessionId, out var registration);
        var profile = aiCliProfileService.NormalizeProfile(
            request.Profile ?? (hasRegistry ? registration!.Profile : null),
            session);

        if (!aiCliProfileService.IsInteractiveAi(profile))
        {
            return false;
        }

        var launchCommand = hasRegistry
            ? registration!.LaunchCommand
            : aiCliProfileService.GetDefaultLaunchCommand(profile);

        if (string.IsNullOrWhiteSpace(launchCommand))
        {
            return false;
        }

        plan = new WorkerAutoResumePlan(
            launchCommand.Trim(),
            profile,
            hasRegistry ? registration!.SlashCommands : [],
            hasRegistry ? registration!.LaunchDelayMs : 1200,
            hasRegistry ? registration!.SlashCommandDelayMs : 350);
        return true;
    }

    private static async Task SendInputAndRecordAsync(
        TtyHostSessionManager sessionManager,
        SessionTelemetryService sessionTelemetry,
        string sessionId,
        byte[] data,
        CancellationToken ct = default)
    {
        sessionTelemetry.RecordInput(sessionId, data.Length);
        await sessionManager.SendInputAsync(sessionId, data, ct);
    }

    internal readonly record struct WorkerAutoResumePlan(
        string LaunchCommand,
        string Profile,
        IReadOnlyList<string> SlashCommands,
        int LaunchDelayMs,
        int SlashCommandDelayMs);

    internal static int GetPreferredClipboardProcessId(SessionInfo session)
    {
        return session.HostPid > 0 ? session.HostPid : session.Pid;
    }

    internal static async Task<bool> TrySetClipboardImageAsync(
        Func<CancellationToken, Task<bool>> sessionScopedSetter,
        Func<CancellationToken, Task<bool>> fallbackSetter,
        CancellationToken ct = default)
    {
        if (await sessionScopedSetter(ct).ConfigureAwait(false))
        {
            return true;
        }

        return await fallbackSetter(ct).ConfigureAwait(false);
    }

    private static async Task<string> SaveUploadedFileAsync(
        TtyHostSessionManager sessionManager, string sessionId, IFormFile file, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = string.Create(CultureInfo.InvariantCulture, $"upload_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        }

        var uploadDir = GetUploadDirectory(sessionManager, sessionId);

        var targetPath = Path.Combine(uploadDir, fileName);
        var counter = 1;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        while (File.Exists(targetPath))
        {
            fileName = string.Create(CultureInfo.InvariantCulture, $"{baseName}_{counter}{extension}");
            targetPath = Path.Combine(uploadDir, fileName);
            counter++;
        }

        await using (var stream = File.Create(targetPath))
        {
            await file.CopyToAsync(stream, ct);
        }

        return targetPath;
    }

    private static Task<bool> TrySetClipboardImageAsync(
        TtyHostSessionManager sessionManager,
        ClipboardService clipboardService,
        SessionInfo session,
        string sessionId,
        string targetPath,
        string? mimeType,
        CancellationToken ct = default)
    {
        var preferredProcessId = GetPreferredClipboardProcessId(session);
        return TrySetClipboardImageAsync(
            token => sessionManager.SetClipboardImageAsync(sessionId, targetPath, mimeType, token),
            _ => clipboardService.SetImageAsync(targetPath, mimeType, preferredProcessId),
            ct);
    }

    private static string GetUploadDirectory(TtyHostSessionManager sessionManager, string sessionId)
    {
        var session = sessionManager.GetSession(sessionId);
        var cwd = session?.CurrentDirectory;

        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
        {
            try
            {
                return MidtermDirectory.EnsureSubdirectory(cwd, "uploads");
            }
            catch
            {
                // Fall through to temp directory if cwd is not writable
            }
        }

        return sessionManager.GetTempDirectory(sessionId);
    }

    private static SessionListDto GetSessionListDto(
        TtyHostSessionManager sessionManager,
        SessionSupervisorService sessionSupervisor,
        SessionAppServerControlRuntimeService appServerControlRuntime)
    {
        var response = sessionManager.GetSessionList();
        foreach (var session in response.Sessions)
        {
            session.Supervisor = sessionSupervisor.Describe(session);
            session.HasAppServerControlHistory = appServerControlRuntime.HasHistory(session.Id);
        }

        return response;
    }

    private static void ApplySessionSpaceMetadata(
        TtyHostSessionManager sessionManager,
        string sessionId,
        string? spaceId,
        string? workspacePath,
        string? surface,
        string? launchOrigin)
    {
        sessionManager.SetLaunchOrigin(sessionId, launchOrigin);
        sessionManager.SetSpaceId(sessionId, spaceId);
        sessionManager.SetWorkspacePath(sessionId, workspacePath);
        sessionManager.SetSurface(sessionId, surface);
    }

    private static TerminalTransportDiagnosticsDto BuildTerminalTransportDiagnostics(
        TtyHostSessionManager sessionManager,
        string sessionId)
    {
        var session = sessionManager.GetSession(sessionId);
        var transport = session?.Transport;
        var runtime = sessionManager.GetTransportRuntimeSnapshot(sessionId);

        return new TerminalTransportDiagnosticsDto
        {
            SourceSeq = ((transport?.SourceSeq ?? 0UL) > 0 ? transport!.SourceSeq : runtime.SourceSeq).ToString(),
            MuxReceivedSeq = runtime.MuxReceivedSeq.ToString(),
            MthostIpcQueuedSeq = (transport?.IpcQueuedSeq ?? 0UL).ToString(),
            MthostIpcFlushedSeq = (transport?.IpcFlushedSeq ?? 0UL).ToString(),
            IpcBacklogFrames = transport?.IpcBacklogFrames ?? 0,
            IpcBacklogBytes = transport?.IpcBacklogBytes ?? 0,
            OldestBacklogAgeMs = transport?.OldestBacklogAgeMs ?? 0,
            ScrollbackBytes = transport?.ScrollbackBytes ?? 0,
            LastReplayBytes = Math.Max(transport?.LastReplayBytes ?? 0, runtime.LastReplayBytes),
            LastReplayReason = (runtime.LastReplayReason ?? transport?.LastReplayReason)?.ToString(),
            ReconnectCount = runtime.ReconnectCount,
            DataLossCount = Math.Max(transport?.DataLossCount ?? 0, runtime.DataLossCount),
            LastDataLossReason = (runtime.LastDataLossReason ?? transport?.LastDataLossReason)?.ToString()
        };
    }

    private static SessionInfoDto GetSessionDto(
        TtyHostSessionManager sessionManager,
        SessionSupervisorService sessionSupervisor,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        string sessionId)
    {
        return GetSessionListDto(sessionManager, sessionSupervisor, appServerControlRuntime).Sessions.First(s => s.Id == sessionId);
    }

    internal sealed record SessionPromptExecutionPlan(
        byte[]? InterruptData,
        byte[] PromptData,
        byte[] SubmitData,
        int InterruptDelayMs,
        int SubmitDelayMs,
        int FollowupSubmitCount,
        int FollowupSubmitDelayMs);

    private static IResult CreateSessionLaunchProblem(
        SessionLaunchFailure? failure,
        string title = "Session launch failed")
    {
        var statusCode = failure?.Stage == "limits"
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status500InternalServerError;
        var problem = new ProblemDetails
        {
            Title = title,
            Status = statusCode,
            Detail = failure?.Message ?? "Failed to create session."
        };

        if (failure is not null)
        {
            problem.Extensions["errorStage"] = failure.Stage;
            if (!string.IsNullOrWhiteSpace(failure.Detail))
            {
                problem.Extensions["errorDetails"] = failure.Detail;
            }
            if (!string.IsNullOrWhiteSpace(failure.ExceptionType))
            {
                problem.Extensions["exceptionType"] = failure.ExceptionType;
            }
            if (failure.NativeErrorCode is not null)
            {
                problem.Extensions["nativeErrorCode"] = failure.NativeErrorCode.Value;
            }
        }

        return Results.Problem(problem);
    }
}
