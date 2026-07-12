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

public class StubAuthHandler : IAuthHandler
{
    public IResult Login(LoginRequest request, HttpContext ctx) =>
        Results.Json(new AuthResponse { Success = true });

    public IResult Logout(HttpContext ctx) =>
        Results.Ok();

    public IResult Refresh(HttpContext ctx) =>
        Results.NoContent();

    public IResult ChangePassword(ChangePasswordRequest request, HttpContext ctx) =>
        Results.Json(new AuthResponse { Success = true });

    public IResult GetStatus() =>
        Results.Json(new AuthStatusResponse { AuthenticationEnabled = true, PasswordSet = true });
}
