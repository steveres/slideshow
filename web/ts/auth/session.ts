// Sign-in and sign-out flows shared by the pages.

import { AccountApi } from '../api/account-api.js';
import { getAuth, loginUrl } from './auth.js';

/**
 * For pages that need a signed-in user: resolves when there is one, otherwise sends them to the
 * login page (which brings them back here) and never resolves.
 */
export async function requireSignIn(): Promise<void> {
  const auth = await getAuth();
  if (auth.account()) return;
  location.assign(loginUrl());
  await new Promise(() => {}); // the page is navigating away
}

/** Signs out everywhere: the API stops accepting this user's current tokens, then this browser forgets them. */
export async function signOut(): Promise<void> {
  const auth = await getAuth();
  try { await AccountApi.logout(); }
  catch (err) { console.warn('API logout failed; signing out of this browser anyway.', err); }
  await auth.signOut();
  if (auth.mode === 'dev') location.assign('/'); // Entra's sign-out redirects by itself
}
