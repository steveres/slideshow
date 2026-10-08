using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Slideshow.Api.Tests.Infrastructure;
using static Slideshow.Api.Tests.AlbumTests;

namespace Slideshow.Api.Tests;

/// <summary>Link sharing: anyone with the link can play the slideshow, and nothing else.</summary>
public sealed class SharingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Owner, string Album, JsonElement Shown, JsonElement Hidden, JsonElement Track)> SharedAlbumAsync()
    {
        var owner = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        var album = await CreateAlbumAsync(owner, "Shared trip");
        var shown = await UploadAsync(owner, album, TestMedia.Jpeg(new DateTime(2026, 7, 1, 9, 0, 0), (45.4642, 9.19)), "milan.jpg");
        var hidden = await UploadAsync(owner, album, TestMedia.Jpeg(), "no-date-or-place.jpg"); // not in the slideshow
        var track = await UploadAsync(owner, album, TestMedia.Mp3(), "song.mp3", "music");
        return (owner, album, shown, hidden, track);
    }

    private static async Task<JsonElement> ShareAsync(HttpClient owner, string album, object body)
    {
        var res = await owner.PutAsJsonAsync($"/api/v1/albums/{album}/share", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string FileId(JsonElement media) => media.GetProperty("id").GetString()!;

    [Fact]
    public async Task Albums_are_not_shared_until_the_owner_turns_it_on()
    {
        var (owner, album, _, _, _) = await SharedAlbumAsync();
        var share = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}/share");
        Assert.False(share.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, share.GetProperty("token").ValueKind);
        Assert.True(share.GetProperty("showMap").GetBoolean()); // default: viewers see the map
        Assert.False((await owner.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}/info")).GetProperty("isShared").GetBoolean());
    }

    [Fact]
    public async Task Anyone_with_the_link_can_play_the_slideshow_without_signing_in()
    {
        var (owner, album, shown, _, track) = await SharedAlbumAsync();
        var token = (await ShareAsync(owner, album, new { enabled = true })).GetProperty("token").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{22}$", token);
        Assert.True((await owner.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}/info")).GetProperty("isShared").GetBoolean());

        var anonymous = factory.CreateClient(); // no token at all
        var res = await anonymous.GetAsync($"/api/v1/shared/{token}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var shared = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Shared trip", shared.GetProperty("name").GetString());
        Assert.True(shared.GetProperty("showMap").GetBoolean());
        var slide = Assert.Single(shared.GetProperty("slides").EnumerateArray());          // only the playable photo
        Assert.Equal($"/api/v1/shared/{token}/images/{FileId(shown)}", slide.GetProperty("url").GetString());
        Assert.Equal(45.4642, slide.GetProperty("location").GetProperty("lat").GetDouble(), 3);
        Assert.False(slide.TryGetProperty("albumId", out _));
        Assert.Equal(JsonValueKind.Null, slide.GetProperty("thumbnailUrl").ValueKind);
        var music = Assert.Single(shared.GetProperty("music").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(slide.GetProperty("url").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(music.GetProperty("url").GetString())).StatusCode);
        Assert.Equal(FileId(track), FileId(music));
    }

    [Fact]
    public async Task The_link_gives_nothing_but_the_slideshow()
    {
        var (owner, album, shown, hidden, track) = await SharedAlbumAsync();
        var token = (await ShareAsync(owner, album, new { enabled = true })).GetProperty("token").GetString()!;
        var anonymous = factory.CreateClient();

        // A photo left out of the slideshow stays private, as does a file asked for in the wrong collection.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/shared/{token}/images/{FileId(hidden)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/shared/{token}/images/{FileId(track)}")).StatusCode);

        // Another album's files can't be fetched through this link.
        var other = await CreateAlbumAsync(owner, "Not shared");
        var otherPhoto = await UploadAsync(owner, other, TestMedia.Jpeg(new DateTime(2026, 7, 2), (41.9, 12.5)), "rome.jpg");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/shared/{token}/images/{FileId(otherPhoto)}")).StatusCode);

        // The normal album endpoints still need sign-in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/albums/{album}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{shown.GetProperty("url").GetString()}")).StatusCode);

        // Made-up and malformed codes look just like revoked ones.
        foreach (var bad in new[] { "AAAAAAAAAAAAAAAAAAAAAA", "short", "not a token at all!!!!" })
        {
            var res = await anonymous.GetAsync($"/api/v1/shared/{Uri.EscapeDataString(bad)}");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task Turning_sharing_off_or_getting_a_new_link_stops_old_links_at_once()
    {
        var (owner, album, shown, _, _) = await SharedAlbumAsync();
        var anonymous = factory.CreateClient();
        var first = (await ShareAsync(owner, album, new { enabled = true })).GetProperty("token").GetString()!;

        var reset = await owner.PostAsync($"/api/v1/albums/{album}/share/reset", null);
        var second = (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/shared/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/shared/{first}/images/{FileId(shown)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/v1/shared/{second}")).StatusCode);

        var off = await ShareAsync(owner, album, new { enabled = false });
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/shared/{second}")).StatusCode);

        // Turning it back on gives a fresh link; the old ones stay dead.
        var third = (await ShareAsync(owner, album, new { enabled = true })).GetProperty("token").GetString()!;
        Assert.NotEqual(second, third);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/shared/{second}")).StatusCode);

        // Getting a new link needs sharing to be on.
        await ShareAsync(owner, album, new { enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/v1/albums/{album}/share/reset", null)).StatusCode);
    }

    [Fact]
    public async Task Hiding_the_map_also_hides_the_photos_locations()
    {
        var (owner, album, _, _, _) = await SharedAlbumAsync();
        var share = await ShareAsync(owner, album, new { enabled = true, showMap = false });
        Assert.False(share.GetProperty("showMap").GetBoolean());

        var shared = await factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/v1/shared/{share.GetProperty("token").GetString()}");
        Assert.False(shared.GetProperty("showMap").GetBoolean());
        Assert.All(shared.GetProperty("slides").EnumerateArray(), s => Assert.Equal(JsonValueKind.Null, s.GetProperty("location").ValueKind));

        // Changing only the map choice keeps the same link.
        var again = await ShareAsync(owner, album, new { showMap = true });
        Assert.Equal(share.GetProperty("token").GetString(), again.GetProperty("token").GetString());
        Assert.True(again.GetProperty("showMap").GetBoolean());
    }

    [Fact]
    public async Task Viewers_see_the_album_as_it_is_now()
    {
        var (owner, album, _, _, _) = await SharedAlbumAsync();
        var token = (await ShareAsync(owner, album, new { enabled = true })).GetProperty("token").GetString();
        var anonymous = factory.CreateClient();
        Assert.Single((await anonymous.GetFromJsonAsync<JsonElement>($"/api/v1/shared/{token}")).GetProperty("slides").EnumerateArray());

        await UploadAsync(owner, album, TestMedia.Jpeg(new DateTime(2026, 7, 3), (41.9, 12.5)), "rome.jpg"); // no compile by the owner
        Assert.Equal(2, (await anonymous.GetFromJsonAsync<JsonElement>($"/api/v1/shared/{token}")).GetProperty("slides").GetArrayLength());
    }

    [Fact]
    public async Task Only_the_owner_can_see_or_change_sharing()
    {
        var (owner, album, _, _, _) = await SharedAlbumAsync();
        await ShareAsync(owner, album, new { enabled = true });
        var stranger = await factory.RegisteredClientAsync(Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/albums/{album}/share")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/api/v1/albums/{album}/share", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/v1/albums/{album}/share/reset", null)).StatusCode);
        Assert.True((await owner.GetFromJsonAsync<JsonElement>($"/api/v1/albums/{album}/share")).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Deleting_the_album_ends_the_link()
    {
        var (owner, album, _, _, _) = await SharedAlbumAsync();
        var token = (await ShareAsync(owner, album, new { enabled = true })).GetProperty("token").GetString();
        (await owner.DeleteAsync($"/api/v1/albums/{album}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/api/v1/shared/{token}")).StatusCode);
    }
}
