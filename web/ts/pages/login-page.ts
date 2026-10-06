// login.html: sign in (Entra, or the development form), register with the API, then go back.

import { AccountApi } from '../api/account-api.js';
import { getAuth, safeReturnPath } from '../auth/auth.js';
import type { DevAuthProvider } from '../auth/dev-auth.js';
import { mountNavigation } from '../components/navigation.js';
import { query } from '../utils/dom.js';
import { errorMessage } from '../utils/format.js';

const returnTo = safeReturnPath(new URLSearchParams(location.search).get('returnTo'), '/albums.html');
const card = query(document, '.login');
const errorEl = query(card, '.login__error');
const showError = (err: unknown) => { errorEl.textContent = errorMessage(err); errorEl.hidden = false; };

/** Signed in: make sure the API account exists (first sign-in = registration), then continue. */
async function finish(): Promise<void> {
  query(card, '.login__busy').hidden = false;
  for (const form of card.querySelectorAll<HTMLElement>('.login__form')) form.hidden = true;
  try {
    await AccountApi.register();
    location.replace(returnTo);
  } catch (err) {
    query(card, '.login__busy').hidden = true;
    showError(err);
  }
}

const auth = await getAuth();
void mountNavigation();

if (auth.account()) {
  await finish(); // already signed in, or just back from Entra
} else if (auth.mode === 'dev') {
  const form = query<HTMLFormElement>(card, '.login__form--dev');
  form.hidden = false;
  const email = query<HTMLInputElement>(form, '[name=email]');
  email.focus();
  form.onsubmit = async e => {
    e.preventDefault();
    errorEl.hidden = true;
    const button = query<HTMLButtonElement>(form, 'button');
    button.disabled = true;
    try {
      await (auth as DevAuthProvider).signInAs(email.value.trim(), query<HTMLInputElement>(form, '[name=name]').value.trim());
      await finish();
    } catch (err) {
      showError(err);
    } finally {
      button.disabled = false;
    }
  };
} else {
  const form = query(card, '.login__form--entra');
  form.hidden = false;
  query<HTMLButtonElement>(form, 'button').onclick = () => auth.signIn(returnTo).catch(showError);
}
