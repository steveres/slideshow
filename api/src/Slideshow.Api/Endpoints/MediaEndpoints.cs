using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Slideshow.Api.Data;
using Slideshow.Api.Identity;
using Slideshow.Api.Media;
using Slideshow.Api.Storage;

namespace Slideshow.Api.Endpoints;

/// <summary>/albums/{albumId}/images (photos and videos) and /albums/{albumId}/music share these handlers.</summary>
public static class MediaEndpoints
{
    private const long MultipartOverhead = 64 * 1024; // boundaries, part headers and the small form fields

    public static string ContentUrl(Guid albumId, string segment, Guid id) => $"/api/v1/albums/{albumId}/{segment}/{id}";

    public static void MapCollection(RouteGroupBuilder albums, string segment, MediaCollection collection, long maxUploadBytes)
    {
        var g = albums.MapGroup($"/{{albumId:guid}}/{segment}").WithTags(collection == MediaCollection.Images ? "Images" : "Music");
        var what = collection == MediaCollection.Images ? "image or video" : "music track";

        g.MapGet("/", (Guid albumId, HttpContext http, AppDbContext db, CancellationToken ct) => List(albumId, segment, collection, http, db, ct))
            .WithSummary($"Every {what} in the album, in upload order.");

        g.MapPost("/", (Guid albumId, IFormFile? file, [FromForm] int? utcOffsetMinutes,
                HttpContext http, AppDbContext db, IBlobStore blobs, IOptions<LimitsOptions> limits, ILogger<AppDbContext> log, CancellationToken ct) =>
                Add(albumId, segment, collection, file, utcOffsetMinutes, http, db, blobs, limits.Value, log, ct))
            .WithSummary($"Upload an {what} (multipart/form-data, field \"file\").")
            .DisableAntiforgery() // bearer tokens only, no cookies: nothing for CSRF to ride on
            .Accepts<IFormFile>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(maxUploadBytes + MultipartOverhead))
            .WithFormOptions(multipartBodyLengthLimit: maxUploadBytes + MultipartOverhead);

        g.MapGet("/{id:guid}", (Guid albumId, Guid id, HttpContext http, AppDbContext db, IBlobStore blobs, CancellationToken ct) =>
                GetContent(albumId, id, collection, http, db, blobs, ct))
            .WithSummary($"The {what}'s bytes (supports Range).");

        g.MapGet("/{id:guid}/info", async (Guid albumId, Guid id, HttpContext http, AppDbContext db, CancellationToken ct) =>
                await Find(db, http.User.UserId(), albumId, collection, id, ct) is { } m
                    ? Results.Ok(MediaInfoDto.From(m, ContentUrl(albumId, segment, m.Id)))
                    : Problems.MediaNotFound())
            .WithSummary($"The {what}'s details.");

        g.MapDelete("/{id:guid}", (Guid albumId, Guid id, HttpContext http, AppDbContext db, IBlobStore blobs, ILogger<AppDbContext> log, CancellationToken ct) =>
                Delete(albumId, id, collection, http, db, blobs, log, ct))
            .WithSummary($"Delete the {what}.");

        if (collection == MediaCollection.Images)
            g.MapGet("/{id:guid}/thumbnail", (Guid albumId, Guid id, HttpContext http, AppDbContext db, IBlobStore blobs, CancellationToken ct) =>
                    GetThumbnail(albumId, id, http, db, blobs, ct))
                .WithSummary("A small JPEG preview of a photo (videos have none yet).");
    }

    private static Task<MediaFile?> Find(AppDbContext db, string ownerId, Guid albumId, MediaCollection collection, Guid id, CancellationToken ct) =>
        db.MediaFiles.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.AlbumId == albumId && m.OwnerId == ownerId && m.Collection == collection, ct);

    private static Task<bool> AlbumExists(AppDbContext db, string ownerId, Guid albumId, CancellationToken ct) =>
        db.Albums.AnyAsync(a => a.Id == albumId && a.OwnerId == ownerId, ct);

    private static async Task<IResult> List(Guid albumId, string segment, MediaCollection collection, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var ownerId = http.User.UserId();
        if (!await AlbumExists(db, ownerId, albumId, ct)) return Problems.AlbumNotFound();
        var media = await db.MediaFiles.AsNoTracking()
            .Where(m => m.AlbumId == albumId && m.OwnerId == ownerId && m.Collection == collection)
            .OrderBy(m => m.UploadedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);
        return Results.Ok(media.Select(m => MediaInfoDto.From(m, ContentUrl(albumId, segment, m.Id))));
    }

    private static async Task<IResult> Add(Guid albumId, string segment, MediaCollection collection, IFormFile? file, int? utcOffsetMinutes,
        HttpContext http, AppDbContext db, IBlobStore blobs, LimitsOptions limits, ILogger log, CancellationToken ct)
    {
        var ownerId = http.User.UserId();
        if (!await AlbumExists(db, ownerId, albumId, ct)) return Problems.AlbumNotFound();
        if (file is null || file.Length == 0) return Problems.Validation("Send the file as multipart/form-data in a field named \"file\".");
        if (utcOffsetMinutes is < -14 * 60 or > 14 * 60) return Problems.Validation("utcOffsetMinutes must be between -840 and 840.");

        await using var content = file.OpenReadStream(); // buffered by ASP.NET Core (to disk when large), so seekable
        var header = new byte[MediaSniffer.HeaderLength];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var (sniffed, rejection) = MediaSniffer.Sniff(header.AsSpan(0, headerLength));
        if (sniffed is null) return Problems.Of(415, "unsupported_media_type", rejection!);

        var fits = collection == MediaCollection.Music ? sniffed.Kind == MediaKind.Audio : sniffed.Kind != MediaKind.Audio;
        if (!fits) return Problems.Of(415, "unsupported_media_type",
            collection == MediaCollection.Music ? "Music must be MP3." : "Upload music to the album's music collection.");

        var max = sniffed.Kind switch { MediaKind.Image => limits.MaxImageBytes, MediaKind.Video => limits.MaxVideoBytes, _ => limits.MaxMusicBytes };
        if (file.Length > max) return Problems.Of(413, "file_too_large", $"This kind of file can be at most {max / (1024 * 1024)} MB.");

        var used = await db.MediaFiles.Where(m => m.OwnerId == ownerId).SumAsync(m => (long?)m.SizeBytes, ct) ?? 0;
        if (used + file.Length > limits.QuotaBytesPerUser) return Problems.Of(413, "quota_exceeded", "Your storage is full. Delete some files and try again.");
        if (await db.MediaFiles.CountAsync(m => m.AlbumId == albumId && m.OwnerId == ownerId, ct) >= limits.MaxFilesPerAlbum)
            return Problems.Of(409, "album_full", $"An album can hold at most {limits.MaxFilesPerAlbum} files.");

        var offset = TimeSpan.FromMinutes(utcOffsetMinutes ?? 0);
        var meta = collection == MediaCollection.Images
            ? MetadataReader.Read(content, sniffed.Kind, offset)
            : new MediaMetadata(null, null, null, null);

        byte[]? thumbnail = null;
        if (sniffed.Kind == MediaKind.Image)
        {
            content.Position = 0;
            thumbnail = Thumbnails.TryCreate(content);
        }

        var now = DateTime.UtcNow;
        var media = new MediaFile
        {
            Id = Guid.CreateVersion7(),
            AlbumId = albumId,
            OwnerId = ownerId,
            Collection = collection,
            Kind = sniffed.Kind,
            FileName = SafeFileName(file.FileName),
            ContentType = sniffed.ContentType,
            SizeBytes = file.Length,
            UploadedAt = now,
            TakenAt = meta.TakenAt,
            TakenSource = meta.TakenSource,
            Latitude = meta.Latitude,
            Longitude = meta.Longitude,
            ThumbnailState = sniffed.Kind != MediaKind.Image ? ThumbnailState.None
                : thumbnail is null ? ThumbnailState.Unavailable : ThumbnailState.Ready,
            BlobName = "",
        };
        media.BlobName = BlobNames.For(ownerId, albumId, media.Id);

        content.Position = 0;
        await blobs.UploadAsync(media.BlobName, content, media.ContentType, ct);
        try
        {
            if (thumbnail is not null)
                await blobs.UploadAsync(BlobNames.Thumbnail(media.BlobName), new MemoryStream(thumbnail), Thumbnails.ContentType, ct);
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                db.MediaFiles.Add(media);
                await db.SaveChangesAsync(ct);
                // A missing album here means it was deleted mid-upload; the FK insert above would have failed.
                await BumpVersion(db, albumId, now, ct);
                await tx.CommitAsync(ct);
            });
        }
        catch
        {
            await blobs.DeleteAsync(media.BlobName, CancellationToken.None);
            if (thumbnail is not null) await blobs.DeleteAsync(BlobNames.Thumbnail(media.BlobName), CancellationToken.None);
            throw;
        }
        log.LogInformation("Stored {Kind} {MediaId} ({Bytes} bytes) in album {AlbumId}", media.Kind, media.Id, media.SizeBytes, albumId);
        var url = ContentUrl(albumId, segment, media.Id);
        return Results.Created(url + "/info", MediaInfoDto.From(media, url));
    }

    private static async Task<IResult> GetContent(Guid albumId, Guid id, MediaCollection collection, HttpContext http, AppDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var media = await Find(db, http.User.UserId(), albumId, collection, id, ct);
        if (media is null) return Problems.MediaNotFound();
        var stream = await blobs.OpenReadAsync(media.BlobName, ct);
        if (stream is null) return Problems.MediaNotFound();

        var headers = http.Response.Headers;
        headers.CacheControl = "private, max-age=31536000, immutable"; // a media id's bytes never change
        headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = media.FileName }.ToString();
        return Results.Stream(stream, media.ContentType,
            lastModified: DateTime.SpecifyKind(media.UploadedAt, DateTimeKind.Utc),
            entityTag: new EntityTagHeaderValue($"\"{media.Id:N}\""),
            enableRangeProcessing: true);
    }

    /// <summary>The photo's preview; made now (and kept) if it was uploaded before previews existed.</summary>
    private static async Task<IResult> GetThumbnail(Guid albumId, Guid id, HttpContext http, AppDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var media = await Find(db, http.User.UserId(), albumId, MediaCollection.Images, id, ct);
        if (media is null) return Problems.MediaNotFound();
        var noThumbnail = Problems.Of(404, "no_thumbnail", "This file has no preview.");
        if (media.Kind != MediaKind.Image || media.ThumbnailState == ThumbnailState.Unavailable) return noThumbnail;

        var name = BlobNames.Thumbnail(media.BlobName);
        var stream = media.ThumbnailState == ThumbnailState.Ready ? await blobs.OpenReadAsync(name, ct) : null;
        if (stream is null)
        {
            byte[]? bytes;
            await using (var original = await blobs.OpenReadAsync(media.BlobName, ct))
            {
                if (original is null) return Problems.MediaNotFound();
                bytes = Thumbnails.TryCreate(original);
            }
            if (bytes is not null) await blobs.UploadAsync(name, new MemoryStream(bytes), Thumbnails.ContentType, ct);
            var state = bytes is null ? ThumbnailState.Unavailable : ThumbnailState.Ready;
            await db.MediaFiles.Where(m => m.Id == media.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.ThumbnailState, state), ct);
            if (bytes is null) return noThumbnail;
            stream = new MemoryStream(bytes);
        }

        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable"; // a photo's preview never changes
        return Results.Stream(stream, Thumbnails.ContentType, entityTag: new EntityTagHeaderValue($"\"{media.Id:N}-thumb\""));
    }

    private static async Task<IResult> Delete(Guid albumId, Guid id, MediaCollection collection, HttpContext http, AppDbContext db, IBlobStore blobs, ILogger log, CancellationToken ct)
    {
        var media = await Find(db, http.User.UserId(), albumId, collection, id, ct);
        if (media is null) return Problems.MediaNotFound();

        var strategy = db.Database.CreateExecutionStrategy();
        var deleted = 0;
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            deleted = await db.MediaFiles.Where(m => m.Id == media.Id).ExecuteDeleteAsync(ct);
            if (deleted > 0) await BumpVersion(db, albumId, DateTime.UtcNow, ct);
            await tx.CommitAsync(ct);
        });
        if (deleted == 0) return Problems.MediaNotFound(); // deleted concurrently

        try
        {
            await blobs.DeleteAsync(media.BlobName, ct);
            if (media.Kind == MediaKind.Image) await blobs.DeleteAsync(BlobNames.Thumbnail(media.BlobName), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Media {MediaId} deleted; its blob was left behind", media.Id);
        }
        return Results.NoContent();
    }

    private static Task<int> BumpVersion(AppDbContext db, Guid albumId, DateTime now, CancellationToken ct) =>
        db.Albums.Where(a => a.Id == albumId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.ContentVersion, a => a.ContentVersion + 1)
            .SetProperty(a => a.UpdatedAt, now), ct);

    /// <summary>Keeps only the base name, without control or path characters, at most 255 chars.</summary>
    public static string SafeFileName(string? raw)
    {
        var name = Path.GetFileName((raw ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('/' or '\\' or '"')).ToArray()).Trim();
        if (name.Length > Limits.FileNameLength)
        {
            var ext = Path.GetExtension(name);
            name = name[..(Limits.FileNameLength - ext.Length)] + ext;
        }
        return name.Length == 0 ? "file" : name;
    }

}
