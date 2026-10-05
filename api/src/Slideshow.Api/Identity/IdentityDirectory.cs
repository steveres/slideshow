using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace Slideshow.Api.Identity;

/// <summary>Operations on the user's identity in Entra External ID.</summary>
public interface IIdentityDirectory
{
    /// <summary>Invalidates the user's refresh tokens so they must sign in again everywhere.</summary>
    Task RevokeSessionsAsync(string userId, CancellationToken ct);
    /// <summary>Deletes the user from the tenant. Succeeds if they are already gone.</summary>
    Task DeleteUserAsync(string userId, CancellationToken ct);
}

/// <summary>Used when Graph isn't configured (local development): the Entra-side step is skipped.</summary>
public sealed class NoOpIdentityDirectory(ILogger<NoOpIdentityDirectory> log) : IIdentityDirectory
{
    public Task RevokeSessionsAsync(string userId, CancellationToken ct)
    {
        log.LogWarning("Graph is not configured; refresh tokens were not revoked.");
        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(string userId, CancellationToken ct)
    {
        log.LogWarning("Graph is not configured; the Entra user was not deleted.");
        return Task.CompletedTask;
    }
}

/// <summary>Microsoft Graph against the customer tenant (application permission User.ReadWrite.All).</summary>
public sealed class GraphIdentityDirectory : IIdentityDirectory
{
    private static readonly string[] GraphScope = ["https://graph.microsoft.com/.default"];
    private readonly HttpClient _http;
    private readonly TokenCredential _credential;

    public GraphIdentityDirectory(HttpClient http, IOptions<GraphOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        var o = options.Value;
        if (o.UseManagedIdentityFederation)
        {
            // The managed identity lives in the Azure tenant; it signs in to the customer tenant as the
            // Graph app via a federated credential, so no secret is stored anywhere.
            var mi = string.IsNullOrEmpty(o.ManagedIdentityClientId)
                ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(o.ManagedIdentityClientId));
            _credential = new ClientAssertionCredential(o.TenantId, o.ClientId, async ct =>
                (await mi.GetTokenAsync(new TokenRequestContext(["api://AzureADTokenExchange/.default"]), ct)).Token);
        }
        else
        {
            _credential = new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret);
        }
    }

    public async Task RevokeSessionsAsync(string userId, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Post, $"users/{Uri.EscapeDataString(userId)}/revokeSignInSessions", ct);
        if (res.StatusCode != System.Net.HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
    }

    public async Task DeleteUserAsync(string userId, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Delete, $"users/{Uri.EscapeDataString(userId)}", ct);
        if (res.StatusCode != System.Net.HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new("Bearer", token.Token);
        return await _http.SendAsync(req, ct);
    }
}
