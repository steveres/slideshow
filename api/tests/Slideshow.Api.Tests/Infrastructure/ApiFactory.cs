using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Slideshow.Api.Identity;

namespace Slideshow.Api.Tests.Infrastructure;

/// <summary>
/// The real app with SQLite + a temp folder for storage, and tokens signed by a test key in place of
/// Entra's. Everything else (JWT validation, scope check, logout cut-off) is the production pipeline.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://login.test/tenant-id/v2.0";
    public const string Audience = "api-client-id";
    public const string Scope = "Slideshow.Access";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-signing-key-that-is-long-enough-for-hs256!"));

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "slideshow-tests", Guid.NewGuid().ToString("N"));
    public string MediaRoot => Path.Combine(Root, "media");
    public FakeIdentityDirectory Directory { get; } = new();

    /// <summary>Configuration overrides, e.g. smaller limits.</summary>
    public Dictionary<string, string?> Settings { get; init; } = [];

    /// <summary>Hosting environment. In "Development" the test-key token setup is skipped, so the app's own
    /// Development token configuration (user-jwts style) applies.</summary>
    public string Environment { get; init; } = "Testing";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        var testTokens = Environment != "Development";
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:ConnectionString", $"Data Source={Path.Combine(Root, "test.db")};Pooling=False");
        builder.UseSetting("Storage:Provider", "FileSystem");
        builder.UseSetting("Storage:RootPath", MediaRoot);
        if (testTokens)
        {
            builder.UseSetting("Auth:Authority", "https://login.test/tenant-id/v2.0");
            builder.UseSetting("Auth:Audiences:0", Audience);
        }
        builder.UseSetting("Auth:RequiredScope", Scope);
        builder.UseSetting("Cors:AllowedOrigins:0", "https://spa.test");
        foreach (var (k, v) in Settings) builder.UseSetting(k, v);

        builder.ConfigureTestServices(services =>
        {
            if (testTokens)
            {
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                    config.SigningKeys.Add(Key);
                    o.Configuration = config; // no metadata download
                });
            }
            services.AddScoped<IIdentityDirectory>(_ => Directory);
        });
    }

    public static string Token(string oid, string? scope = Scope, string audience = Audience, DateTime? issuedAt = null,
        SecurityKey? key = null, string name = "Test User")
    {
        var iat = issuedAt ?? DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["oid"] = oid, ["name"] = name, ["email"] = $"{oid}@example.test" };
        if (scope is not null) claims["scp"] = scope;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = iat,
            NotBefore = iat.AddMinutes(-1),
            Expires = iat.AddHours(1),
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>A client acting as the given user.</summary>
    public HttpClient ClientFor(string oid, string? token = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token(oid));
        return client;
    }

    /// <summary>A client for a user who has already registered.</summary>
    public async Task<HttpClient> RegisteredClientAsync(string oid)
    {
        var client = ClientFor(oid);
        (await client.PostAsync("/api/v1/account", null)).EnsureSuccessStatusCode();
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { System.IO.Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeIdentityDirectory : IIdentityDirectory
{
    public ConcurrentQueue<string> Revoked { get; } = new();
    public ConcurrentQueue<string> Deleted { get; } = new();
    public bool FailDeletes { get; set; }

    public Task RevokeSessionsAsync(string userId, CancellationToken ct) { Revoked.Enqueue(userId); return Task.CompletedTask; }

    public Task DeleteUserAsync(string userId, CancellationToken ct)
    {
        if (FailDeletes) throw new HttpRequestException("Graph unavailable");
        Deleted.Enqueue(userId);
        return Task.CompletedTask;
    }
}
