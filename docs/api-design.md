# Slideshow API — Architecture & Design

Status: v1 (implemented in `api/`). The SPA will be moved onto this API in a later step.

## 1. Goals

- Each user can store albums of photos/videos plus background music, and play them back in the
  existing slideshow SPA from any browser.
- A user can only ever see or change their own data.
- "Compile" an album: produce the date-ordered playlist (with GPS points for the route map) that
  `prepare.ps1` + `slideshow.json` produce today, so the SPA needs no local preparation step.
- Cloud-native on Azure, but every Azure dependency sits behind an interface so the API runs and is
  fully tested on a laptop with no cloud resources.

Out of scope for v1: HEIC conversion, video thumbnails, video rendering, sharing albums between users.
Photo previews are made with SkiaSharp (MIT) and stored next to the photo as `{blob}.thumb`.

## 2. Architecture

```
 Browser (SPA)                         Microsoft Entra External ID (customer tenant)
   │  MSAL.js: sign-up / sign-in / sign-out  ──────────────►  user flows, issues JWT access tokens
   │
   │  HTTPS + Authorization: Bearer <access token>
   ▼
 Azure Container Apps ── Slideshow.Api (ASP.NET Core 9, stateless, scale 0..N)
   │   system-assigned managed identity (no secrets in config)
   ├──► Azure SQL Database (serverless)   users, albums, media metadata, compiled manifests
   ├──► Azure Blob Storage (private)      the media bytes: media/{userId}/{albumId}/{mediaId}
   ├──► Microsoft Graph (customer tenant)  delete user / revoke sessions on account delete & logout
   └──► Application Insights               logs, traces, metrics (OpenTelemetry)
```

| Concern | Azure service | Local / test substitute |
|---|---|---|
| Compute | Container Apps (consumption) | `dotnet run` / in-process test host |
| Identity | Entra External ID | test-issued JWTs (same validation pipeline) |
| Metadata DB | Azure SQL Database, serverless | SQLite file |
| Media bytes | Blob Storage (private container) | folder on disk |
| Directory ops | Microsoft Graph | no-op / recording fake |
| Telemetry | Application Insights | console logs |
| Image registry | Azure Container Registry | — |

**Why these choices**
- *Container Apps*: HTTP-scaled containers with scale-to-zero, managed TLS ingress and managed identity;
  no cluster to run. The image is built with `dotnet publish /t:PublishContainer` (no Dockerfile/Docker needed).
- *Azure SQL serverless*: relational data with ownership joins, auto-pause, free-offer eligible, and
  passwordless Entra authentication from the managed identity.
- *Blob Storage*: cheap durable bytes. Shared-key access is disabled; the API reaches it with its
  managed identity (`Storage Blob Data Contributor`). The container has no public access.
- *Entra External ID*: hosted, branded sign-up/sign-in (password, email OTP, social), MFA, password
  reset and token issuance — none of which we want to build ourselves.

### Data flow: upload
1. SPA `POST /api/v1/albums/{albumId}/images` (multipart) with the bearer token.
2. API validates the token, confirms the album belongs to the caller, checks size and quota.
3. The file's **content** is sniffed (magic bytes) to decide its type — the client's filename and
   `Content-Type` are not trusted. Unsupported types are rejected (415).
4. Date-taken and GPS are read from the file (EXIF for images; QuickTime metadata for videos).
5. Bytes go to Blob Storage under a server-generated name; then the metadata row is committed.
   If the DB write fails the blob is deleted.

### Data flow: play
1. `POST /albums/{id}/compile` takes the photos and videos that have **both** a capture date (EXIF for
   photos, the file's own creation date for videos) **and** a location, sorts them by date (then natural
   filename order) and stores the result as the album's manifest. Files missing either are left out;
   listings flag them (`playable: false`, `missing: [...]`) so the UI can say why.
2. SPA `GET /albums/{id}` → manifest (ordered slides with dates + GPS, music list).
3. SPA fetches each item's bytes with `GET .../images/{id}` (bearer token, `Range` supported) and
   shows it via an object URL — the same way it handles local `File`s today.

## 3. Authentication & account lifecycle

Tokens: Entra External ID v2 access tokens for the API's app registration. The API validates
signature (keys from the tenant's OIDC metadata), issuer, audience, lifetime, and requires the
delegated scope `Slideshow.Access` in `scp` (or the standard `scope` claim). The user's identity is the `oid` claim.

| Operation | How |
|---|---|
| **Register** | User signs up through the Entra sign-up flow (in the SPA via MSAL), then calls `POST /account` once to create their API account. All album/media endpoints return `403 account_not_registered` until then. Explicit registration means a still-valid token for a deleted account cannot silently re-create it. |
| **Login** | Done by MSAL against Entra (`loginRedirect`/`acquireTokenSilent`). There is deliberately no password endpoint in the API: passwords never touch our servers. |
| **Logout** | SPA calls `POST /account/logout`, then MSAL `logoutRedirect`. The API records `TokensValidAfter = now`, so any access token issued earlier is rejected (normally JWTs stay valid until they expire). It also asks Graph to revoke the user's refresh tokens. This signs the user out on **all** devices. |
| **Delete account** | `DELETE /account` deletes all albums, media rows and blobs, the user row, and the Entra user (via Graph). Irreversible. |

Graph calls use an app registration in the customer tenant (`User.ReadWrite.All` application
permission). In Azure the API authenticates as that app with a federated credential backed by its
managed identity, so no client secret exists. If Graph is not configured, data deletion and token
cut-off still happen; only the Entra-side step is skipped (logged as a warning).

## 4. Authorization & isolation

- Every table row carries `OwnerId`. Every query filters on the caller's `oid` **and** the parent
  album id, so an album/media id from another user is indistinguishable from a missing one: `404`.
- Blob names are generated by the server (`{ownerId}/{albumId}/{mediaId}`); no client input forms a path.
- The blob container is private and only the API's identity can read it.

## 5. REST API

Base path `/api/v1`. JSON uses camelCase. Errors are RFC 7807 `application/problem+json` with a
`code` extension (e.g. `album_not_found`). All endpoints except `/healthz` need a bearer token.

### Account
| Method & path | Result |
|---|---|
| `POST /account` | Register. `201` + `Account` (first time) or `200` (already registered). |
| `GET /account` | `Account` (profile, usage, quota). |
| `POST /account/logout` | `204`. Invalidates all of the user's current tokens. |
| `DELETE /account` | `204`. Deletes everything. |

### Albums
| Method & path | Result |
|---|---|
| `GET /albums` | `AlbumInfo[]`, newest first. |
| `POST /albums` `{name, description?}` | `201` + `AlbumInfo`. |
| `GET /albums/{albumId}` | `AlbumManifest` — the compiled, playable album. `409 album_not_compiled` if never compiled. |
| `GET /albums/{albumId}/info` | `AlbumInfo`. |
| `POST /albums/{albumId}/compile` | `200` + `AlbumManifest`. |
| `DELETE /albums/{albumId}` | `204`. Deletes its images and music too. |

### Images (photos **and** videos — the slides) and Music
`{collection}` is `images` or `music`.

| Method & path | Result |
|---|---|
| `GET /albums/{albumId}/{collection}` | `MediaInfo[]` in upload order. |
| `POST /albums/{albumId}/{collection}` | multipart: `file` (required), `utcOffsetMinutes` (optional: the uploader's UTC offset, to turn a video's UTC creation time into local time like EXIF). `201` + `MediaInfo`. |
| `GET /albums/{albumId}/{collection}/{id}` | The bytes, with the sniffed `Content-Type`, `Range`/`ETag` support, `Cache-Control: private, immutable`. |
| `GET /albums/{albumId}/{collection}/{id}/info` | `MediaInfo`. |
| `GET /albums/{albumId}/images/{id}/thumbnail` | A JPEG preview, at most 400px on its longest edge, upright per EXIF orientation. Made at upload; photos uploaded before previews existed get one on first request. `404 no_thumbnail` for videos and photos that can't be decoded (e.g. AVIF). |
| `DELETE /albums/{albumId}/{collection}/{id}` | `204`. |

Accepted types (by content, not name): images JPEG, PNG, GIF, WebP, AVIF, BMP; videos MP4/M4V/MOV,
WebM, Ogg; music MP3. HEIC/HEIF and SVG are rejected (HEIC isn't displayable in most browsers; SVG
can carry script).

### Shapes
```jsonc
// Account
{ "id": "oid", "displayName": "…", "email": "…", "createdAt": "…Z",
  "usage": { "albums": 3, "files": 120, "bytes": 123456789, "quotaBytes": 10737418240 } }

// AlbumInfo
{ "id": "guid", "name": "Italy 2026", "description": null, "createdAt": "…Z", "updatedAt": "…Z",
  "imageCount": 80, "videoCount": 4, "musicCount": 3, "excludedCount": 2, "totalBytes": 456789,
  "compiledAt": "…Z" | null, "isStale": false,      // isStale: never compiled, or changed since
  "coverThumbnailUrl": "…/thumbnail" | null }         // first photo in slideshow order, else first uploaded

// MediaInfo
{ "id": "guid", "fileName": "IMG_0001.jpg", "kind": "image" | "video" | "audio",
  "contentType": "image/jpeg", "sizeBytes": 2345678, "uploadedAt": "…Z",
  "taken": "2026-07-04T09:31:05" | null,           // local wall-clock time, no zone (as EXIF)
  "takenSource": "exif" | "video" | null,         // only real capture dates; file times are never used
  "location": { "lat": 41.9, "lon": 12.5 } | null,
  "thumbnailUrl": "/api/v1/albums/…/images/…/thumbnail" | null,   // photos only
  "playable": true,                                // shown in the slideshow (music: always true)
  "missing": [] }                                  // otherwise why not: "date" and/or "location"

// AlbumManifest
{ "albumId": "guid", "name": "…", "compiledAt": "…Z", "isStale": false,
  "slides": [ MediaInfo + { "url": "/api/v1/albums/…/images/…" } ],   // playable only, in play order
  "music":  [ MediaInfo + { "url": "…" } ],
  "excludedCount": 2 }                                                  // photos/videos left out
```

## 6. Data model (Azure SQL)

```
Users      Id (oid, PK) · DisplayName · Email · CreatedAt · TokensValidAfter?
Albums     Id (guid v7, PK) · OwnerId → Users · Name · Description · CreatedAt · UpdatedAt
           ContentVersion · CompiledVersion? · CompiledAt? · ManifestJson?
MediaFiles Id (guid v7, PK) · AlbumId → Albums (cascade) · OwnerId · Collection (Images|Music)
           Kind · FileName · ContentType · SizeBytes · BlobName · UploadedAt
           TakenAt? · TakenSource? · Latitude? · Longitude? · ThumbnailState (None|Ready|Unavailable)
           index (OwnerId, AlbumId, Collection)
```
`ContentVersion` is bumped atomically on every add/delete; an album is stale when it differs from
`CompiledVersion`. Schema changes ship as EF Core migrations, applied at startup (EF takes a lock,
so several replicas starting together is safe).

## 7. Security controls

- Bearer-token only (no cookies) → not CSRF-prone; CORS restricted to configured SPA origins.
- Token checks: signature, issuer, audience, expiry, required scope, plus the logout cut-off.
- Content-sniffed uploads, allow-listed types, `X-Content-Type-Options: nosniff`, attachment-safe
  `Content-Disposition` with the sanitized original filename.
- Limits: per-file size by kind, per-user storage quota (default 10 GB), files per album, and a
  per-user rate limit (`429`).
- No secrets: managed identity for SQL, Blob, ACR and (via federation) Graph. TLS-only ingress.
- No raw SQL; EF Core parameterizes everything. File names and personal data are not logged.

## 8. Operations

- `GET /healthz` (anonymous) for Container Apps probes.
- OpenTelemetry → Application Insights when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set.
- OpenAPI document at `/openapi/v1.json` and an interactive API explorer at `/scalar` (Development only).
- Infrastructure as code: `infra/main.bicep`. Setup steps: `api/README.md`.

## 9. Known limits / later

- Uploads and downloads stream through the API. If large videos become a bottleneck, add
  short-lived user-delegation SAS URLs for direct browser ↔ Blob transfer.
- Logout is "sign out everywhere"; per-device sign-out would need session ids in tokens.
- Blobs orphaned by a crash between blob delete and DB delete are harmless but could be swept by a job.
