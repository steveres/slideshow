using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;

namespace Slideshow.Api.Tests.Infrastructure;

/// <summary>Builds small but real-format media files: a JPEG with an EXIF date and GPS, an MP4 with a creation time and location, etc.</summary>
public static class TestMedia
{
    public static byte[] Jpeg(DateTime? taken = null, (double Lat, double Lon)? gps = null, int padding = 256)
    {
        var tiff = Tiff(taken, gps);
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var payloadLength = 2 + 6 + tiff.Length;
        app1.Add((byte)(payloadLength >> 8));
        app1.Add((byte)payloadLength);
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(tiff);

        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange(app1);
        bytes.AddRange(Enumerable.Repeat((byte)0, padding)); // stand-in for image data
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    /// <summary>Big-endian TIFF: IFD0 → Exif IFD (DateTimeOriginal) and GPS IFD.</summary>
    private static byte[] Tiff(DateTime? taken, (double Lat, double Lon)? gps)
    {
        var w = new TiffWriter();
        var ifd0 = new List<(ushort Tag, ushort Type, uint Count, Func<uint> Value)>();
        var exifEntries = new List<(ushort, ushort, uint, Func<uint>)>();
        var gpsEntries = new List<(ushort, ushort, uint, Func<uint>)>();

        if (taken is { } t)
        {
            var text = Encoding.ASCII.GetBytes(t.ToString("yyyy:MM:dd HH:mm:ss") + "\0");
            exifEntries.Add((0x9003, 2, (uint)text.Length, () => w.Data(text)));
        }
        if (gps is { } g)
        {
            gpsEntries.Add((1, 2, 2, () => Inline(g.Lat < 0 ? 'S' : 'N')));
            gpsEntries.Add((2, 5, 3, () => w.Data(Dms(Math.Abs(g.Lat)))));
            gpsEntries.Add((3, 2, 2, () => Inline(g.Lon < 0 ? 'W' : 'E')));
            gpsEntries.Add((4, 5, 3, () => w.Data(Dms(Math.Abs(g.Lon)))));
        }

        uint exifOffset = 0, gpsOffset = 0;
        if (exifEntries.Count > 0) ifd0.Add((0x8769, 4, 1, () => exifOffset));
        if (gpsEntries.Count > 0) ifd0.Add((0x8825, 4, 1, () => gpsOffset));

        // Layout: header, IFD0, Exif IFD, GPS IFD, then the data area.
        uint IfdSize(int n) => (uint)(2 + n * 12 + 4);
        var ifd0Offset = 8u;
        exifOffset = ifd0Offset + IfdSize(ifd0.Count);
        gpsOffset = exifOffset + (exifEntries.Count > 0 ? IfdSize(exifEntries.Count) : 0);
        w.DataStart = gpsOffset + (gpsEntries.Count > 0 ? IfdSize(gpsEntries.Count) : 0);

        var head = new List<byte> { (byte)'M', (byte)'M', 0, 42 };
        head.AddRange(U32(ifd0Offset));
        head.AddRange(Ifd(ifd0));
        if (exifEntries.Count > 0) head.AddRange(Ifd(exifEntries));
        if (gpsEntries.Count > 0) head.AddRange(Ifd(gpsEntries));
        head.AddRange(w.Bytes);
        return [.. head];
    }

    private static uint Inline(char c) => (uint)c << 24; // ASCII "N\0" stored left-justified in the value field

    private static byte[] Dms(double deg)
    {
        var d = (uint)deg;
        var mFull = (deg - d) * 60;
        var m = (uint)mFull;
        var s = (uint)Math.Round((mFull - m) * 60 * 10000);
        return [.. U32(d), .. U32(1), .. U32(m), .. U32(1), .. U32(s), .. U32(10000)];
    }

    private static byte[] Ifd(List<(ushort Tag, ushort Type, uint Count, Func<uint> Value)> entries)
    {
        var b = new List<byte>();
        b.AddRange(U16((ushort)entries.Count));
        foreach (var (tag, type, count, value) in entries)
        {
            b.AddRange(U16(tag));
            b.AddRange(U16(type));
            b.AddRange(U32(count));
            b.AddRange(U32(value()));
        }
        b.AddRange(U32(0)); // no next IFD
        return [.. b];
    }

    private sealed class TiffWriter
    {
        public uint DataStart;
        public List<byte> Bytes { get; } = [];
        public uint Data(byte[] data)
        {
            var offset = DataStart + (uint)Bytes.Count;
            Bytes.AddRange(data);
            if (Bytes.Count % 2 == 1) Bytes.Add(0);
            return offset;
        }
    }

    /// <summary>ftyp + moov(mvhd + udta/©xyz). Only the parts the API reads.</summary>
    public static byte[] Mp4(DateTime? createdUtc = null, string? iso6709 = null, string? appleCreationDate = null)
    {
        var ftyp = Box("ftyp", [.. "isom"u8.ToArray(), .. U32(0x200), .. "isomiso2mp41"u8.ToArray()]);
        var mvhd = new byte[100];
        if (createdUtc is { } c)
        {
            var secs = (uint)(c - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(4), secs);  // creation time (after version/flags)
            BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(8), secs);  // modification time
        }
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 1000);     // timescale
        var udta = new List<byte>();
        if (iso6709 is not null) udta.AddRange(Box("©xyz", Encoding.Latin1.GetBytes("\0\0\0\0" + iso6709)));
        if (appleCreationDate is not null) udta.AddRange(Box("meta", Encoding.ASCII.GetBytes("com.apple.quicktime.creationdate" + appleCreationDate)));
        var moov = Box("moov", [.. Box("mvhd", mvhd), .. Box("udta", [.. udta])]);
        return [.. ftyp, .. moov, .. Box("mdat", new byte[512])];
    }

    public static byte[] Mp3() => [.. "ID3"u8.ToArray(), 4, 0, 0, 0, 0, 0, 0xFF, 0xFB, 0x90, 0x00, .. new byte[400]];
    public static byte[] Png() => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[64]];
    public static byte[] Heic() => [.. Box("ftyp", [.. "heic"u8.ToArray(), .. U32(0), .. "mif1heic"u8.ToArray()]), .. new byte[64]];
    public static byte[] Svg() => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

    private static byte[] Box(string type, byte[] payload) => [.. U32((uint)(8 + payload.Length)), .. Encoding.Latin1.GetBytes(type), .. payload];
    private static byte[] U16(ushort v) => [(byte)(v >> 8), (byte)v];
    private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>multipart/form-data body as a browser would send it.</summary>
    public static MultipartFormDataContent Form(byte[] bytes, string fileName, string contentType = "application/octet-stream",
        string? lastModified = null, int? utcOffsetMinutes = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        if (lastModified is not null) form.Add(new StringContent(lastModified), "lastModified");
        if (utcOffsetMinutes is not null) form.Add(new StringContent(utcOffsetMinutes.Value.ToString()), "utcOffsetMinutes");
        return form;
    }
}
