using System.Text;
using Slideshow.Api.Data;

namespace Slideshow.Api.Media;

public sealed record SniffResult(MediaKind Kind, string ContentType);

/// <summary>Decides a file's type from its first bytes. The client's file name and Content-Type are not trusted.</summary>
public static class MediaSniffer
{
    public const int HeaderLength = 64;

    private static readonly HashSet<string> Mp4Brands =
        ["isom", "iso2", "iso3", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1", "dash", "MSNV", "M4V ", "M4VH", "M4VP", "3gp4", "3gp5", "3gp6", "3g2a"];
    private static readonly HashSet<string> HeifBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1"];
    private static readonly HashSet<string> QuickTimeAtoms = ["moov", "mdat", "wide", "free", "skip", "pnot"];

    /// <summary>Returns the detected type, or null with a reason when the content isn't an accepted format.</summary>
    public static (SniffResult? Result, string? Rejection) Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return Ok(MediaKind.Image, "image/jpeg");
        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return Ok(MediaKind.Image, "image/png");
        if (Ascii(h, 0, 6) is "GIF87a" or "GIF89a") return Ok(MediaKind.Image, "image/gif");
        if (Ascii(h, 0, 4) == "RIFF" && Ascii(h, 8, 4) == "WEBP") return Ok(MediaKind.Image, "image/webp");
        if (Ascii(h, 0, 2) == "BM" && h.Length >= 14) return Ok(MediaKind.Image, "image/bmp");
        if (h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3)
            return Contains(h, "webm") ? Ok(MediaKind.Video, "video/webm") : (null, "Matroska (.mkv) video is not supported; use MP4 or WebM.");
        if (Ascii(h, 0, 4) == "OggS") return Ok(MediaKind.Video, "video/ogg");
        if (Ascii(h, 0, 3) == "ID3" || (h.Length >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)) return Ok(MediaKind.Audio, "audio/mpeg");

        var box = Ascii(h, 4, 4);
        if (box == "ftyp")
        {
            var major = Ascii(h, 8, 4);
            var brands = CompatibleBrands(h);
            if (major is "avif" or "avis" || (major == "mif1" && brands.Contains("avif"))) return Ok(MediaKind.Image, "image/avif");
            if (HeifBrands.Contains(major)) return (null, "HEIC/HEIF images can't be shown by most browsers. Convert them to JPEG first.");
            if (major == "qt  ") return Ok(MediaKind.Video, "video/quicktime");
            if (major is "M4A " or "M4B ") return (null, "AAC/M4A audio is not supported; use MP3.");
            if (Mp4Brands.Contains(major) || brands.Overlaps(Mp4Brands)) return Ok(MediaKind.Video, "video/mp4");
            return (null, $"Unsupported video format ('{major.Trim()}').");
        }
        if (QuickTimeAtoms.Contains(box)) return Ok(MediaKind.Video, "video/quicktime");

        return (null, "Unsupported file type. Accepted: JPEG, PNG, GIF, WebP, AVIF, BMP images; MP4, MOV, WebM, Ogg videos; MP3 music.");
    }

    private static (SniffResult?, string?) Ok(MediaKind kind, string contentType) => (new SniffResult(kind, contentType), null);

    private static string Ascii(ReadOnlySpan<byte> h, int offset, int length) =>
        h.Length >= offset + length ? Encoding.Latin1.GetString(h.Slice(offset, length)) : "";

    private static bool Contains(ReadOnlySpan<byte> h, string text) => Encoding.Latin1.GetString(h).Contains(text, StringComparison.Ordinal);

    /// <summary>ftyp box: size(4) "ftyp" major(4) minor(4) then 4-byte compatible brands up to the box size.</summary>
    private static HashSet<string> CompatibleBrands(ReadOnlySpan<byte> h)
    {
        var size = h.Length >= 4 ? (int)Math.Min((uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]), (uint)h.Length) : 0;
        var set = new HashSet<string>();
        for (var i = 16; i + 4 <= size; i += 4) set.Add(Ascii(h, i, 4));
        return set;
    }
}
