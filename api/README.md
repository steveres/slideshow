# Slideshow API

ASP.NET Core 9 API that stores each user's albums (photos, videos, music) and serves them to the
slideshow SPA. Design and endpoint reference: [docs/api-design.md](../docs/api-design.md).

```
api/
  src/Slideshow.Api/            the API
    Endpoints/                  account, albums, images + music
    Identity/                   token validation, logout cut-off, Microsoft Graph
    Media/                      file-type sniffing, EXIF/video date + GPS, natural sort
    Storage/                    Azure Blob / local folder
    Data/                       EF Core model + Azure SQL migrations
  tests/Slideshow.Api.Tests/    integration + unit tests
infra/                          Bicep template, parameters, deploy script
```

## Run the tests

```powershell
cd api
dotnet test
```
No Azure, database server or emulator needed: the tests run the real app against SQLite and a temp
folder, with tokens signed by a test key.

## Run locally

Two launch profiles, both on http://localhost:5160:

| Profile | Database | Media storage | Needs |
|---|---|---|---|
| `local` (default) | SQLite file in `App_Data/` | folder `App_Data/media` | nothing |
| `local-azure` | SQL Server LocalDB (same migrations as Azure SQL) | Azurite (Blob Storage emulator) | LocalDB, Node.js |

```powershell
cd api\src\Slideshow.Api
dotnet run                               # or: dotnet run --launch-profile local-azure
```

For `local-azure`, start Azurite first in another terminal (`--skipApiVersionCheck` because the Azure
SDK is usually newer than the emulator):
```powershell
npx -p azurite azurite-blob --skipApiVersionCheck --location $env:TEMP\azurite
```

### Browse and try the API

Open http://localhost:5160 (Development only). It redirects to an API explorer (`/scalar`) listing
every endpoint, where you can send requests and upload files.

Every `/api/v1` call needs a token. Locally, make one with the .NET SDK's `user-jwts` tool (run in
`api\src\Slideshow.Api`):
```powershell
dotnet user-jwts create --scope Slideshow.Access --claim oid=11111111-2222-3333-4444-555555555555 --claim name="Local Dev" --output token
```
Paste it into the explorer's authentication box (Bearer). Use a different `oid` to act as another user.
Then: `POST /api/v1/account` (register) → `POST /api/v1/albums` → upload to `.../images` and `.../music`
→ `POST .../compile` → `GET /api/v1/albums/{id}`.

The website's development sign-in uses the same key: in Development only, `POST /dev/token`
`{ "email": "...", "name": "..." }` returns a token for that email (the same email is always the same
user). It is not mapped in any other environment.

These tokens only work in Development: they are signed with a key kept in your user secrets, and
configured in `appsettings.Development.json`. In Azure only Entra External ID tokens are accepted.

## Set up Entra External ID (once)

Done in the [Microsoft Entra admin center](https://entra.microsoft.com).

1. **Create an external tenant**: Entra ID → Overview → Manage tenants → Create → *External*.
   Note its **tenant id** and **subdomain** (`<subdomain>.onmicrosoft.com`).
   The token authority is `https://<subdomain>.ciamlogin.com/<tenant-id>/v2.0`.
2. Switch to the external tenant. **Register the API**: App registrations → New → "Slideshow API",
   single tenant.
   - Expose an API → set the Application ID URI (`api://<api-client-id>`) → Add a scope
     `Slideshow.Access` (who can consent: admins and users).
   - Manifest → set `"requestedAccessTokenVersion": 2`.
   - Note the **Application (client) id** → `apiClientId`.
3. **Register the SPA**: App registrations → New → "Slideshow SPA", platform *Single-page application*,
   redirect URI where the SPA will run (e.g. `http://localhost:5173`). API permissions → Add →
   My APIs → Slideshow API → `Slideshow.Access` → Grant admin consent.
4. **User flow**: External Identities → User flows → New → "Sign up and sign in" (email + password,
   or email one-time passcode; collect Display Name). Then Applications → add *Slideshow SPA*.
5. **Graph access** (lets the API delete the Entra user on account deletion and revoke sessions on
   logout). Optional: without it those two steps are skipped and logged; data is still deleted and
   old tokens are still refused.
   - Preferred, no secrets: in your **Azure** tenant, register a *multi-tenant* app "Slideshow Graph".
     Under Certificates & secrets → Federated credentials → Add → *Managed identity* → pick
     `slideshow-api-id` (created by the first deployment). Add Microsoft Graph **application**
     permissions `User.ReadWrite.All`, then grant admin consent **in the external tenant** by visiting
     `https://login.microsoftonline.com/<external-tenant-id>/adminconsent?client_id=<graph-app-client-id>`
     as an external-tenant admin. Set `graphClientId`.
   - Fallback: register the Graph app in the external tenant, add the same permission with admin
     consent, create a client secret, and pass it as `graphClientSecret` at deployment (stored as a
     Container Apps secret).

## Deploy to Azure

Needs the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) and an Azure
subscription. Docker is **not** needed; the .NET SDK builds and pushes the image.

1. Fill in [infra/main.bicepparam](../infra/main.bicepparam) with the values from the Entra setup.
2. Deploy:
   ```powershell
   az login
   .\infra\deploy.ps1 -ResourceGroup slideshow-rg -Location westus2
   ```
   This creates the resources (first run), builds and pushes the image, deploys it, and prints the
   API URL. Run it again to ship a new version.

What gets created: Container Apps environment + app (scale 0–3), Azure Container Registry, Storage
account (private `media` container, shared keys off, 7-day soft delete), Azure SQL serverless database
(free offer, auto-pause, Entra-only auth), Log Analytics + Application Insights, and a user-assigned
managed identity with exactly the roles it needs (AcrPull, Storage Blob Data Contributor, SQL admin).

Notes
- The API's managed identity is the SQL server's Entra admin, since it runs the schema migrations on
  start. To query the database yourself, temporarily set yourself as admin:
  `az sql server ad-admin create -g slideshow-rg -s <server> -u <you@domain> -i <your-object-id>`
  (then set it back to the identity).
- The first request after an idle period is slow: the app scales from zero and the database resumes
  from auto-pause. Set `minReplicas: 1` in `main.bicep` to avoid the app cold start (costs more).
- Set `allowedOrigins` to the SPA's origin(s), or browsers will block its requests.

## Configuration reference

| Setting | Meaning |
|---|---|
| `Auth:Authority`, `Auth:Audiences`, `Auth:RequiredScope` | Entra External ID token validation |
| `Database:Provider` (`Sqlite`/`SqlServer`), `Database:ConnectionString` | metadata store |
| `Storage:Provider` (`FileSystem`/`AzureBlob`), `Storage:BlobServiceUri` or `Storage:ConnectionString` | media bytes |
| `Graph:TenantId`, `Graph:ClientId`, `Graph:ClientSecret` | Entra user deletion / session revocation |
| `Limits:*` | per-kind file size, per-user quota, files per album, albums per user, requests per minute |
| `Cors:AllowedOrigins` | SPA origins |

Environment variables use `__` for `:` (e.g. `Database__Provider`).

## Schema changes

```powershell
cd api
dotnet ef migrations add <Name> -p src/Slideshow.Api -o Data/Migrations
```
Migrations target Azure SQL / SQL Server and are applied when the app starts. SQLite (local/tests)
builds its schema straight from the model.
