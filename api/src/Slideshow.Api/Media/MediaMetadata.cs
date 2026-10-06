using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Slideshow.Api.Data;

namespace Slideshow.Api.Media;

public sealed record MediaMetadata(DateTime? TakenAt, TakenSource? TakenSource, double? Latitude, double? Longitude);

/// <summary>Reads date taken and GPS position — the server-side equivalent of prepare.ps1 and the SPA's GPS readers.</summary>
public static partial class MetadataReader
{
    private const int VideoScanBytes = 1 << 20; // the head and tail of a video hold its metadata

    /// <param name="utcOffset">The uploader's UTC offset, used to turn UTC timestamps (MP4 headers) into local time like EXIF.</param>
    /// <remarks>Only the capture date recorded in the file counts; file modified times are deliberately not used.</remarks>
    public static MediaMetadata Read(Stream file, MediaKind kind, TimeSpan utcOffset)
    {
        DateTime? taken = null;
        TakenSource? source = null;
        (double Lat, double Lon)? gps = null;
        try
        {
            if (kind == MediaKind.Image)
            {
                file.Position = 0;
                var dirs = ImageMetadataReader.ReadMetadata(file);
                taken = ExifDate(dirs);
                if (taken is not null) source = TakenSource.Exif;
                gps = ExifGps(dirs);
            }
            else if (kind == MediaKind.Video)
            {
                var (head, tail) = ReadHeadAndTail(file);
                taken = AppleCreationDate(head) ?? AppleCreationDate(tail);
                taken ??= MovieHeaderUtc(head) is { } utc1 ? utc1 + utcOffset : MovieHeaderUtc(tail) is { } utc2 ? utc2 + utcOffset : null;
                if (taken is not null) source = TakenSource.Video;
                gps = Iso6709(head) ?? Iso6709(tail);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            // Malformed metadata: treat as "none", like the SPA does.
        }

        if (taken is { } t) taken = DateTime.SpecifyKind(new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Unspecified);
        return new MediaMetadata(taken, source, gps?.Lat, gps?.Lon);
    }

    private static DateTime? ExifDate(IReadOnlyList<MetadataExtractor.Directory> dirs)
    {
        foreach (var d in dirs.OfType<ExifSubIfdDirectory>())
            if (d.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt) && dt.Year > 1) return dt;
        foreach (var d in dirs.OfType<ExifIfd0Directory>())
            if (d.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt) && dt.Year > 1) return dt;
        return null;
    }

    private static (double, double)? ExifGps(IReadOnlyList<MetadataExtractor.Directory> dirs)
    {
        foreach (var d in dirs.OfType<GpsDirectory>())
        {
            if (d.TryGetGeoLocation(out var loc) && Valid(loc.Latitude, loc.Longitude))
                return (loc.Latitude, loc.Longitude);
        }
        return null;
    }

    private static bool Valid(double lat, double lon) =>
        !(lat == 0 && lon == 0) && double.IsFinite(lat) && double.IsFinite(lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;

    private static (byte[] Head, byte[] Tail) ReadHeadAndTail(Stream file)
    {
        file.Position = 0;
        var head = ReadUpTo(file, VideoScanBytes);
        byte[] tail = [];
        if (file.Length > VideoScanBytes)
        {
            file.Position = Math.Max(VideoScanBytes, file.Length - VideoScanBytes);
            tail = ReadUpTo(file, VideoScanBytes);
        }
        return (head, tail);
    }

    private static byte[] ReadUpTo(Stream s, int count)
    {
        var buf = new byte[count];
        var n = 0;
        int read;
        while (n < count && (read = s.Read(buf, n, count - n)) > 0) n += read;
        return n == count ? buf : buf[..n];
    }

    /// <summary>Apple's "com.apple.quicktime.creationdate", e.g. "2026-07-04T09:31:05-0700": keep the local wall time.</summary>
    private static DateTime? AppleCreationDate(byte[] data)
    {
        var m = AppleDate().Match(Encoding.Latin1.GetString(data));
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    /// <summary>The "mvhd" box's creation time (seconds since 1904, UTC), as prepare.ps1 reads it.</summary>
    private static DateTime? MovieHeaderUtc(byte[] data)
    {
        var i = data.AsSpan().IndexOf("mvhd"u8);
        if (i < 0 || i + 20 > data.Length) return null;
        var p = i + 8; // skip "mvhd" + version/flags
        ulong secs = data[i + 4] == 1
            ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p, 8))
            : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
        if (secs == 0 || secs > 200UL * 365 * 86400) return null;
        return new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs);
    }

    /// <summary>ISO 6709 location string, e.g. "+37.7749-122.4194/" (as the SPA's videoGps reads it).</summary>
    private static (double, double)? Iso6709(byte[] data)
    {
        var m = Iso6709Regex().Match(Encoding.Latin1.GetString(data));
        if (!m.Success) return null;
        var lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var lon = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return Valid(lat, lon) ? (lat, lon) : null;
    }

    [GeneratedRegex(@"([+-]\d{2}\.\d{2,})([+-]\d{3}\.\d{2,})")]
    private static partial Regex Iso6709Regex();

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})[+-]\d{2}:?\d{2}")]
    private static partial Regex AppleDate();
}

/// <summary>Filename order that puts "IMG_2" before "IMG_10" (like the SPA's localeCompare with numeric: true).</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return x is null ? (y is null ? 0 : -1) : 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length - b.Length;
                var c = a.SequenceCompareTo(b);
                if (c != 0) return c;
            }
            else
            {
                var c = string.Compare(x, i, y, j, 1, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i) - (y.Length - j) is var rest && rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
