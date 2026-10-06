using Slideshow.Api.Data;
using Slideshow.Api.Endpoints;
using Slideshow.Api.Media;
using Slideshow.Api.Tests.Infrastructure;

namespace Slideshow.Api.Tests;

public sealed class SnifferTests
{
    public static TheoryData<string, byte[], string?> Cases => new()
    {
        { "jpeg", TestMedia.Jpeg(), "image/jpeg" },
        { "png", TestMedia.Png(), "image/png" },
        { "gif", "GIF89a\x01\0\x01\0"u8.ToArray(), "image/gif" },
        { "webp", "RIFF\0\0\0\0WEBPVP8 "u8.ToArray(), "image/webp" },
        { "bmp", "BM\0\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray(), "image/bmp" },
        { "mp4", TestMedia.Mp4(), "video/mp4" },
        { "mov", [0, 0, 0, 20, .. "ftypqt  "u8.ToArray(), 0, 0, 0, 0, .. "qt  "u8.ToArray()], "video/quicktime" },
        { "old mov", [0, 0, 0, 8, .. "wide"u8.ToArray()], "video/quicktime" },
        { "webm", [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, .. "webm"u8.ToArray()], "video/webm" },
        { "ogg", "OggS\0\x02"u8.ToArray(), "video/ogg" },
        { "mp3 id3", TestMedia.Mp3(), "audio/mpeg" },
        { "mp3 raw", [0xFF, 0xFB, 0x90, 0x00], "audio/mpeg" },
        { "avif", [0, 0, 0, 24, .. "ftypavif"u8.ToArray(), 0, 0, 0, 0, .. "avifmif1"u8.ToArray()], "image/avif" },
        { "heic", TestMedia.Heic(), null },
        { "svg", TestMedia.Svg(), null },
        { "mkv", [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, .. "matroska"u8.ToArray()], null },
        { "m4a", [0, 0, 0, 20, .. "ftypM4A "u8.ToArray(), 0, 0, 0, 0, .. "M4A "u8.ToArray()], null },
        { "empty", [], null },
        { "exe", "MZ\x90\0"u8.ToArray(), null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Sniffs_by_content(string _, byte[] bytes, string? expected)
    {
        var (result, rejection) = MediaSniffer.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, MediaSniffer.HeaderLength)));
        Assert.Equal(expected, result?.ContentType);
        if (expected is null) Assert.False(string.IsNullOrEmpty(rejection));
    }
}

public sealed class NaturalOrderTests
{
    [Fact]
    public void Numbers_sort_by_value_and_case_is_ignored()
    {
        string[] names = ["IMG_10.jpg", "img_2.jpg", "IMG_1.jpg", "IMG_002b.jpg", "a.jpg", "IMG_100.jpg"];
        var sorted = names.OrderBy(n => n, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(["a.jpg", "IMG_1.jpg", "img_2.jpg", "IMG_002b.jpg", "IMG_10.jpg", "IMG_100.jpg"], sorted);
    }
}

public sealed class FileNameTests
{
    [Theory]
    [InlineData(@"C:\Users\me\Pictures\a.jpg", "a.jpg")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("we\"ird\u0001name.jpg", "weirdname.jpg")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("   ", "file")]
    public void Keeps_a_safe_base_name(string? raw, string expected) => Assert.Equal(expected, MediaEndpoints.SafeFileName(raw));

    [Fact]
    public void Long_names_keep_their_extension()
    {
        var name = MediaEndpoints.SafeFileName(new string('x', 400) + ".jpeg");
        Assert.Equal(255, name.Length);
        Assert.EndsWith(".jpeg", name);
    }
}

public sealed class MetadataTests
{
    [Fact]
    public void Corrupt_metadata_is_treated_as_none()
    {
        byte[] junk = [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, .. "Exif\0\0MM\0*\xFF\xFF\xFF\xFF"u8.ToArray()];
        var meta = MetadataReader.Read(new MemoryStream(junk), MediaKind.Image, TimeSpan.Zero);
        Assert.Null(meta.TakenAt);
        Assert.Null(meta.Latitude);
    }

    [Fact]
    public void Zero_zero_gps_is_ignored()
    {
        var meta = MetadataReader.Read(new MemoryStream(TestMedia.Jpeg(gps: (0, 0))), MediaKind.Image, TimeSpan.Zero);
        Assert.Null(meta.Latitude);
    }
}
