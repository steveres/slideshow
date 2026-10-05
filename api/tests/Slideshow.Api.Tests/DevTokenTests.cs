using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Slideshow.Api.Tests.Infrastructure;

namespace Slideshow.Api.Tests;

/// <summary>POST /dev/token: Development-only sign-in used by the website until Entra is set up.</summary>
public sealed class DevTokenTests
{
    private static ApiFactory Development() => new()
    {
        Environment = "Development",
        Settings = new()
        {
            // Same shape `dotnet user-jwts` writes to user secrets and appsettings.Development.json.
            ["Authentication:Schemes:Bearer:ValidIssuer"] = "dotnet-user-jwts",
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "http://localhost:5160",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = "dotnet-user-jwts",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Id"] = "testkey1",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String("development-signing-key-32-bytes!"u8.ToArray()),
            ["Authentication:Schemes:Bearer:SigningKeys:0:Length"] = "32",
        },
    };

    private static async Task<JsonElement> TokenAsync(HttpClient client, string email, string? name = null)
    {
        var res = await client.PostAsJsonAsync("/dev/token", new { email, name });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Is_not_available_outside_development()
    {
        using var f = new ApiFactory();
        var res = await f.CreateClient().PostAsJsonAsync("/dev/token", new { email = "a@example.test" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Issues_tokens_the_api_accepts_and_the_same_email_is_the_same_user()
    {
        using var f = Development();
        var client = f.CreateClient();

        var first = await TokenAsync(client, "Alice@Example.test", "Alice");
        var again = await TokenAsync(client, "alice@example.test");
        var other = await TokenAsync(client, "bob@example.test");
        Assert.Equal(first.GetProperty("userId").GetString(), again.GetProperty("userId").GetString());
        Assert.NotEqual(first.GetProperty("userId").GetString(), other.GetProperty("userId").GetString());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.GetProperty("accessToken").GetString());
        var register = await client.PostAsync("/api/v1/account", null);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var account = await register.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Alice", account.GetProperty("displayName").GetString());
        Assert.Equal("alice@example.test", account.GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_token_issued_right_after_logout_works()
    {
        using var f = Development();
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await TokenAsync(client, "c@example.test")).GetProperty("accessToken").GetString());
        (await client.PostAsync("/api/v1/account", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/account/logout", null)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", (await TokenAsync(client, "c@example.test")).GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/account")).StatusCode);
    }

    [Fact]
    public async Task Requires_an_email()
    {
        using var f = Development();
        var res = await f.CreateClient().PostAsJsonAsync("/dev/token", new { email = "not-an-email" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
