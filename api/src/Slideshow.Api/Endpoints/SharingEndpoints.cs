using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slideshow.Api.Data;
using Slideshow.Api.Identity;
using Slideshow.Api.Storage;

namespace Slideshow.Api.Endpoints;

/// <summary>
/// Link sharing: the owner turns on "anyone with the link can watch" for an album, which gives it a
/// secret, unguessable code (128 random bits). Anyone with the code can play the album's slideshow
/// without signing in — and nothing else: no file list, no other albums, no changes. Turning sharing
/// off (or getting a new link) makes old links stop working at once.
/// </summary>
public static partial class SharingEndpoints
{
    // ───────── Owner: /api/v1/albums/{albumId}/share ─────────

    public static void MapOwner(RouteGroupBuilder albums)
    {
        var g = albums.MapGroup("/{albumId:guid}/share").WithTags("Sharing");
        g.MapGet("/", Get).WithSummary("Whether the album is shared by link, and its share code.");
        g.MapPut("/", Update).WithSummary("Turn link sharing on/off; choose whether viewers see the map.");
        g.MapPost("/reset", Reset).WithSummary("Replace the share link: the old one stops working.");
    }

    private static ShareDto ToDto(Album a) => new(a.ShareToken is not null, a.ShareToken, !a.ShareHideMap);

    private static Task<Album?> OwnedAlbum(AppDbContext db, HttpContext http, Guid albumId, CancellationToken ct) =>
        db.Albums.FirstOrDefaultAsync(a => a.Id == albumId && a.OwnerId == http.User.UserId(), ct);

    /// <summary>128 random bits, URL-safe (22 characters).</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<IResult> Get(Guid albumId, HttpContext http, AppDbContext db, CancellationToken ct) =>
        await OwnedAlbum(db, http, albumId, ct) is { } album ? Results.Ok(ToDto(album)) : Problems.AlbumNotFound();

    private static async Task<IResult> Update(Guid albumId, UpdateShareRequest body, HttpContext http, AppDbContext db, ILogger<AppDbContext> log, CancellationToken ct)
    {
        var album = await OwnedAlbum(db, http, albumId, ct);
        if (album is null) return Problems.AlbumNotFound();
        if (body.ShowMap is { } showMap) album.ShareHideMap = !showMap;
        if (body.Enabled == true && album.ShareToken is null) album.ShareToken = NewToken();  // turning on again gives a new link
        if (body.Enabled == false) album.ShareToken = null;                                    // old links stop working
        await db.SaveChangesAsync(ct);
        log.LogInformation("Album {AlbumId} sharing: {Shared}", albumId, album.ShareToken is not null);
        return Results.Ok(ToDto(album));
    }

    private static async Task<IResult> Reset(Guid albumId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var album = await OwnedAlbum(db, http, albumId, ct);
        if (album is null) return Problems.AlbumNotFound();
        if (album.ShareToken is null) return Problems.Of(409, "album_not_shared", "Turn on link sharing first.");
        album.ShareToken = NewToken();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(album));
    }

    // ───────── Anyone with the link: /api/v1/shared/{token} ─────────

    public static void MapShared(this RouteGroupBuilder shared)
    {
        shared.WithTags("Shared");
        shared.MapGet("/{token}", GetShared).WithSummary("The shared album's slideshow (no sign-in).");
        shared.MapGet("/{token}/images/{id:guid}", (string token, Guid id, HttpContext http, AppDbContext db, IBlobStore blobs, CancellationToken ct) =>
            GetSharedFile(token, id, MediaCollection.Images, http, db, blobs, ct)).WithSummary("A photo or video of a shared album.");
        shared.MapGet("/{token}/music/{id:guid}", (string token, Guid id, HttpContext http, AppDbContext db, IBlobStore blobs, CancellationToken ct) =>
            GetSharedFile(token, id, MediaCollection.Music, http, db, blobs, ct)).WithSummary("A music track of a shared album.");
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{22}$")]
    private static partial Regex TokenFormat();

    /// <summary>A wrong, revoked or malformed code all look the same: not found.</summary>
    private static IResult NotShared() => Problems.Of(404, "share_not_found", "This slideshow isn't shared, or the link is wrong.");

    private static Task<Album?> SharedAlbum(AppDbContext db, string token, CancellationToken ct) =>
        TokenFormat().IsMatch(token) ? db.Albums.FirstOrDefaultAsync(a => a.ShareToken == token, ct) : Task.FromResult<Album?>(null);

    private static async Task<IResult> GetShared(string token, AppDbContext db, CancellationToken ct)
    {
        var album = await SharedAlbum(db, token, ct);
        if (album is null) return NotShared();

        // Viewers always get the current slideshow: compile if the owner changed the album since.
        var manifest = album.ManifestJson is null || album.CompiledVersion != album.ContentVersion
            ? await AlbumEndpoints.CompileAsync(db, album, ct)
            : JsonSerializer.Deserialize<AlbumManifestDto>(album.ManifestJson, AlbumEndpoints.ManifestJson)!;

        var showMap = !album.ShareHideMap;
        MediaInfoDto Shared(MediaInfoDto m, string segment) => m with
        {
            Url = $"/api/v1/shared/{token}/{segment}/{m.Id}",
            ThumbnailUrl = null,
            Location = showMap ? m.Location : null, // hidden map: no locations either
        };
        return Results.Ok(new SharedAlbumDto(album.Name, showMap,
            manifest.Slides.Select(m => Shared(m, "images")).ToList(),
            manifest.Music.Select(m => Shared(m, "music")).ToList()));
    }

    private static async Task<IResult> GetSharedFile(string token, Guid id, MediaCollection collection, HttpContext http, AppDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var album = await SharedAlbum(db, token, ct);
        if (album is null) return NotShared();
        var media = await db.MediaFiles.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id && m.AlbumId == album.Id && m.Collection == collection, ct);
        // Only what the slideshow shows: photos/videos left out of it (no date or location) stay private.
        if (media is null || MediaInfoDto.MissingFor(media).Count > 0) return Problems.MediaNotFound();
        return await MediaEndpoints.StreamAsync(http, blobs, media, "private, max-age=3600", ct);
    }
}
