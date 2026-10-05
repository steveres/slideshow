// Site header: loads components/navigation.html into #navigation-placeholder and shows who is signed in.

import { getAuth, loginUrl } from '../auth/auth.js';
import { signOut } from '../auth/session.js';
import { query } from '../utils/dom.js';

export async function mountNavigation(): Promise<void> {
  const placeholder = document.getElementById('navigation-placeholder');
  if (!placeholder) return;
  placeholder.innerHTML = await (await fetch('/components/navigation.html')).text();

  // Mark the link for the current page.
  for (const link of placeholder.querySelectorAll<HTMLAnchorElement>('.nav__link')) {
    if (link.pathname === location.pathname) link.setAttribute('aria-current', 'page');
  }

  const auth = await getAuth();
  const account = auth.account();
  const signIn = query<HTMLAnchorElement>(placeholder, '.nav__sign-in');
  const accountLink = query<HTMLAnchorElement>(placeholder, '.nav__account');
  const signOutBtn = query<HTMLButtonElement>(placeholder, '.nav__sign-out');

  if (account) {
    accountLink.textContent = account.name;
    accountLink.hidden = false;
    signOutBtn.hidden = false;
    signOutBtn.onclick = async () => { signOutBtn.disabled = true; await signOut(); };
  } else if (location.pathname !== '/login.html') {
    signIn.href = loginUrl();
    signIn.hidden = false;
  }
}
