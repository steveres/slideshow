namespace Slideshow.Api;

/// <summary>Token validation for Entra External ID ("Auth" section).</summary>
public sealed class AuthOptions
{
    /// <summary>e.g. https://contoso.ciamlogin.com/{tenantId}/v2.0</summary>
    public string Authority { get; set; } = "";
    /// <summary>The API app registration's client id (and/or its api:// URI).</summary>
    public string[] Audiences { get; set; } = [];
    /// <summary>Delegated scope the token must carry in "scp".</summary>
    public string RequiredScope { get; set; } = "Slideshow.Access";
}

/// <summary>"Database" section.</summary>
public sealed class DatabaseOptions
{
    /// <summary>"SqlServer" (Azure SQL) or "Sqlite" (local development and tests).</summary>
    public string Provider { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=App_Data/slideshow.db";
    public bool MigrateOnStartup { get; set; } = true;
}

/// <summary>"Storage" section.</summary>
public sealed class StorageOptions
{
    /// <summary>"AzureBlob" or "FileSystem".</summary>
    public string Provider { get; set; } = "FileSystem";
    /// <summary>FileSystem: root folder.</summary>
    public string RootPath { get; set; } = "App_Data/media";
    /// <summary>AzureBlob: e.g. https://account.blob.core.windows.net (managed identity), or a connection string for Azurite.</summary>
    public string? BlobServiceUri { get; set; }
    public string? ConnectionString { get; set; }
    public string ContainerName { get; set; } = "media";
}

/// <summary>"Graph" section: Microsoft Graph access to the customer tenant. Leave TenantId empty to disable.</summary>
public sealed class GraphOptions
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Authenticate as ClientId using the managed identity as a federated credential (preferred in Azure).</summary>
    public bool UseManagedIdentityFederation { get; set; } = true;
    /// <summary>Client id of a user-assigned managed identity; empty for system-assigned.</summary>
    public string? ManagedIdentityClientId { get; set; }
    /// <summary>Fallback for local testing against a real tenant; never commit it.</summary>
    public string? ClientSecret { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>"Limits" section.</summary>
public sealed class LimitsOptions
{
    public long MaxImageBytes { get; set; } = 50L * 1024 * 1024;
    public long MaxVideoBytes { get; set; } = 1024L * 1024 * 1024;
    public long MaxMusicBytes { get; set; } = 50L * 1024 * 1024;
    public long QuotaBytesPerUser { get; set; } = 10L * 1024 * 1024 * 1024;
    public int MaxFilesPerAlbum { get; set; } = 5000;
    public int MaxAlbumsPerUser { get; set; } = 500;
    /// <summary>Requests per user per minute.</summary>
    public int RequestsPerMinute { get; set; } = 600;

    public long MaxUploadBytes => Math.Max(MaxImageBytes, Math.Max(MaxVideoBytes, MaxMusicBytes));
}

/// <summary>"Cors" section.</summary>
public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];
}
