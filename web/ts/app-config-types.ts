// Shape of the per-environment settings that build.mjs writes to generated/app-config.ts.

export interface AppConfig {
  /** Where the Slideshow API runs, without a trailing slash, e.g. http://localhost:5160 */
  apiBaseUrl: string;
  auth: DevAuthConfig | EntraAuthConfig;
}

/** Local development: sign in with any email; the API (in Development) issues the token. */
export interface DevAuthConfig { mode: 'dev'; }

/** Microsoft Entra External ID. */
export interface EntraAuthConfig {
  mode: 'entra';
  /** The SPA app registration's client id. */
  clientId: string;
  /** e.g. https://<tenant-subdomain>.ciamlogin.com/ */
  authority: string;
  /** The API's delegated scope, e.g. api://<api-client-id>/Slideshow.Access */
  apiScope: string;
}
