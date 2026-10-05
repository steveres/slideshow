using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Slideshow.Api.Tests.Infrastructure;

namespace Slideshow.Api.Tests;

public sealed class AuthAndAccountTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string NewUser() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Health_is_anonymous()
    {
        var res = await factory.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Api_requires_a_token()
    {
        var res = await factory.CreateClient().GetAsync("/api/v1/albums");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Tokens_signed_by_another_key_are_rejected()
    {
        var forged = ApiFactory.Token(NewUser(), key: new SymmetricSecurityKey(Encoding.UTF8.GetBytes("an-attacker-key-that-is-long-enough-for-hs256!!")));
        var res = await factory.ClientFor("x", forged).GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Tokens_for_another_audience_are_rejected()
    {
        var res = await factory.ClientFor("x", ApiFactory.Token(NewUser(), audience: "some-other-api")).GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Tokens_without_the_scope_are_forbidden()
    {
        var res = await factory.ClientFor("x", ApiFactory.Token(NewUser(), scope: "openid profile")).GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Unregistered_users_cannot_use_albums()
    {
        var client = factory.ClientFor(NewUser());
        var res = await client.GetAsync("/api/v1/albums");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("account_not_registered", await Code(res));
        Assert.Equal("account_not_registered", await Code(await client.GetAsync("/api/v1/account")));
    }

    [Fact]
    public async Task Register_is_idempotent_and_returns_the_account()
    {
        var oid = NewUser();
        var client = factory.ClientFor(oid);

        var first = await client.PostAsync("/api/v1/account", null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var account = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(oid, account.GetProperty("id").GetString());
        Assert.Equal("Test User", account.GetProperty("displayName").GetString());
        Assert.Equal(0, account.GetProperty("usage").GetProperty("albums").GetInt32());

        var second = await client.PostAsync("/api/v1/account", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var get = await client.GetFromJsonAsync<JsonElement>("/api/v1/account");
        Assert.Equal(oid, get.GetProperty("id").GetString());
        Assert.EndsWith("Z", get.GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task Usage_counts_albums_files_and_bytes()
    {
        var client = await factory.RegisteredClientAsync(NewUser());
        var album = await AlbumTests.CreateAlbumAsync(client, "Usage");
        var jpeg = TestMedia.Jpeg();
        (await client.PostAsync($"/api/v1/albums/{album}/images", TestMedia.Form(jpeg, "a.jpg"))).EnsureSuccessStatusCode();

        var usage = (await client.GetFromJsonAsync<JsonElement>("/api/v1/account")).GetProperty("usage");
        Assert.Equal(1, usage.GetProperty("albums").GetInt32());
        Assert.Equal(1, usage.GetProperty("files").GetInt32());
        Assert.Equal(jpeg.Length, usage.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task Logout_rejects_tokens_issued_before_it()
    {
        var oid = NewUser();
        var client = await factory.RegisteredClientAsync(oid);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/account/logout", null)).StatusCode);
        Assert.Contains(oid, factory.Directory.Revoked);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/account")).StatusCode);

        // Signing in again yields a newer token, which works.
        var fresh = factory.ClientFor(oid, ApiFactory.Token(oid, issuedAt: DateTime.UtcNow.AddSeconds(2)));
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/v1/account")).StatusCode);
    }

    [Fact]
    public async Task Delete_account_removes_data_files_and_identity()
    {
        var oid = NewUser();
        var client = await factory.RegisteredClientAsync(oid);
        var album = await AlbumTests.CreateAlbumAsync(client, "Doomed");
        (await client.PostAsync($"/api/v1/albums/{album}/images", TestMedia.Form(TestMedia.Jpeg(), "a.jpg"))).EnsureSuccessStatusCode();
        Assert.True(Directory.Exists(Path.Combine(factory.MediaRoot, oid)));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/account")).StatusCode);

        Assert.Contains(oid, factory.Directory.Deleted);
        Assert.False(Directory.Exists(Path.Combine(factory.MediaRoot, oid)));
        Assert.Equal("account_not_registered", await Code(await client.GetAsync("/api/v1/account")));

        // Registering again starts from nothing.
        (await client.PostAsync("/api/v1/account", null)).EnsureSuccessStatusCode();
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/v1/albums") ?? []);
    }

    [Fact]
    public async Task Delete_account_reports_identity_failure_and_can_be_retried()
    {
        using var f = new ApiFactory();
        var oid = NewUser();
        var client = await f.RegisteredClientAsync(oid);

        f.Directory.FailDeletes = true;
        var res = await client.DeleteAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Equal("identity_delete_failed", await Code(res));

        f.Directory.FailDeletes = false;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/account")).StatusCode);
        Assert.Contains(oid, f.Directory.Deleted);
    }

    [Fact]
    public async Task Rate_limit_returns_429()
    {
        using var f = new ApiFactory { Settings = new() { ["Limits:RequestsPerMinute"] = "3" } };
        var client = f.ClientFor(NewUser());
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) statuses.Add((await client.GetAsync("/api/v1/account")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Cors_allows_only_configured_origins()
    {
        var client = factory.CreateClient();
        async Task<HttpResponseMessage> Preflight(string origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Options, "/api/v1/albums");
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", "POST");
            req.Headers.Add("Access-Control-Request-Headers", "authorization");
            return await client.SendAsync(req);
        }
        Assert.Equal("https://spa.test", (await Preflight("https://spa.test")).Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False((await Preflight("https://evil.test")).Headers.Contains("Access-Control-Allow-Origin"));
    }

    internal static async Task<string?> Code(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;
}
