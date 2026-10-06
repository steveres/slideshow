using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Slideshow.Api.Data;
using Slideshow.Api.Tests.Infrastructure;
using static Slideshow.Api.Tests.AlbumTests;

namespace Slideshow.Api.Tests;

public sealed class ThumbnailTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Client, string Album, string UserId)> NewAlbumAsync()
    {
        var userId = Guid.NewGuid().ToString();
        var client = await factory.RegisteredClientAsync(userId);
        return (client, await CreateAlbumAsync(client, "Thumbs"), userId);
    }

    private static async Task<SKSizeI> ThumbnailSizeAsync(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/jpeg", res.Content.Headers.ContentType!.MediaType);
        using var bitmap = SKBitmap.Decode(await res.Content.ReadAsByteArrayAsync());
        Assert.NotNull(bitmap);
        return new SKSizeI(bitmap.Width, bitmap.Height);
    }

    [Fact]
    public async Task Photos_get_a_400px_preview_on_upload()
    {
        var (client, album, _) = await NewAlbumAsync();
        var info = await UploadAsync(client, album, TestMedia.RealJpeg(1200, 600), "wide.jpg");
        var thumb = info.GetProperty("thumbnailUrl").GetString();
        Assert.Equal(info.GetProperty("url").GetString() + "/thumbnail", thumb);
        Assert.Equal(new SKSizeI(400, 200), await ThumbnailSizeAsync(client, thumb!));
    }

    [Fact]
    public async Task Small_photos_are_not_enlarged()
    {
        var (client, album, _) = await NewAlbumAsync();
        var info = await UploadAsync(client, album, TestMedia.RealJpeg(120, 80), "small.jpg");
        Assert.Equal(new SKSizeI(120, 80), await ThumbnailSizeAsync(client, info.GetProperty("thumbnailUrl").GetString()!));
    }

    [Fact]
    public async Task Preview_is_upright_for_rotated_phone_photos()
    {
        // Stored landscape with EXIF orientation 6 ("rotate 90° clockwise"), as phones save portrait shots.
        var (client, album, _) = await NewAlbumAsync();
        var info = await UploadAsync(client, album, TestMedia.RealJpeg(800, 400, orientation: 6), "portrait.jpg");
        Assert.Equal(new SKSizeI(200, 400), await ThumbnailSizeAsync(client, info.GetProperty("thumbnailUrl").GetString()!));
    }

    [Fact]
    public async Task Undecodable_photos_and_videos_have_no_preview()
    {
        var (client, album, _) = await NewAlbumAsync();
        var broken = await UploadAsync(client, album, TestMedia.Jpeg(), "broken.jpg"); // valid header, no picture
        var video = await UploadAsync(client, album, TestMedia.Mp4(), "clip.mp4");
        Assert.Equal(JsonValueKind.Null, broken.GetProperty("thumbnailUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, video.GetProperty("thumbnailUrl").ValueKind);

        foreach (var m in new[] { broken, video })
        {
            var res = await client.GetAsync(m.GetProperty("url").GetString() + "/thumbnail");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Equal("no_thumbnail", await AuthAndAccountTests.Code(res));
        }
    }

    [Fact]
    public async Task Photos_uploaded_before_previews_get_one_on_first_request()
    {
        var (client, album, userId) = await NewAlbumAsync();
        var info = await UploadAsync(client, album, TestMedia.RealJpeg(900, 900), "old.jpg");
        var id = Guid.Parse(info.GetProperty("id").GetString()!);

        // Make it look like an upload from before thumbnails existed.
        var thumbFile = Path.Combine(factory.MediaRoot, userId, Guid.Parse(album).ToString("N"), id.ToString("N") + ".thumb");
        Assert.True(File.Exists(thumbFile));
        File.Delete(thumbFile);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.MediaFiles.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.ThumbnailState, ThumbnailState.None));
        }

        Assert.Equal(new SKSizeI(400, 400), await ThumbnailSizeAsync(client, info.GetProperty("thumbnailUrl").GetString()!));
        Assert.True(File.Exists(thumbFile)); // kept for next time
    }

    [Fact]
    public async Task Deleting_a_photo_deletes_its_preview()
    {
        var (client, album, userId) = await NewAlbumAsync();
        var info = await UploadAsync(client, album, TestMedia.RealJpeg(300, 200), "gone.jpg");
        var id = Guid.Parse(info.GetProperty("id").GetString()!);
        var thumbFile = Path.Combine(factory.MediaRoot, userId, Guid.Parse(album).ToString("N"), id.ToString("N") + ".thumb");
        Assert.True(File.Exists(thumbFile));

        (await client.DeleteAsync(info.GetProperty("url").GetString())).EnsureSuccessStatusCode();
        Assert.False(File.Exists(thumbFile));
    }

    [Fact]
    public async Task Album_cover_is_the_first_photo_in_slideshow_order()
    {
        var (client, album, _) = await NewAlbumAsync();
        Assert.Equal(JsonValueKind.Null, (await client.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}/info")).GetProperty("coverThumbnailUrl").ValueKind);

        // No playable photo yet: the first upload is the cover.
        var undated = await UploadAsync(client, album, TestMedia.RealJpeg(100, 100), "undated.jpg");
        var cover = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}/info")).GetProperty("coverThumbnailUrl").GetString();
        Assert.Equal(undated.GetProperty("thumbnailUrl").GetString(), cover);

        // Playable photos win, earliest first.
        await UploadAsync(client, album, TestMedia.RealJpeg(100, 100, new DateTime(2026, 7, 2), (45.46, 9.19)), "second.jpg");
        var first = await UploadAsync(client, album, TestMedia.RealJpeg(100, 100, new DateTime(2026, 7, 1), (41.90, 12.49)), "first.jpg");
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/albums");
        var card = list!.Single(a => a.GetProperty("id").GetString() == album);
        Assert.Equal(first.GetProperty("thumbnailUrl").GetString(), card.GetProperty("coverThumbnailUrl").GetString());
        Assert.False(card.TryGetProperty("coverId", out _)); // internal
    }
}
