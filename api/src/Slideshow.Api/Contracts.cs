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

public sealed record AlbumInfoDto(
    Guid Id, string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt,
    int ImageCount, int VideoCount, int MusicCount, long TotalBytes, DateTime? CompiledAt, bool IsStale);

public sealed record LocationDto(double Lat, double Lon);

public sealed record MediaInfoDto(
    Guid Id, string FileName, string Kind, string ContentType, long SizeBytes, DateTime UploadedAt,
    [property: JsonConverter(typeof(LocalDateTimeConverter))] DateTime? Taken,
    string? TakenSource, LocationDto? Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null)
{
    public static MediaInfoDto From(MediaFile m, string? url = null) => new(
        m.Id, m.FileName, m.Kind.ToString().ToLowerInvariant(), m.ContentType, m.SizeBytes, m.UploadedAt,
        m.TakenAt, m.TakenSource?.ToString() switch { null => null, var s => char.ToLowerInvariant(s[0]) + s[1..] },
        m.Latitude is { } lat && m.Longitude is { } lon ? new LocationDto(lat, lon) : null, url);
}

public sealed record AlbumManifestDto(
    Guid AlbumId, string Name, DateTime CompiledAt, bool IsStale,
    IReadOnlyList<MediaInfoDto> Slides, IReadOnlyList<MediaInfoDto> Music);

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
