using System.Text.Json;
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

public interface ISystemHandler
{
    IResult GetBootstrap();
    IResult GetBootstrapLogin();
    IResult GetSystem();
    IResult GetVersion();
    IResult GetHealth();
    IResult GetVersionDetails();
    IResult GetCertificateInfo();
    IResult DownloadCertificatePem();
    IResult DownloadMobileConfig(HttpContext context);
    IResult GetSharePacket(HttpContext context);
    IResult GetNetworks();
    IResult GetShells();
    IResult GetAgentControllerInstallations();
    IResult GetUsers();
    IResult GetSettings();
    IResult UpdateSettings(MidTermSettingsPublic settings);
    IResult PatchSettings(JsonElement patch);
    IResult ReloadSettings();
    IResult GetPaths();
    Task<IResult> CheckUpdateAsync();
    Task<IResult> ApplyUpdateAsync(string? source, bool forceFull = false);
    IResult GetUpdateResult(bool clear);
    IResult DeleteUpdateResult();
    IResult GetUpdateLog();
}
