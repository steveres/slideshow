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

Pages: `index.html` (home), `login.html` (sign in; first sign-in registers the account with the API),
`account.html` (usage, sign out everywhere, delete account), `slideshow.html` (play a local folder).

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
    api/                        api-client (token, errors, 401 → sign in), account-api
    auth/                       auth (provider interface), dev-auth, entra-auth (MSAL), session (sign in/out flows)
    media/                      media types, GPS readers, local-folder source
    utils/                      DOM helpers, settings, formatting
    config.ts                   per-environment settings (generated) + tunable constants
  build.mjs                     tsc + Maps key → ts/generated/
  server.js                     local dev server (Express)
```

`mountSlideshow(element)` plays any `SlideshowContents` (slides + music) it is given; it does not know
where they come from. Today the folder picker supplies them; next, the albums API will.
