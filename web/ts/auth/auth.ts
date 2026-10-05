// Sign-in, independent of how it is done. The provider is chosen by APP_CONFIG.auth.mode:
//   "dev"   — local development: sign in with an email; the API issues the token (dev-auth.ts)
//   "entra" — Microsoft Entra External ID via MSAL (entra-auth.ts)

import { APP_CONFIG } from '../config.js';

export interface Account {
  name: string;
  email?: string;
}

export interface AuthProvider {
  readonly mode: 'dev' | 'entra';
  /** The signed-in user, or null. */
  account(): Account | null;
  /** Sends the user to sign in; afterwards they come back to `returnTo` (a path on this site). */
  signIn(returnTo?: string): Promise<void>;
  /** Forgets the sign-in on this browser (and, for Entra, at the identity provider). */
  signOut(): Promise<void>;
  /** A valid access token for the API; throws NotSignedInError when there is none. */
  getAccessToken(): Promise<string>;
}

export class NotSignedInError extends Error {
  constructor() { super('Not signed in.'); this.name = 'NotSignedInError'; }
}

let provider: Promise<AuthProvider> | undefined;

/** The sign-in provider for this page, ready to use (any sign-in redirect in progress is completed). */
export function getAuth(): Promise<AuthProvider> {
  provider ??= APP_CONFIG.auth.mode === 'entra'
    ? import('./entra-auth.js').then(m => m.createEntraAuth())
    : import('./dev-auth.js').then(m => m.createDevAuth());
  return provider;
}

/** The current page, for coming back to it after signing in. */
export const currentPath = () => location.pathname + location.search;

/** Only same-site paths are allowed as a return target (no open redirects). */
export function safeReturnPath(value: string | null | undefined, fallback = '/'): string {
  return value && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\') ? value : fallback;
}

/** The login page, with where to go afterwards. */
export const loginUrl = (returnTo = currentPath()) => `/login.html?returnTo=${encodeURIComponent(returnTo)}`;
