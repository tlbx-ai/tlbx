using Ai.Tlbx.MidTerm.Api.Handlers;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Http;

using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
namespace Ai.Tlbx.MidTerm.OpenApi.Stubs;

public class StubSessionHandler : ISessionHandler
{
    public IResult GetState() =>
        Results.Json(new StateUpdate());

    public IResult GetSessions() =>
        Results.Json(new SessionListDto());

    public IResult GetSessionAttention(bool agentOnly) =>
        Results.Json(new SessionAttentionResponse { AgentOnly = agentOnly });

    public Task<IResult> CreateSessionAsync(CreateSessionRequest? request) =>
        Task.FromResult<IResult>(Results.Json(new SessionInfoDto { Id = "stub" }));

    public Task<IResult> BootstrapWorkerAsync(WorkerBootstrapRequest request) =>
        Task.FromResult<IResult>(Results.Json(new WorkerBootstrapResponse
        {
            Session = new SessionInfoDto { Id = "stub", AgentControlled = request.AgentControlled },
            Profile = request.Profile ?? "unknown",
            LaunchCommand = request.LaunchCommand,
            SlashCommands = request.SlashCommands,
            GuidanceInjected = request.InjectGuidance
        }));

    public IResult ReorderSessions(SessionReorderRequest request) =>
        Results.Ok();

    public Task<IResult> DeleteSessionAsync(string id) =>
        Task.FromResult<IResult>(Results.Ok());

    public Task<IResult> ResizeSessionAsync(string id, ResizeRequest request) =>
        Task.FromResult<IResult>(Results.Json(new ResizeResponse { Accepted = true }));

    public Task<IResult> GetSessionStateAsync(string id, bool includeBuffer, bool includeBufferBase64) =>
        Task.FromResult<IResult>(Results.Json(new SessionStateResponse
        {
            Session = new SessionInfoDto { Id = id }
        }));

    public Task<IResult> SendRawInputAsync(string id, byte[] body) =>
        Task.FromResult<IResult>(Results.Ok());

    public Task<IResult> SendTextInputAsync(string id, SessionInputRequest request) =>
        Task.FromResult<IResult>(Results.Ok());

    public Task<IResult> SendKeyInputAsync(string id, SessionKeyInputRequest request) =>
        Task.FromResult<IResult>(Results.Ok());

    public Task<IResult> SendPromptInputAsync(string id, SessionPromptRequest request) =>
        Task.FromResult<IResult>(Results.Ok());

    public Task<IResult> GetBufferAsync(string id) =>
        Task.FromResult<IResult>(Results.Bytes(Array.Empty<byte>(), "application/octet-stream"));

    public Task<IResult> GetBufferTextAsync(string id, bool includeBase64) =>
        Task.FromResult<IResult>(Results.Json(new SessionBufferTextResponse { SessionId = id }));

    public Task<IResult> GetBufferTailAsync(string id, int lines, bool stripAnsi) =>
        Task.FromResult<IResult>(Results.Text("", "text/plain"));

    public Task<IResult> GetActivityAsync(string id, int seconds, int bellLimit) =>
        Task.FromResult<IResult>(Results.Json(new SessionActivityResponse { SessionId = id }));

    public Task<IResult> GetAgentVibeAsync(string id, int tailLines, int activitySeconds, int bellLimit) =>
        Task.FromResult<IResult>(Results.Json(new AgentSessionVibeResponse { SessionId = id }));

    public Task<IResult> GetAgentFeedAsync(string id, int tailLines, int activitySeconds, int bellLimit) =>
        Task.FromResult<IResult>(Results.Json(new AgentSessionFeedResponse { SessionId = id }));

    public Task<IResult> RenameSessionAsync(string id, RenameSessionRequest request, bool auto) =>
        Task.FromResult<IResult>(Results.Ok());

    public Task<IResult> SetSessionNotesAsync(string id, SetSessionNotesRequest request) =>
        Task.FromResult<IResult>(Results.Json(new SessionInfoDto
        {
            Id = id,
            Notes = request.Notes
        }));

    public Task<IResult> SetSessionTopicAsync(string id, SetSessionTopicRequest request) =>
        Task.FromResult<IResult>(Results.Json(new SessionInfoDto
        {
            Id = id,
            Topic = request.Topic
        }));

    public Task<IResult> SetSessionControlAsync(string id, SetSessionControlRequest request) =>
        Task.FromResult<IResult>(Results.Json(new SessionInfoDto
        {
            Id = id,
            AgentControlled = request.AgentControlled
        }));

    public Task<IResult> UploadFileAsync(string id, IFormFile file) =>
        Task.FromResult<IResult>(Results.Json(new FileUploadResponse { Path = "/tmp/file" }));

    public Task<IResult> PasteClipboardImageAsync(string id, IFormFile file) =>
        Task.FromResult<IResult>(Results.Ok());

    public IResult InjectGuidance(string id) =>
        Results.Json(new InjectGuidanceResponse { MidtermDir = ".midterm" });

    public IResult SetBookmark(string id, SetBookmarkRequest request) =>
        Results.Ok();
}
