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
namespace Ai.Tlbx.MidTerm.Api.Handlers;

public interface ISessionHandler
{
    IResult GetState();
    IResult GetSessions();
    IResult GetSessionAttention(bool agentOnly);
    Task<IResult> CreateSessionAsync(CreateSessionRequest? request);
    Task<IResult> BootstrapWorkerAsync(WorkerBootstrapRequest request);
    IResult ReorderSessions(SessionReorderRequest request);
    Task<IResult> DeleteSessionAsync(string id);
    Task<IResult> ResizeSessionAsync(string id, ResizeRequest request);
    Task<IResult> GetSessionStateAsync(string id, bool includeBuffer, bool includeBufferBase64);
    Task<IResult> SendRawInputAsync(string id, byte[] body);
    Task<IResult> SendTextInputAsync(string id, SessionInputRequest request);
    Task<IResult> SendKeyInputAsync(string id, SessionKeyInputRequest request);
    Task<IResult> SendPromptInputAsync(string id, SessionPromptRequest request);
    Task<IResult> GetBufferAsync(string id);
    Task<IResult> GetBufferTextAsync(string id, bool includeBase64);
    Task<IResult> GetBufferTailAsync(string id, int lines, bool stripAnsi);
    Task<IResult> GetActivityAsync(string id, int seconds, int bellLimit);
    Task<IResult> GetAgentVibeAsync(string id, int tailLines, int activitySeconds, int bellLimit);
    Task<IResult> GetAgentFeedAsync(string id, int tailLines, int activitySeconds, int bellLimit);
    Task<IResult> RenameSessionAsync(string id, RenameSessionRequest request, bool auto);
    Task<IResult> SetSessionNotesAsync(string id, SetSessionNotesRequest request);
    Task<IResult> SetSessionTopicAsync(string id, SetSessionTopicRequest request);
    Task<IResult> SetSessionControlAsync(string id, SetSessionControlRequest request);
    Task<IResult> UploadFileAsync(string id, IFormFile file);
    Task<IResult> PasteClipboardImageAsync(string id, IFormFile file);
    IResult InjectGuidance(string id);
    IResult SetBookmark(string id, SetBookmarkRequest request);
}
