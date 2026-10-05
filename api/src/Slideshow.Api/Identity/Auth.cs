using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Slideshow.Api.Data;

namespace Slideshow.Api.Identity;

public static class Auth
{
    public const string ApiPolicy = "SlideshowApi";
    private const string AccountItem = "slideshow.account";

    public static IServiceCollection AddSlideshowAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((o, auth) =>
            {
                // When Auth is empty, settings from "Authentication:Schemes:Bearer" apply instead
                // (that's where `dotnet user-jwts` puts its local signing key for development).
                if (!string.IsNullOrEmpty(auth.Value.Authority)) o.Authority = auth.Value.Authority;
                if (auth.Value.Audiences.Length > 0) o.TokenValidationParameters.ValidAudiences = auth.Value.Audiences;
                o.MapInboundClaims = false; // keep "oid", "scp", "name" as issued
                o.TokenValidationParameters.NameClaimType = "name";
                o.Events = new JwtBearerEvents { OnTokenValidated = OnTokenValidated };
            });

        services.AddAuthorizationBuilder().AddPolicy(ApiPolicy, p => p
            .RequireAuthenticatedUser()
            .RequireClaim("oid")
            .RequireAssertion(ctx =>
            {
                var scope = ctx.Resource is HttpContext http
                    ? http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.RequiredScope
                    : null;
                return scope is null || HasScope(ctx.User, scope);
            }));
        return services;
    }

    /// <summary>Entra puts delegated scopes in "scp"; the OAuth standard (and dotnet user-jwts) uses "scope".</summary>
    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.Claims.Where(c => c.Type is "scp" or "scope").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(scope, StringComparer.Ordinal);

    /// <summary>Loads the caller's account and rejects tokens issued before their last logout.</summary>
    private static async Task OnTokenValidated(TokenValidatedContext ctx)
    {
        var oid = ctx.Principal?.FindFirstValue("oid");
        if (string.IsNullOrEmpty(oid)) { ctx.Fail("Token has no oid claim."); return; }

        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == oid, ctx.HttpContext.RequestAborted);
        if (account?.TokensValidAfter is { } cutoff)
        {
            var issuedAt = ctx.SecurityToken is JsonWebToken jwt ? jwt.IssuedAt : DateTime.MinValue;
            if (issuedAt < cutoff) { ctx.Fail("Token was issued before the user signed out."); return; }
        }
        if (account is not null) ctx.HttpContext.Items[AccountItem] = account;
    }

    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("oid") ?? throw new InvalidOperationException("No oid claim.");

    /// <summary>The caller's API account, or null if they haven't registered.</summary>
    public static User? Account(this HttpContext http) => http.Items[AccountItem] as User;
}
