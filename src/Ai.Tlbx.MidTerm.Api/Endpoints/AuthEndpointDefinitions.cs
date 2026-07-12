using Ai.Tlbx.MidTerm.Api.Handlers;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Ai.Tlbx.MidTerm.Models.Auth;
namespace Ai.Tlbx.MidTerm.Api.Endpoints;

public static class AuthEndpointDefinitions
{
    public static IEndpointRouteBuilder MapAuthApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", (LoginRequest request, HttpContext ctx, IAuthHandler handler) =>
            handler.Login(request, ctx))
            .Produces<AuthResponse>(StatusCodes.Status200OK, "application/json");

        app.MapPost("/api/auth/logout", (HttpContext ctx, IAuthHandler handler) =>
            handler.Logout(ctx))
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/auth/refresh", (HttpContext ctx, IAuthHandler handler) =>
            handler.Refresh(ctx))
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapPost("/api/auth/change-password", (ChangePasswordRequest request, HttpContext ctx, IAuthHandler handler) =>
            handler.ChangePassword(request, ctx))
            .Produces<AuthResponse>(StatusCodes.Status200OK, "application/json");

        app.MapGet("/api/auth/status", (IAuthHandler handler) =>
            handler.GetStatus())
            .Produces<AuthStatusResponse>(StatusCodes.Status200OK, "application/json");
        return app;
    }
}
