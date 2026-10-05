using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Slideshow.Api.Identity;

/// <summary>
/// Development-only sign-in for the website while there is no Entra External ID tenant: issues tokens
/// signed with the `dotnet user-jwts` key, which Development already trusts. Never mapped outside
/// Development, and only when that key exists.
/// </summary>
public static class DevTokens
{
    private const string Issuer = "dotnet-user-jwts";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    public sealed record DevTokenRequest(string? Email, string? Name);
    public sealed record DevTokenResponse(string AccessToken, DateTime ExpiresAt, string UserId);

    public static void MapDevTokens(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        var bearer = app.Configuration.GetSection("Authentication:Schemes:Bearer");
        var keyConfig = bearer.GetSection("SigningKeys").GetChildren().FirstOrDefault(k => k["Issuer"] == Issuer);
        var audience = bearer.GetSection("ValidAudiences").GetChildren().FirstOrDefault()?.Value;
        if (keyConfig?["Value"] is not { } keyValue || audience is null)
        {
            app.Logger.LogInformation("Development sign-in is off: run `dotnet user-jwts create` once to set up a signing key.");
            return;
        }
        var key = new SymmetricSecurityKey(Convert.FromBase64String(keyValue)) { KeyId = keyConfig["Id"] };

        app.MapPost("/dev/token", (DevTokenRequest body, Microsoft.Extensions.Options.IOptions<AuthOptions> auth) =>
        {
            var email = body.Email?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(email) || !email.Contains('@') || email.Length > 320)
                return Problems.Validation("Enter an email address.");
            var name = string.IsNullOrWhiteSpace(body.Name) ? email.Split('@')[0] : body.Name.Trim();

            // The same email always signs in as the same user.
            var oid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("dev-user:" + email)).AsSpan(0, 16)).ToString();
            // iat is whole seconds; start at the next second so a token issued right after a logout isn't caught by its cut-off.
            var now = DateTime.UtcNow;
            var issuedAt = now.AddTicks(TimeSpan.TicksPerSecond - now.Ticks % TimeSpan.TicksPerSecond);
            var expires = issuedAt + Lifetime;
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = audience,
                IssuedAt = issuedAt,
                NotBefore = issuedAt.AddMinutes(-1),
                Expires = expires,
                Claims = new Dictionary<string, object>
                {
                    ["oid"] = oid, ["sub"] = oid, ["name"] = name, ["email"] = email, ["scope"] = auth.Value.RequiredScope,
                },
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            });
            return Results.Ok(new DevTokenResponse(token, expires, oid));
        }).AllowAnonymous().ExcludeFromDescription();

        app.Logger.LogWarning("Development sign-in is ON at POST /dev/token. Never enable Development in a deployed environment.");
    }
}
