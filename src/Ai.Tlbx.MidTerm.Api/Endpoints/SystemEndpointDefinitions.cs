using System.Text.Json;
using Ai.Tlbx.MidTerm.Api.Handlers;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
namespace Ai.Tlbx.MidTerm.Api.Endpoints;

public static class SystemEndpointDefinitions
{
    public static IEndpointRouteBuilder MapSystemApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bootstrap", (ISystemHandler handler) =>
            handler.GetBootstrap())
            .Produces<BootstrapResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/bootstrap/login", (ISystemHandler handler) =>
            handler.GetBootstrapLogin())
            .Produces<BootstrapLoginResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/system", (ISystemHandler handler) =>
            handler.GetSystem())
            .Produces<SystemResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/version", (ISystemHandler handler) =>
            handler.GetVersion())
            .Produces<string>(StatusCodes.Status200OK, "text/plain");

        app.MapGet("/api/health", (ISystemHandler handler) =>
            handler.GetHealth())
            .Produces<SystemHealth>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/version/details", (ISystemHandler handler) =>
            handler.GetVersionDetails())
            .Produces<VersionManifest>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/certificate/info", (ISystemHandler handler) =>
            handler.GetCertificateInfo())
            .Produces<CertificateInfoResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/certificate/download/pem", (ISystemHandler handler) =>
            handler.DownloadCertificatePem())
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/api/certificate/download/mobileconfig", (HttpContext ctx, ISystemHandler handler) =>
            handler.DownloadMobileConfig(ctx))
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/api/certificate/share-packet", (HttpContext ctx, ISystemHandler handler) =>
            handler.GetSharePacket(ctx))
            .Produces<SharePacketInfo>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/networks", (ISystemHandler handler) =>
            handler.GetNetworks())
            .Produces<List<NetworkInterfaceDto>>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/shells", (ISystemHandler handler) =>
            handler.GetShells())
            .Produces<List<ShellInfoDto>>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/agent-controller/installations", (ISystemHandler handler) =>
            handler.GetAgentControllerInstallations())
            .Produces<List<AgentControllerInstallationDto>>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/users", (ISystemHandler handler) =>
            handler.GetUsers())
            .Produces<List<UserInfo>>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/settings", (ISystemHandler handler) =>
            handler.GetSettings())
            .Produces<MidTermSettingsPublic>(StatusCodes.Status200OK, "application/json");

        app.MapPut("/api/settings", (MidTermSettingsPublic settings, ISystemHandler handler) =>
            handler.UpdateSettings(settings))
            .Produces(StatusCodes.Status200OK);

        app.MapPatch("/api/settings", (JsonElement patch, ISystemHandler handler) =>
            handler.PatchSettings(patch))
            .Accepts<JsonElement>("application/json")
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/settings/reload", (ISystemHandler handler) =>
            handler.ReloadSettings())
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/api/paths", (ISystemHandler handler) =>
            handler.GetPaths())
            .Produces<PathsResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/update/check", async (ISystemHandler handler) =>
            await handler.CheckUpdateAsync())
            .Produces<UpdateInfo>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/update/apply", async (ISystemHandler handler, string? source, bool forceFull = false) =>
            await handler.ApplyUpdateAsync(source, forceFull))
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/api/update/result", (ISystemHandler handler, bool clear = false) =>
            handler.GetUpdateResult(clear))
            .Produces<UpdateResult>(StatusCodes.Status200OK, "application/json");

        app.MapDelete("/api/update/result", (ISystemHandler handler) =>
            handler.DeleteUpdateResult())
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/api/update/log", (ISystemHandler handler) =>
            handler.GetUpdateLog())
            .Produces<string>(StatusCodes.Status200OK, "text/plain");

        return app;
    }
}
