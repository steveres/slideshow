using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Slideshow.Api.Tests.Infrastructure;
using static Slideshow.Api.Tests.AlbumTests;

namespace Slideshow.Api.Tests;

public sealed class MediaTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Client, string Album)> NewAlbumAsync()
    {
        var client = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        return (client, await CreateAlbumAsync(client, "Media"));
    }

    [Fact]
    public async Task Upload_reads_exif_date_and_gps_and_serves_the_bytes()
    {
        var (client, album) = await NewAlbumAsync();
        var jpeg = TestMedia.Jpeg(new DateTime(2026, 7, 4, 9, 31, 5), (-33.8688, 151.2093));

        var info = await UploadAsync(client, album, jpeg, @"C:\fakepath\Sydney Opera.jpg");
        Assert.Equal("Sydney Opera.jpg", info.GetProperty("fileName").GetString());
        Assert.Equal("image", info.GetProperty("kind").GetString());
        Assert.Equal("image/jpeg", info.GetProperty("contentType").GetString());
        Assert.Equal(jpeg.Length, info.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("2026-07-04T09:31:05", info.GetProperty("taken").GetString());
        Assert.Equal("exif", info.GetProperty("takenSource").GetString());
        Assert.Equal(-33.8688, info.GetProperty("location").GetProperty("lat").GetDouble(), 3);
        Assert.Equal(151.2093, info.GetProperty("location").GetProperty("lon").GetDouble(), 3);
        Assert.True(info.GetProperty("playable").GetBoolean());
        Assert.Empty(info.GetProperty("missing").EnumerateArray());

        var url = info.GetProperty("url").GetString()!;
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/jpeg", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(jpeg, await res.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("inline", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.NotNull(res.Headers.ETag);

        var again = await client.GetFromJsonAsync<JsonElement>(url + "/info");
        Assert.Equal(info.GetProperty("id").GetString(), again.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Content_supports_range_requests()
    {
        var (client, album) = await NewAlbumAsync();
        var jpeg = TestMedia.Jpeg(padding: 4096);
        var url = (await UploadAsync(client, album, jpeg, "a.jpg")).GetProperty("url").GetString();

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(10, 19);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal(jpeg[10..20], await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Video_reads_apple_creation_date_and_iso6709_location()
    {
        var (client, album) = await NewAlbumAsync();
        var mp4 = TestMedia.Mp4(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "+37.7749-122.4194/", "2026-07-04T09:31:05-0700");
        var info = await UploadAsync(client, album, mp4, "clip.mov", utcOffsetMinutes: 600);
        Assert.Equal("video", info.GetProperty("kind").GetString());
        Assert.Equal("video/mp4", info.GetProperty("contentType").GetString());
        Assert.Equal("2026-07-04T09:31:05", info.GetProperty("taken").GetString()); // local time as recorded, offset ignored
        Assert.Equal("video", info.GetProperty("takenSource").GetString());
        Assert.Equal(37.7749, info.GetProperty("location").GetProperty("lat").GetDouble(), 4);
        Assert.Equal(-122.4194, info.GetProperty("location").GetProperty("lon").GetDouble(), 4);
    }

    [Fact]
    public async Task Without_a_capture_date_in_the_file_there_is_no_date()
    {
        // File modified times are never used: a photo without an EXIF date stays undated and isn't shown.
        var (client, album) = await NewAlbumAsync();
        var info = await UploadAsync(client, album, TestMedia.Png(), "screenshot.png", utcOffsetMinutes: -300);
        Assert.Equal(JsonValueKind.Null, info.GetProperty("taken").ValueKind);
        Assert.Equal(JsonValueKind.Null, info.GetProperty("takenSource").ValueKind);
        Assert.False(info.GetProperty("playable").GetBoolean());
        Assert.Equal(["date", "location"], info.GetProperty("missing").EnumerateArray().Select(m => m.GetString()));
    }

    [Theory]
    [InlineData("svg")]
    [InlineData("heic")]
    [InlineData("text")]
    [InlineData("mp3-as-image")]
    public async Task Images_rejects_unsupported_content(string kind)
    {
        var (client, album) = await NewAlbumAsync();
        var (bytes, name) = kind switch
        {
            "svg" => (TestMedia.Svg(), "pic.jpg"),        // the name lies; the content decides
            "heic" => (TestMedia.Heic(), "IMG_1.heic"),
            "text" => ("hello world"u8.ToArray(), "notes.png"),
            _ => (TestMedia.Mp3(), "song.mp3"),
        };
        var res = await client.PostAsync($"/api/v1/albums/{album}/images", TestMedia.Form(bytes, name, "image/jpeg"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
        Assert.Equal("unsupported_media_type", await AuthAndAccountTests.Code(res));
    }

    [Fact]
    public async Task Music_accepts_only_mp3()
    {
        var (client, album) = await NewAlbumAsync();
        var res = await client.PostAsync($"/api/v1/albums/{album}/music", TestMedia.Form(TestMedia.Jpeg(), "a.mp3"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);

        var track = await UploadAsync(client, album, TestMedia.Mp3(), "song.mp3", "music");
        Assert.Equal("audio", track.GetProperty("kind").GetString());
        Assert.Equal("audio/mpeg", track.GetProperty("contentType").GetString());
        Assert.Equal(JsonValueKind.Null, track.GetProperty("taken").ValueKind);
        Assert.True(track.GetProperty("playable").GetBoolean()); // music needs no date or location

        var music = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/albums/{album}/music");
        Assert.Single(music!);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/albums/{album}/images"))!);

        // A music id isn't reachable through the images collection.
        var id = track.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/albums/{album}/images/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/albums/{album}/music/{id}")).StatusCode);
    }

    [Fact]
    public async Task Upload_without_a_file_is_400()
    {
        var (client, album) = await NewAlbumAsync();
        var form = new MultipartFormDataContent { { new StringContent("x"), "other" } };
        var res = await client.PostAsync($"/api/v1/albums/{album}/images", form);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Upload_to_missing_album_is_404()
    {
        var (client, _) = await NewAlbumAsync();
        var res = await client.PostAsync($"/api/v1/albums/{Guid.NewGuid()}/images", TestMedia.Form(TestMedia.Jpeg(), "a.jpg"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task List_is_in_upload_order_and_delete_removes_bytes()
    {
        var (client, album) = await NewAlbumAsync();
        var first = await UploadAsync(client, album, TestMedia.Jpeg(new DateTime(2030, 1, 1)), "z.jpg");
        var second = await UploadAsync(client, album, TestMedia.Jpeg(new DateTime(2000, 1, 1)), "a.jpg");

        var list = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/albums/{album}/images");
        Assert.Equal(["z.jpg", "a.jpg"], list!.Select(i => i.GetProperty("fileName").GetString()));

        var url = first.GetProperty("url").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url + "/info")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(url)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/albums/{album}/images"))!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(second.GetProperty("url").GetString())).StatusCode);
    }

    [Fact]
    public async Task Per_kind_size_limit_is_enforced()
    {
        using var f = new ApiFactory { Settings = new() { ["Limits:MaxImageBytes"] = "1000" } };
        var client = await f.RegisteredClientAsync(Guid.NewGuid().ToString());
        var album = await CreateAlbumAsync(client, "Small");
        var res = await client.PostAsync($"/api/v1/albums/{album}/images", TestMedia.Form(TestMedia.Jpeg(padding: 2000), "big.jpg"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        Assert.Equal("file_too_large", await AuthAndAccountTests.Code(res));
        // Videos have their own (larger) limit.
        await UploadAsync(client, album, TestMedia.Mp4(), "clip.mp4");
    }

    [Fact]
    public async Task Quota_is_enforced_per_user()
    {
        using var f = new ApiFactory { Settings = new() { ["Limits:QuotaBytesPerUser"] = "1200" } };
        var client = await f.RegisteredClientAsync(Guid.NewGuid().ToString());
        var album = await CreateAlbumAsync(client, "Quota");
        await UploadAsync(client, album, TestMedia.Jpeg(padding: 600), "a.jpg");
        var res = await client.PostAsync($"/api/v1/albums/{album}/images", TestMedia.Form(TestMedia.Jpeg(padding: 600), "b.jpg"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        Assert.Equal("quota_exceeded", await AuthAndAccountTests.Code(res));

        // Another user has their own quota.
        var other = await f.RegisteredClientAsync(Guid.NewGuid().ToString());
        await UploadAsync(other, await CreateAlbumAsync(other, "Mine"), TestMedia.Jpeg(padding: 600), "a.jpg");
    }

    [Fact]
    public async Task Files_per_album_limit_is_enforced()
    {
        using var f = new ApiFactory { Settings = new() { ["Limits:MaxFilesPerAlbum"] = "1" } };
        var client = await f.RegisteredClientAsync(Guid.NewGuid().ToString());
        var album = await CreateAlbumAsync(client, "Tiny");
        await UploadAsync(client, album, TestMedia.Jpeg(), "a.jpg");
        var res = await client.PostAsync($"/api/v1/albums/{album}/music", TestMedia.Form(TestMedia.Mp3(), "a.mp3"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }
}
