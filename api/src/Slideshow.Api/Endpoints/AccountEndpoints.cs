using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slideshow.Api.Data;
using Slideshow.Api.Identity;
using Slideshow.Api.Storage;

namespace Slideshow.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccount(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/account").WithTags("Account");

        g.MapPost("/", Register).WithSummary("Register: create the caller's API account (idempotent).");
        g.MapGet("/", Get).WithSummary("The caller's account, usage and quota.");
        g.MapPost("/logout", Logout).WithSummary("Sign out everywhere: rejects all tokens issued until now.");
        g.MapDelete("/", Delete).WithSummary("Delete the account, all albums and files, and the Entra user.");
    }

    private static async Task<IResult> Register(HttpContext http, AppDbContext db, IOptions<LimitsOptions> limits, ILogger<AppDbContext> log, CancellationToken ct)
    {
        if (http.Account() is { } existing) return Results.Ok(await ToDto(db, existing, limits.Value, ct));

        var user = new User
        {
            Id = http.User.UserId(),
            DisplayName = Truncate(http.User.FindFirstValue("name"), 256),
            Email = Truncate(http.User.FindFirstValue("email") ?? http.User.FindFirstValue("preferred_username"), 320),
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two registrations raced; the other one won.
            db.ChangeTracker.Clear();
            var winner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == user.Id, ct);
            if (winner is null) throw;
            return Results.Ok(await ToDto(db, winner, limits.Value, ct));
        }
        log.LogInformation("Registered user {UserId}", user.Id);
        return Results.Created("/api/v1/account", await ToDto(db, user, limits.Value, ct));
    }

    private static async Task<IResult> Get(HttpContext http, AppDbContext db, IOptions<LimitsOptions> limits, CancellationToken ct) =>
        http.Account() is { } user ? Results.Ok(await ToDto(db, user, limits.Value, ct)) : Problems.NotRegistered();

    private static async Task<IResult> Logout(HttpContext http, AppDbContext db, IIdentityDirectory directory, ILogger<AppDbContext> log, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.TokensValidAfter, DateTime.UtcNow), ct);
        try { await directory.RevokeSessionsAsync(userId, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Our cut-off already rejects the old access tokens; Entra's refresh tokens will lapse on their own.
            log.LogWarning(e, "Could not revoke Entra sessions for {UserId}", userId);
        }
        return Results.NoContent();
    }

    /// <summary>Data first, identity last: if any step fails the user can still sign in and retry.</summary>
    private static async Task<IResult> Delete(HttpContext http, AppDbContext db, IBlobStore blobs, IIdentityDirectory directory, ILogger<AppDbContext> log, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.MediaFiles.Where(m => m.OwnerId == userId).ExecuteDeleteAsync(ct);
            await db.Albums.Where(a => a.OwnerId == userId).ExecuteDeleteAsync(ct);
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        });
        await blobs.DeletePrefixAsync(BlobNames.UserPrefix(userId), ct);

        try { await directory.DeleteUserAsync(userId, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Data for {UserId} was deleted but the Entra user could not be", userId);
            return Problems.Of(502, "identity_delete_failed", "Your data was deleted, but your sign-in could not be removed. Try again.");
        }
        log.LogInformation("Deleted account {UserId}", userId);
        return Results.NoContent();
    }

    private static async Task<AccountDto> ToDto(AppDbContext db, User u, LimitsOptions limits, CancellationToken ct)
    {
        var albums = await db.Albums.CountAsync(a => a.OwnerId == u.Id, ct);
        var files = await db.MediaFiles.Where(m => m.OwnerId == u.Id).GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Bytes = g.Sum(m => m.SizeBytes) }).FirstOrDefaultAsync(ct);
        return new AccountDto(u.Id, u.DisplayName, u.Email, u.CreatedAt,
            new UsageDto(albums, files?.Count ?? 0, files?.Bytes ?? 0, limits.QuotaBytesPerUser));
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
