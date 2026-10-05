// account.html: who you are, how much you store, sign out, delete account.

import { AccountApi, type AccountInfo } from '../api/account-api.js';
import { requireSignIn, signOut } from '../auth/session.js';
import { getAuth } from '../auth/auth.js';
import { mountNavigation } from '../components/navigation.js';
import { query } from '../utils/dom.js';
import { errorMessage, formatBytes, formatDay } from '../utils/format.js';

await requireSignIn();
void mountNavigation();

const page = query(document, '.account');
const errorEl = query(page, '.account__error');
const showError = (err: unknown) => { errorEl.textContent = errorMessage(err); errorEl.hidden = false; };

function render(a: AccountInfo): void {
  query(page, '.account__name').textContent = a.displayName ?? '(no name)';
  query(page, '.account__email').textContent = a.email ?? '';
  query(page, '.account__since').textContent = formatDay(a.createdAt);
  const { albums, files, bytes, quotaBytes } = a.usage;
  query(page, '.account__albums').textContent = String(albums);
  query(page, '.account__files').textContent = String(files);
  query(page, '.account__storage').textContent = `${formatBytes(bytes)} of ${formatBytes(quotaBytes)}`;
  const meter = query<HTMLMeterElement>(page, '.account__meter');
  meter.max = quotaBytes;
  meter.value = bytes;
  query(page, '.account__details').hidden = false;
}

try {
  // Signed in but not registered with the API yet (e.g. came straight here): registering is harmless.
  render(await AccountApi.get() ?? await AccountApi.register());
} catch (err) {
  showError(err);
} finally {
  query(page, '.account__loading').hidden = true;
}

const signOutBtn = query<HTMLButtonElement>(page, '.account__sign-out');
signOutBtn.onclick = async () => { signOutBtn.disabled = true; await signOut(); };

// Delete: a second, explicit confirmation step.
const confirmBox = query(page, '.account__confirm');
query<HTMLButtonElement>(page, '.account__delete').onclick = () => { confirmBox.hidden = false; };
query<HTMLButtonElement>(page, '.account__cancel').onclick = () => { confirmBox.hidden = true; };
const confirmBtn = query<HTMLButtonElement>(page, '.account__confirm-delete');
confirmBtn.onclick = async () => {
  confirmBtn.disabled = true;
  errorEl.hidden = true;
  try {
    await AccountApi.remove();
    await (await getAuth()).signOut();
    location.assign('/?deleted=1');
  } catch (err) {
    showError(err);
    confirmBtn.disabled = false;
  }
};
