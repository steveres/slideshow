# Slideshow

Plays albums of photos and videos, draws the route between them on a Google map, and plays the
album's MP3s as background music.

| Folder | What |
|---|---|
| [web/](web/) | The website (TypeScript + CSS, no framework, no bundler) |
| [api/](api/) | The API that stores each user's albums ([api/README.md](api/README.md)) |
| [infra/](infra/) | Azure infrastructure (Bicep) and deploy script |
| [docs/](docs/) | Design documents ([API design](docs/api-design.md)) |
| `prepare.bat`, `prepare.ps1` | Optional, Windows: prepare a local photo folder (HEIC → JPEG, date order) |

## Website

Requires [Node.js](https://nodejs.org).

```powershell
cd web
npm install          # once
npm run dev          # build, rebuild on save, serve on http://localhost:3000
```

Other scripts: `npm run build` (build once), `npm run watch` (rebuild on save), `npm start` (build once
and serve).

**Google Maps key:** put it in `web/apikey.txt` (git-ignored), or set `GOOGLE_MAPS_API_KEY`. The key
needs the **Maps JavaScript API** enabled. It is visible to anyone who loads the page (that's how
Maps keys work), so in the Google Cloud console restrict it to your site's addresses, e.g.
`http://localhost:3000/*` and the production URL.

**Sign-in:** chosen in `web/app.config.json` (committed defaults; put personal overrides in the
git-ignored `app.config.local.json`, or pass `APP_CONFIG` as JSON in CI):

- `"auth": { "mode": "dev" }` (default) — local development without Entra. Run the API in Development
  (`dotnet run` in `api/src/Slideshow.Api`), open http://localhost:3000/login.html and sign in with any
  email; each email is a separate user. The API issues the token from `POST /dev/token`, which only
  exists in Development and uses the `dotnet user-jwts` signing key (run `dotnet user-jwts create`
  there once if you haven't).
- `"auth": { "mode": "entra", "clientId": "<spa-client-id>", "authority": "https://<tenant>.ciamlogin.com/", "apiScope": "api://<api-client-id>/Slideshow.Access" }`
  — Microsoft Entra External ID (see [api/README.md](api/README.md) for the tenant setup). Register
  `<site>/login.html` as the SPA redirect URI.

Pages:

| Page | What |
|---|---|
| `index.html` | Home |
| `login.html` | Sign in; the first sign-in registers the account with the API |
| `albums.html` | Your albums; create one |
| `album.html?id=…` | Upload photos, videos and MP3s (drag and drop), see which files the slideshow will show, play, delete |
| `slideshow.html?album=…` | Play an album (compiles it first if it changed) |
| `slideshow.html` | Play a folder from this computer (no sign-in needed) |
| `account.html` | Usage, sign out everywhere, delete account |

**What the slideshow shows:** photos and videos that have both a real capture date (EXIF for photos,
the video's own creation date) and a location (GPS), in date order. Anything missing either is kept in
the album but left out of the slideshow, and the album page marks it with the reason.

**Playing a local folder:** open http://localhost:3000 in Chrome or Edge and choose the folder. The
folder and settings are remembered. Optionally run `prepare.bat` on the folder first (drag the folder
onto it): it converts HEIC files to JPEG and writes `slideshow.json`, which puts the slideshow in
date-taken order and supplies the dates on the timeline.

### Layout

Modelled on the frontpack website: one HTML file per page in `src/`, styles split into base and
component sheets, code split into pages, components, data sources and utilities.

```
web/
  src/                          the site as served (deployed as-is)
    slideshow.html              page markup
    css/base/                   reset, variables
    css/components/             one stylesheet per component
    js/                         compiled from ts/ (git-ignored)
  ts/                           TypeScript source → src/js/, loaded as native ES modules
    pages/                      one entry module per HTML page
    components/                 navigation; slideshow/ (mount, player, map, timeline, zoom, music, folder picker)
    api/                        api-client (token, errors, 401 → sign in, upload progress), account-api, albums-api
    auth/                       auth (provider interface), dev-auth, entra-auth (MSAL), session (sign in/out flows)
    media/                      media types, GPS readers, local-folder source, API album source
    utils/                      DOM helpers, settings, formatting
    config.ts                   per-environment settings (generated) + tunable constants
  build.mjs                     tsc + Maps key → ts/generated/
  server.js                     local dev server (Express)
```

`mountSlideshow(element)` plays any `SlideshowContents` (slides + music) it is given; it does not know
where they come from: the folder picker (`media/folder-source.ts`) or an album on the API
(`media/api-source.ts`, which downloads each file when needed and prefetches the next).
