using System.Text.Json;
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

public class StubSystemHandler : ISystemHandler
{
    public IResult GetBootstrap() =>
        Results.Json(new BootstrapResponse());

    public IResult GetBootstrapLogin() =>
        Results.Json(new BootstrapLoginResponse());

    public IResult GetSystem() =>
        Results.Json(new SystemResponse { Healthy = true });

    public IResult GetVersion() =>
        Results.Text("1.0.0");

    public IResult GetHealth() =>
        Results.Json(new SystemHealth { Healthy = true });

    public IResult GetVersionDetails() =>
        Results.Json(new VersionManifest { Web = "1.0.0", Pty = "1.0.0" });

    public IResult GetCertificateInfo() =>
        Results.Json(new CertificateInfoResponse());

    public IResult DownloadCertificatePem() =>
        Results.File(Array.Empty<byte>(), "application/x-pem-file", "midterm.pem");

    public IResult DownloadMobileConfig(HttpContext context) =>
        Results.File(Array.Empty<byte>(), "application/x-apple-aspen-config", "midterm.mobileconfig");

    public IResult GetSharePacket(HttpContext context) =>
        Results.Json(new SharePacketInfo());

    public IResult GetNetworks() =>
        Results.Json(new List<NetworkInterfaceDto>());

    public IResult GetShells() =>
        Results.Json(new List<ShellInfoDto>());

    public IResult GetAgentControllerInstallations() =>
        Results.Json(new List<AgentControllerInstallationDto>());

    public IResult GetUsers() =>
        Results.Json(new List<UserInfo>());

    public IResult GetSettings() =>
        Results.Json(new MidTermSettingsPublic());

    public IResult UpdateSettings(MidTermSettingsPublic settings) =>
        Results.Ok();

    public IResult PatchSettings(JsonElement patch) =>
        Results.Ok();

    public IResult ReloadSettings() =>
        Results.Json(new MidTermSettingsPublic());

    public IResult GetPaths() =>
        Results.Json(new PathsResponse());

    public Task<IResult> CheckUpdateAsync() =>
        Task.FromResult<IResult>(Results.Json(new UpdateInfo { Available = false }));

    public Task<IResult> ApplyUpdateAsync(string? source, bool forceFull = false) =>
        Task.FromResult<IResult>(Results.Ok("Update started."));

    public IResult GetUpdateResult(bool clear) =>
        Results.Json(new UpdateResult { Found = false });

    public IResult DeleteUpdateResult() =>
        Results.Ok();

    public IResult GetUpdateLog() =>
        Results.Text("No update log.");
}
