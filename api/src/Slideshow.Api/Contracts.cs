using System.Text.Json.Serialization;
using Slideshow.Api.Data;

namespace Slideshow.Api;

public static class Limits
{
    public const int AlbumNameLength = 200;
    public const int AlbumDescriptionLength = 2000;
    public const int FileNameLength = 255;
}

public sealed record UsageDto(int Albums, int Files, long Bytes, long QuotaBytes);
public sealed record AccountDto(string Id, string? DisplayName, string? Email, DateTime CreatedAt, UsageDto Usage);

public sealed record CreateAlbumRequest(string? Name, string? Description);

/// <param name="ExcludedCount">Photos/videos the slideshow leaves out because they have no date or no location.</param>
/// <param name="CoverId">The photo shown on the album's card: the first in slideshow order, else the first uploaded.</param>
public sealed record AlbumInfoDto(
    Guid Id, string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt,
    int ImageCount, int VideoCount, int MusicCount, int ExcludedCount, long TotalBytes, DateTime? CompiledAt, bool IsStale,
    bool IsShared,
    [property: JsonIgnore] Guid? CoverId)
{
    public string? CoverThumbnailUrl => CoverId is { } c ? $"/api/v1/albums/{Id}/images/{c}/thumbnail" : null;
}

public sealed record LocationDto(double Lat, double Lon);

/// <param name="Playable">Whether the slideshow shows it. Photos and videos need a date and a location; music always plays.</param>
/// <param name="Missing">Why it isn't playable: "date" and/or "location".</param>
/// <param name="ThumbnailUrl">A small JPEG preview (photos only; null when there is none).</param>
public sealed record MediaInfoDto(
    Guid Id, string FileName, string Kind, string ContentType, long SizeBytes, DateTime UploadedAt,
    [property: JsonConverter(typeof(LocalDateTimeConverter))] DateTime? Taken,
    string? TakenSource, LocationDto? Location, bool Playable, IReadOnlyList<string> Missing,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    string? ThumbnailUrl = null)
{
    public static MediaInfoDto From(MediaFile m, string? url = null)
    {
        var missing = MissingFor(m);
        var hasThumbnail = url is not null && m.Kind == MediaKind.Image && m.ThumbnailState != ThumbnailState.Unavailable;
        return new(
            m.Id, m.FileName, m.Kind.ToString().ToLowerInvariant(), m.ContentType, m.SizeBytes, m.UploadedAt,
            m.TakenAt, m.TakenSource?.ToString() switch { null => null, var s => char.ToLowerInvariant(s[0]) + s[1..] },
            m.Latitude is { } lat && m.Longitude is { } lon ? new LocationDto(lat, lon) : null,
            missing.Count == 0, missing, url, hasThumbnail ? url + "/thumbnail" : null);
    }

    /// <summary>What a slide lacks to be shown: the slideshow plays in date order along a route, so it needs both.</summary>
    public static List<string> MissingFor(MediaFile m)
    {
        var missing = new List<string>(2);
        if (m.Collection != MediaCollection.Images) return missing;
        if (m.TakenAt is null) missing.Add("date");
        if (m.Latitude is null || m.Longitude is null) missing.Add("location");
        return missing;
    }
}

/// <param name="Slides">Playable photos and videos in play order.</param>
/// <param name="ExcludedCount">Photos/videos left out (no date or no location).</param>
public sealed record AlbumManifestDto(
    Guid AlbumId, string Name, DateTime CompiledAt, bool IsStale,
    IReadOnlyList<MediaInfoDto> Slides, IReadOnlyList<MediaInfoDto> Music, int ExcludedCount);

/// <param name="Token">The secret code for the share link (the website builds the link); null when not shared.</param>
public sealed record ShareDto(bool Enabled, string? Token, bool ShowMap);

/// <summary>Turn link sharing on/off and choose whether viewers see the map. Omitted fields stay as they are.</summary>
public sealed record UpdateShareRequest(bool? Enabled, bool? ShowMap);

/// <summary>What anonymous viewers of a share link get: the slideshow only, with file URLs under /api/v1/shared/{token}.</summary>
public sealed record SharedAlbumDto(string Name, bool ShowMap, IReadOnlyList<MediaInfoDto> Slides, IReadOnlyList<MediaInfoDto> Music);

/// <summary>Writes "taken" as local wall-clock time with no zone ("2026-07-04T09:31:05"), as it was recorded.</summary>
public sealed class LocalDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType == System.Text.Json.JsonTokenType.Null ? null : DateTime.SpecifyKind(reader.GetDateTime(), DateTimeKind.Unspecified);

    public override void Write(System.Text.Json.Utf8JsonWriter writer, DateTime? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value is { } v) writer.WriteStringValue(v.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}

/// <summary>RFC 7807 errors with a stable machine-readable "code".</summary>
public static class Problems
{
    public static IResult Of(int status, string code, string detail) =>
        Results.Problem(statusCode: status, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult NotRegistered() => Of(403, "account_not_registered", "Register with POST /api/v1/account first.");
    public static IResult AlbumNotFound() => Of(404, "album_not_found", "Album not found.");
    public static IResult MediaNotFound() => Of(404, "media_not_found", "File not found.");
    public static IResult Validation(string detail) => Of(400, "validation_failed", detail);
}
