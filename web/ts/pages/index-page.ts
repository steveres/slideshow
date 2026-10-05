// index.html: home.

import { getAuth, loginUrl } from '../auth/auth.js';
import { mountNavigation } from '../components/navigation.js';
import { query } from '../utils/dom.js';

void mountNavigation();
const auth = await getAuth();
const account = auth.account();

const hello = query(document, '.home__hello');
hello.textContent = account ? `Welcome back, ${account.name}.` : 'Sign in to keep your albums online and play them anywhere.';
const primary = query<HTMLAnchorElement>(document, '.home__primary');
if (account) { primary.textContent = 'Your account'; primary.href = '/account.html'; }
else { primary.textContent = 'Sign in or create an account'; primary.href = loginUrl('/'); }

if (new URLSearchParams(location.search).has('deleted')) query(document, '.home__deleted').hidden = false;
