using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Slideshow.Api.Tests.Infrastructure;
using static Slideshow.Api.Tests.AlbumTests;

namespace Slideshow.Api.Tests;

/// <summary>Another user's ids must behave exactly like ids that don't exist.</summary>
public sealed class IsolationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Users_cannot_see_or_change_each_others_albums_or_files()
    {
        var alice = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        var bob = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());

        var album = await CreateAlbumAsync(alice, "Alice's");
        var image = await UploadAsync(alice, album, TestMedia.Jpeg(), "private.jpg");
        var track = await UploadAsync(alice, album, TestMedia.Mp3(), "private.mp3", "music");
        (await alice.PostAsync($"/api/v1/albums/{album}/compile", null)).EnsureSuccessStatusCode();
        var imageUrl = image.GetProperty("url").GetString()!;
        var trackUrl = track.GetProperty("url").GetString()!;

        Assert.Empty((await bob.GetFromJsonAsync<JsonElement[]>("/api/v1/albums"))!);

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, $"/api/v1/albums/{album}"),
            (HttpMethod.Get, $"/api/v1/albums/{album}/info"),
            (HttpMethod.Post, $"/api/v1/albums/{album}/compile"),
            (HttpMethod.Get, $"/api/v1/albums/{album}/images"),
            (HttpMethod.Get, $"/api/v1/albums/{album}/music"),
            (HttpMethod.Get, imageUrl),
            (HttpMethod.Get, imageUrl + "/info"),
            (HttpMethod.Delete, imageUrl),
            (HttpMethod.Get, trackUrl),
            (HttpMethod.Delete, trackUrl),
            (HttpMethod.Delete, $"/api/v1/albums/{album}"),
        })
        {
            var res = await bob.SendAsync(new HttpRequestMessage(method, url));
            Assert.True(res.StatusCode == HttpStatusCode.NotFound, $"{method} {url} -> {res.StatusCode}");
        }

        var upload = await bob.PostAsync($"/api/v1/albums/{album}/images", TestMedia.Form(TestMedia.Jpeg(), "intruder.jpg"));
        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);

        // Alice's things are untouched.
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(imageUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(trackUrl)).StatusCode);
        var info = await alice.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}/info");
        Assert.Equal(1, info.GetProperty("imageCount").GetInt32());
        Assert.False(info.GetProperty("isStale").GetBoolean());
    }

    [Fact]
    public async Task Deleting_one_account_leaves_others_intact()
    {
        var aliceId = Guid.NewGuid().ToString();
        var alice = await factory.RegisteredClientAsync(aliceId);
        var bob = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        var bobAlbum = await CreateAlbumAsync(bob, "Bob's");
        var bobImage = await UploadAsync(bob, bobAlbum, TestMedia.Jpeg(), "b.jpg");
        await UploadAsync(alice, await CreateAlbumAsync(alice, "Alice's"), TestMedia.Jpeg(), "a.jpg");

        (await alice.DeleteAsync("/api/v1/account")).EnsureSuccessStatusCode();

        Assert.Single((await bob.GetFromJsonAsync<JsonElement[]>("/api/v1/albums"))!);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync(bobImage.GetProperty("url").GetString())).StatusCode);
    }
}
