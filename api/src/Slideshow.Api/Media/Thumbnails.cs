using SkiaSharp;

namespace Slideshow.Api.Media;

/// <summary>Small JPEG previews of photos for the album pages.</summary>
public static class Thumbnails
{
    /// <summary>Longest edge in pixels: about 2x the size shown, for sharp high-DPI screens.</summary>
    public const int MaxEdge = 400;
    private const int Quality = 80;
    public const string ContentType = "image/jpeg";
    private static readonly SKColor Background = new(0x14, 0x18, 0x1d); // behind transparent PNGs; matches the site

    /// <summary>
    /// A JPEG thumbnail of the image in `source`, upright per its EXIF orientation; null if the format
    /// can't be decoded here (e.g. AVIF) or the file is damaged.
    /// </summary>
    public static byte[]? TryCreate(Stream source)
    {
        try
        {
            // SKCodec takes ownership of the stream it reads; the wrapper keeps the caller's stream open.
            using var codec = SKCodec.Create(new SKManagedStream(source, disposeManagedStream: false));
            if (codec is null) return null;
            var full = codec.Info;
            if (full.Width <= 0 || full.Height <= 0) return null;

            // Decode at reduced size where the format allows (JPEG: 1/2, 1/4, 1/8), at least 2x the target.
            var scale = Math.Min(1f, (float)MaxEdge / Math.Max(full.Width, full.Height));
            var decodeSize = codec.GetScaledDimensions(Math.Min(1f, scale * 2));
            var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(decodeInfo);
            var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

            var f = Math.Min(1f, (float)MaxEdge / Math.Max(decodeSize.Width, decodeSize.Height));
            int w = Math.Max(1, (int)Math.Round(decodeSize.Width * f)), h = Math.Max(1, (int)Math.Round(decodeSize.Height * f));
            using var resized = decoded.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (resized is null) return null;

            var origin = codec.EncodedOrigin;
            var sideways = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            using var surface = SKSurface.Create(new SKImageInfo(sideways ? h : w, sideways ? w : h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(Background);
            canvas.SetMatrix(Orientation(origin, w, h));
            canvas.DrawBitmap(resized, 0, 0);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Quality);
            return data?.ToArray();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Maps a w×h image stored with EXIF orientation `origin` onto an upright canvas.</summary>
    private static SKMatrix Orientation(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        // x' = ScaleX·x + SkewX·y + TransX;  y' = SkewY·x + ScaleY·y + TransY
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),      // mirrored
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),  // 180°
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),    // flipped
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),        // transposed
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),      // 90° clockwise (portrait phone photo)
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),  // transverse
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),    // 90° counter-clockwise
        _ => SKMatrix.Identity,
    };
}
