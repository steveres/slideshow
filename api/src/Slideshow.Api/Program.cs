using System.Security.Claims;
using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Scalar.AspNetCore;
using Slideshow.Api;
using Slideshow.Api.Data;
using Slideshow.Api.Endpoints;
using Slideshow.Api.Identity;
using Slideshow.Api.Storage;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddOptions<AuthOptions>().BindConfiguration("Auth");
services.AddOptions<DatabaseOptions>().BindConfiguration("Database");
services.AddOptions<StorageOptions>().BindConfiguration("Storage");
services.AddOptions<GraphOptions>().BindConfiguration("Graph");
services.AddOptions<LimitsOptions>().BindConfiguration("Limits");
services.AddOptions<CorsOptions>().BindConfiguration("Cors");

// Database: Azure SQL in the cloud, SQLite locally and in tests.
services.AddDbContext<AppDbContext>((sp, o) =>
{
    var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    if (db.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        o.UseSqlServer(db.ConnectionString, sql => sql.EnableRetryOnFailure());
    else
        o.UseSqlite(db.ConnectionString);
});

// Media bytes: Azure Blob Storage in the cloud, a folder locally.
services.AddSingleton<IBlobStore>(sp =>
    sp.GetRequiredService<IOptions<StorageOptions>>().Value.Provider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<AzureBlobStore>(sp)
        : ActivatorUtilities.CreateInstance<FileSystemBlobStore>(sp));

// Entra user operations via Microsoft Graph, when configured.
services.AddHttpClient<GraphIdentityDirectory>();
services.AddSingleton<NoOpIdentityDirectory>();
services.AddScoped<IIdentityDirectory>(sp => sp.GetRequiredService<IOptions<GraphOptions>>().Value.Enabled
    ? sp.GetRequiredService<GraphIdentityDirectory>()
    : sp.GetRequiredService<NoOpIdentityDirectory>());

services.AddSlideshowAuth();
services.AddProblemDetails();
services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    // Lets the API explorer send "Authorization: Bearer <token>".
    var scheme = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" };
    doc.Components ??= new OpenApiComponents();
    doc.Components.SecuritySchemes["Bearer"] = scheme;
    doc.SecurityRequirements.Add(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
    return Task.CompletedTask;
}));
services.AddCors();
services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var perMinute = http.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.RequestsPerMinute;
        var key = http.User.FindFirstValue("oid") ?? "ip:" + http.Connection.RemoteIpAddress;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });
    });
});
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    services.AddOpenTelemetry().UseAzureMonitor();

var app = builder.Build();

await InitializeStorageAsync(app.Services);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use((http, next) =>
{
    var h = http.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    // API responses are JSON or media bytes; neither should ever be interpreted as a page.
    // (The development-only API explorer under /scalar is a page, so it's left out.)
    if (!http.Request.Path.StartsWithSegments("/scalar"))
        h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; sandbox";
    return next();
});
var cors = app.Services.GetRequiredService<IOptions<CorsOptions>>().Value;
app.UseCors(p => p.WithOrigins(cors.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("Location", "Content-Disposition", "Content-Range", "Accept-Ranges", "ETag")
    .SetPreflightMaxAge(TimeSpan.FromHours(1)));
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                                                      // /openapi/v1.json
    app.MapScalarApiReference(o => o.WithTitle("Slideshow API"));         // /scalar: browse and try the API
    app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
}
app.MapDevTokens(); // Development only: website sign-in without an Entra tenant
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous().DisableRateLimiting().ExcludeFromDescription();

var api = app.MapGroup("/api/v1").RequireAuthorization(Auth.ApiPolicy);
api.MapAccount();
api.MapAlbums(app.Services.GetRequiredService<IOptions<LimitsOptions>>().Value.MaxUploadBytes);
app.MapGroup("/api/v1/shared").AllowAnonymous().MapShared(); // share links: no sign-in

app.Run();

static async Task InitializeStorageAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var options = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    if (options.MigrateOnStartup)
    {
        if (db.Database.IsSqlServer()) await db.Database.MigrateAsync(); // EF takes a lock, so replicas can start together
        else
        {
            if (db.Database.GetDbConnection().DataSource is { Length: > 0 } file && Path.GetDirectoryName(Path.GetFullPath(file)) is { } dir)
                Directory.CreateDirectory(dir);
            await db.Database.EnsureCreatedAsync(); // SQLite (local/tests): no migrations, schema from the model
        }
    }
    if (scope.ServiceProvider.GetRequiredService<IBlobStore>() is AzureBlobStore azure) await azure.EnsureContainerAsync(default);
}

public partial class Program; // for WebApplicationFactory in tests
