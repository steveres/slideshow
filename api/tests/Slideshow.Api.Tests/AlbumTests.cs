using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Slideshow.Api.Tests.Infrastructure;

namespace Slideshow.Api.Tests;

public sealed class AlbumTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    internal static async Task<string> CreateAlbumAsync(HttpClient client, string name, string? description = null)
    {
        var res = await client.PostAsJsonAsync("/api/v1/albums", new { name, description });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    internal static async Task<JsonElement> UploadAsync(HttpClient client, string album, byte[] bytes, string name,
        string collection = "images", int? utcOffsetMinutes = null)
    {
        var res = await client.PostAsync($"/api/v1/albums/{album}/{collection}", TestMedia.Form(bytes, name, utcOffsetMinutes: utcOffsetMinutes));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Create_list_info_and_delete()
    {
        var client = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        var older = await CreateAlbumAsync(client, "  Italy 2026  ", "Summer trip");
        var newer = await CreateAlbumAsync(client, "Japan");

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/albums");
        Assert.Equal([newer, older], list!.Select(a => a.GetProperty("id").GetString()));

        var info = await client.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{older}/info");
        Assert.Equal("Italy 2026", info.GetProperty("name").GetString());
        Assert.Equal("Summer trip", info.GetProperty("description").GetString());
        Assert.Equal(0, info.GetProperty("imageCount").GetInt32());
        Assert.True(info.GetProperty("isStale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("compiledAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/albums/{older}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/albums/{older}/info")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/albums/{older}")).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/v1/albums"))!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_requires_a_name(string? name)
    {
        var client = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        var res = await client.PostAsJsonAsync("/api/v1/albums", new { name });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("validation_failed", await AuthAndAccountTests.Code(res));
    }

    [Fact]
    public async Task Create_rejects_overlong_name()
    {
        var client = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        var res = await client.PostAsJsonAsync("/api/v1/albums", new { name = new string('x', 201) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Album_limit_is_enforced()
    {
        using var f = new ApiFactory { Settings = new() { ["Limits:MaxAlbumsPerUser"] = "1" } };
        var client = await f.RegisteredClientAsync(Guid.NewGuid().ToString());
        await CreateAlbumAsync(client, "one");
        var res = await client.PostAsJsonAsync("/api/v1/albums", new { name = "two" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Get_before_compile_is_409()
    {
        var client = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        var album = await CreateAlbumAsync(client, "Uncompiled");
        var res = await client.GetAsync($"/api/v1/albums/{album}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("album_not_compiled", await AuthAndAccountTests.Code(res));
    }

    [Fact]
    public async Task Compile_orders_slides_by_date_and_leaves_out_those_without_date_or_location()
    {
        var client = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        var album = await CreateAlbumAsync(client, "Trip");

        // Uploaded out of order on purpose.
        await UploadAsync(client, album, TestMedia.Jpeg(new DateTime(2026, 7, 3, 12, 0, 0), (41.9028, 12.4964)), "rome.jpg");
        await UploadAsync(client, album, TestMedia.Jpeg(), "IMG_10.jpg");                                              // no date, no location
        await UploadAsync(client, album, TestMedia.Jpeg(gps: (48.8566, 2.3522)), "paris-undated.jpg");                 // no date
        await UploadAsync(client, album, TestMedia.Jpeg(new DateTime(2026, 7, 2, 8, 0, 0)), "nowhere.jpg");            // no location
        await UploadAsync(client, album, TestMedia.Mp4(new DateTime(2026, 7, 2, 18, 0, 0, DateTimeKind.Utc), "+45.0703+007.6869/"), "clip.mp4", utcOffsetMinutes: 120);
        await UploadAsync(client, album, TestMedia.Jpeg(new DateTime(2026, 7, 1, 9, 0, 0), (45.4642, 9.19)), "milan.jpg");
        await UploadAsync(client, album, TestMedia.Jpeg(new DateTime(2026, 7, 1, 9, 0, 0), (45.4642, 9.19)), "IMG_2.jpg"); // same time as milan: name breaks the tie
        await UploadAsync(client, album, TestMedia.Mp3(), "song B.mp3", "music");
        await UploadAsync(client, album, TestMedia.Mp3(), "song A.mp3", "music");

        var res = await client.PostAsync($"/api/v1/albums/{album}/compile", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var manifest = await res.Content.ReadFromJsonAsync<JsonElement>();

        var slides = manifest.GetProperty("slides").EnumerateArray().ToList();
        Assert.Equal(["IMG_2.jpg", "milan.jpg", "clip.mp4", "rome.jpg"], slides.Select(s => s.GetProperty("fileName").GetString()));
        Assert.All(slides, s => Assert.True(s.GetProperty("playable").GetBoolean()));
        Assert.Equal("2026-07-01T09:00:00", slides[0].GetProperty("taken").GetString());
        Assert.Equal("2026-07-02T20:00:00", slides[2].GetProperty("taken").GetString()); // UTC + uploader's offset
        Assert.Equal("video", slides[2].GetProperty("kind").GetString());
        Assert.Equal(45.4642, slides[0].GetProperty("location").GetProperty("lat").GetDouble(), 3);
        Assert.StartsWith($"/api/v1/albums/{album}/images/", slides[0].GetProperty("url").GetString());
        Assert.Equal(3, manifest.GetProperty("excludedCount").GetInt32());

        Assert.Equal(["song A.mp3", "song B.mp3"], manifest.GetProperty("music").EnumerateArray().Select(s => s.GetProperty("fileName").GetString()));

        // GET returns the same manifest; it's fresh until the contents change.
        var get = await client.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}");
        Assert.False(get.GetProperty("isStale").GetBoolean());
        Assert.Equal(4, get.GetProperty("slides").GetArrayLength());

        var info = await client.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}/info");
        Assert.Equal(6, info.GetProperty("imageCount").GetInt32());
        Assert.Equal(1, info.GetProperty("videoCount").GetInt32());
        Assert.Equal(2, info.GetProperty("musicCount").GetInt32());
        Assert.Equal(3, info.GetProperty("excludedCount").GetInt32());
        Assert.False(info.GetProperty("isStale").GetBoolean());

        // The file list says why each left-out file isn't shown.
        var listed = (await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/albums/{album}/images"))!
            .ToDictionary(i => i.GetProperty("fileName").GetString()!, i => i.GetProperty("missing").EnumerateArray().Select(m => m.GetString()!).ToArray());
        Assert.Equal(["date", "location"], listed["IMG_10.jpg"]);
        Assert.Equal(["date"], listed["paris-undated.jpg"]);
        Assert.Equal(["location"], listed["nowhere.jpg"]);
        Assert.Empty(listed["rome.jpg"]);

        await UploadAsync(client, album, TestMedia.Jpeg(), "late.jpg");
        Assert.True((await client.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}")).GetProperty("isStale").GetBoolean());
        Assert.True((await client.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}/info")).GetProperty("isStale").GetBoolean());
    }

    [Fact]
    public async Task Delete_album_removes_its_files()
    {
        var oid = Guid.NewGuid().ToString();
        var client = await factory.RegisteredClientAsync(oid);
        var album = await CreateAlbumAsync(client, "Gone");
        var image = await UploadAsync(client, album, TestMedia.Jpeg(), "a.jpg");
        var albumDir = Path.Combine(factory.MediaRoot, oid, Guid.Parse(album).ToString("N"));
        Assert.True(Directory.Exists(albumDir));

        (await client.DeleteAsync($"/api/v1/albums/{album}")).EnsureSuccessStatusCode();

        Assert.False(Directory.Exists(albumDir));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/albums/{album}/images/{image.GetProperty("id").GetString()}")).StatusCode);
    }
}
