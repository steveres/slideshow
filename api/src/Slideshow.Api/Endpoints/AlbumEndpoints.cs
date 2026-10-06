using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slideshow.Api.Data;
using Slideshow.Api.Identity;
using Slideshow.Api.Media;
using Slideshow.Api.Storage;

namespace Slideshow.Api.Endpoints;

public static class AlbumEndpoints
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    public static void MapAlbums(this RouteGroupBuilder api, long maxUploadBytes)
    {
        var g = api.MapGroup("/albums").WithTags("Albums").AddEndpointFilter(RequireRegistered);

        g.MapGet("/", List).WithSummary("The caller's albums, newest first.");
        g.MapPost("/", Create).WithSummary("Create an album.");
        g.MapGet("/{albumId:guid}", Get).WithSummary("The compiled, playable album (manifest).");
        g.MapGet("/{albumId:guid}/info", GetInfo).WithSummary("Album details and counts.");
        g.MapPost("/{albumId:guid}/compile", Compile).WithSummary("Order the slides by date taken and store the manifest.");
        g.MapDelete("/{albumId:guid}", Delete).WithSummary("Delete the album with its images and music.");

        MediaEndpoints.MapCollection(g, "images", MediaCollection.Images, maxUploadBytes);
        MediaEndpoints.MapCollection(g, "music", MediaCollection.Music, maxUploadBytes);
    }

    public static async ValueTask<object?> RequireRegistered(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
        ctx.HttpContext.Account() is null ? Problems.NotRegistered() : await next(ctx);

    private static IQueryable<AlbumInfoDto> Infos(IQueryable<Album> albums) =>
        albums.Select(a => new AlbumInfoDto(
            a.Id, a.Name, a.Description, a.CreatedAt, a.UpdatedAt,
            a.Media.Count(m => m.Kind == MediaKind.Image),
            a.Media.Count(m => m.Kind == MediaKind.Video),
            a.Media.Count(m => m.Collection == MediaCollection.Music),
            // Keep in step with MediaInfoDto.MissingFor.
            a.Media.Count(m => m.Collection == MediaCollection.Images && (m.TakenAt == null || m.Latitude == null || m.Longitude == null)),
            a.Media.Sum(m => (long?)m.SizeBytes) ?? 0,
            a.CompiledAt,
            a.CompiledVersion == null || a.CompiledVersion != a.ContentVersion));

    private static async Task<IResult> List(HttpContext http, AppDbContext db, CancellationToken ct) =>
        Results.Ok(await Infos(db.Albums.Where(a => a.OwnerId == http.User.UserId()).OrderByDescending(a => a.CreatedAt)).ToListAsync(ct));

    private static async Task<IResult> GetInfo(Guid albumId, HttpContext http, AppDbContext db, CancellationToken ct) =>
        await Infos(db.Albums.Where(a => a.Id == albumId && a.OwnerId == http.User.UserId())).FirstOrDefaultAsync(ct) is { } info
            ? Results.Ok(info) : Problems.AlbumNotFound();

    private static async Task<IResult> Create(CreateAlbumRequest body, HttpContext http, AppDbContext db, IOptions<LimitsOptions> limits, CancellationToken ct)
    {
        var name = body.Name?.Trim();
        var description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        if (string.IsNullOrEmpty(name)) return Problems.Validation("Name is required.");
        if (name.Length > Limits.AlbumNameLength) return Problems.Validation($"Name can be at most {Limits.AlbumNameLength} characters.");
        if (description?.Length > Limits.AlbumDescriptionLength) return Problems.Validation($"Description can be at most {Limits.AlbumDescriptionLength} characters.");

        var ownerId = http.User.UserId();
        if (await db.Albums.CountAsync(a => a.OwnerId == ownerId, ct) >= limits.Value.MaxAlbumsPerUser)
            return Problems.Of(409, "album_limit_reached", $"You can have at most {limits.Value.MaxAlbumsPerUser} albums.");

        var now = DateTime.UtcNow;
        var album = new Album { Id = Guid.CreateVersion7(), OwnerId = ownerId, Name = name, Description = description, CreatedAt = now, UpdatedAt = now };
        db.Albums.Add(album);
        await db.SaveChangesAsync(ct);
        var info = new AlbumInfoDto(album.Id, album.Name, album.Description, now, now, 0, 0, 0, 0, 0, null, true);
        return Results.Created($"/api/v1/albums/{album.Id}/info", info);
    }

    private static async Task<IResult> Get(Guid albumId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var ownerId = http.User.UserId();
        var album = await db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId && a.OwnerId == ownerId)
            .Select(a => new { a.ManifestJson, a.ContentVersion, a.CompiledVersion })
            .FirstOrDefaultAsync(ct);
        if (album is null) return Problems.AlbumNotFound();
        if (album.ManifestJson is null) return Problems.Of(409, "album_not_compiled", "Compile the album first: POST /api/v1/albums/{id}/compile.");
        var manifest = JsonSerializer.Deserialize<AlbumManifestDto>(album.ManifestJson, ManifestJson)!;
        return Results.Ok(manifest with { IsStale = album.CompiledVersion != album.ContentVersion });
    }

    private static async Task<IResult> Compile(Guid albumId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var ownerId = http.User.UserId();
        // Read the version before the media: anything added meanwhile leaves the album marked stale.
        var album = await db.Albums.FirstOrDefaultAsync(a => a.Id == albumId && a.OwnerId == ownerId, ct);
        if (album is null) return Problems.AlbumNotFound();
        var media = await db.MediaFiles.AsNoTracking().Where(m => m.AlbumId == albumId && m.OwnerId == ownerId).ToListAsync(ct);

        // Only photos/videos with a date and a location are shown: the slideshow plays in date order along a route.
        var images = media.Where(m => m.Collection == MediaCollection.Images).ToList();
        var slides = images.Where(m => MediaInfoDto.MissingFor(m).Count == 0)
            .OrderBy(m => m.TakenAt)
            .ThenBy(m => m.FileName, NaturalStringComparer.Instance)
            .ThenBy(m => m.Id)
            .Select(m => MediaInfoDto.From(m, MediaEndpoints.ContentUrl(albumId, "images", m.Id)))
            .ToList();
        var music = media.Where(m => m.Collection == MediaCollection.Music)
            .OrderBy(m => m.FileName, NaturalStringComparer.Instance)
            .ThenBy(m => m.Id)
            .Select(m => MediaInfoDto.From(m, MediaEndpoints.ContentUrl(albumId, "music", m.Id)))
            .ToList();

        var now = DateTime.UtcNow;
        var manifest = new AlbumManifestDto(album.Id, album.Name, now, false, slides, music, images.Count - slides.Count);
        album.ManifestJson = JsonSerializer.Serialize(manifest, ManifestJson);
        album.CompiledAt = now;
        album.CompiledVersion = album.ContentVersion;
        await db.SaveChangesAsync(ct); // writes only the three compile columns
        return Results.Ok(manifest);
    }

    private static async Task<IResult> Delete(Guid albumId, HttpContext http, AppDbContext db, IBlobStore blobs, ILogger<AppDbContext> log, CancellationToken ct)
    {
        var ownerId = http.User.UserId();
        var deleted = 0;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.MediaFiles.Where(m => m.AlbumId == albumId && m.OwnerId == ownerId).ExecuteDeleteAsync(ct);
            deleted = await db.Albums.Where(a => a.Id == albumId && a.OwnerId == ownerId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        });
        if (deleted == 0) return Problems.AlbumNotFound();

        try { await blobs.DeletePrefixAsync(BlobNames.AlbumPrefix(ownerId, albumId), ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Album {AlbumId} deleted; some of its blobs were left behind", albumId); // unreachable, so harmless
        }
        return Results.NoContent();
    }
}
