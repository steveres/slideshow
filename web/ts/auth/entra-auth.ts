// Microsoft Entra External ID sign-in with MSAL (redirect flow).
// MSAL is loaded as a classic script from /vendor (copied there by build.mjs); only its types are imported.

import type { AccountInfo, Configuration, IPublicClientApplication } from '@azure/msal-browser';
import { APP_CONFIG } from '../config.js';
import type { EntraAuthConfig } from '../app-config-types.js';
import { loginUrl, NotSignedInError, type AuthProvider } from './auth.js';

type Msal = typeof import('@azure/msal-browser');

/** Loads /vendor/msal-browser.min.js once; it defines window.msal. */
function loadMsal(): Promise<Msal> {
  const w = window as unknown as { msal?: Msal };
  if (w.msal) return Promise.resolve(w.msal);
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = '/vendor/msal-browser.min.js';
    script.onload = () => (w.msal ? resolve(w.msal) : reject(new Error('MSAL did not load.')));
    script.onerror = () => reject(new Error('Could not load the sign-in library.'));
    document.head.append(script);
  });
}

export async function createEntraAuth(): Promise<AuthProvider> {
  const config = APP_CONFIG.auth as EntraAuthConfig;
  const msal = await loadMsal();
  const msalConfig: Configuration = {
    auth: {
      clientId: config.clientId,
      authority: config.authority,
      redirectUri: `${location.origin}/login.html`,       // must be registered on the SPA app registration
      postLogoutRedirectUri: `${location.origin}/`,
    },
    cache: { cacheLocation: 'localStorage' },               // stay signed in across tabs
  };
  const app: IPublicClientApplication = await msal.createStandardPublicClientApplication(msalConfig);

  // Completes a sign-in redirect if this page load is the return from Entra.
  const result = await app.handleRedirectPromise();
  if (result?.account) app.setActiveAccount(result.account);
  const current = (): AccountInfo | null => app.getActiveAccount() ?? app.getAllAccounts()[0] ?? null;
  const scopes = [config.apiScope];

  return {
    mode: 'entra',

    account() {
      const a = current();
      return a ? { name: a.name ?? a.username, email: a.username } : null;
    },

    async signIn(returnTo) {
      if (location.pathname !== '/login.html') { location.assign(loginUrl(returnTo)); return; }
      // After Entra, MSAL lands on redirectUri and then returns to this exact URL
      // (login.html?returnTo=...), so the login page still knows where to go next.
      await app.loginRedirect({ scopes });
    },

    async signOut() {
      const account = current();
      await app.logoutRedirect(account ? { account } : undefined);
    },

    async getAccessToken() {
      const account = current();
      if (!account) throw new NotSignedInError();
      try {
        return (await app.acquireTokenSilent({ scopes, account })).accessToken;
      } catch (err) {
        if (err instanceof msal.InteractionRequiredAuthError) {
          await app.acquireTokenRedirect({ scopes, account }); // comes back to this page
        }
        throw err;
      }
    },
  };
}
