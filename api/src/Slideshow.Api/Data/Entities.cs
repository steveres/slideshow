namespace Slideshow.Api.Data;

public sealed class User
{
    /// <summary>The Entra object id ("oid" claim).</summary>
    public required string Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Tokens issued before this (UTC) are rejected: set by logout.</summary>
    public DateTime? TokensValidAfter { get; set; }
}

public sealed class Album
{
    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Bumped on every add/delete of media; compared with CompiledVersion to detect staleness.</summary>
    public int ContentVersion { get; set; }
    public int? CompiledVersion { get; set; }
    public DateTime? CompiledAt { get; set; }
    public string? ManifestJson { get; set; }

    public List<MediaFile> Media { get; set; } = [];
}

/// <summary>Which album collection a file belongs to (the URL segment).</summary>
public enum MediaCollection { Images = 0, Music = 1 }

public enum MediaKind { Image = 0, Video = 1, Audio = 2 }

/// <summary>Where the capture date came from. Only real capture dates are used, never file times.</summary>
public enum TakenSource { Exif = 0, Video = 1 }

public sealed class MediaFile
{
    public Guid Id { get; set; }
    public Guid AlbumId { get; set; }
    public required string OwnerId { get; set; }
    public MediaCollection Collection { get; set; }
    public MediaKind Kind { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string BlobName { get; set; }
    public DateTime UploadedAt { get; set; }
    /// <summary>Local wall-clock time the photo/video was taken (no time zone, like EXIF).</summary>
    public DateTime? TakenAt { get; set; }
    public TakenSource? TakenSource { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}
