// The album page's Share panel: turn "anyone with the link can watch" on or off, copy or share the
// link (the device's own share sheet where there is one), show/hide the map, or replace the link.

import { shareLink, SharingApi, type ShareInfo } from '../api/albums-api.js';
import { query } from '../utils/dom.js';
import { errorMessage } from '../utils/format.js';

export function mountSharePanel(panel: HTMLElement, toggle: HTMLButtonElement, albumId: string, albumName: () => string): void {
  const enabled = query<HTMLInputElement>(panel, '.share__enabled');
  const details = query(panel, '.share__details');
  const link = query<HTMLInputElement>(panel, '.share__link');
  const copy = query<HTMLButtonElement>(panel, '.share__copy');
  const native = query<HTMLButtonElement>(panel, '.share__native');
  const map = query<HTMLInputElement>(panel, '.share__map');
  const reset = query<HTMLButtonElement>(panel, '.share__reset');
  const errorEl = query(panel, '.share__error');
  let loaded = false;

  const render = (s: ShareInfo) => {
    enabled.checked = s.enabled;
    map.checked = s.showMap;
    details.hidden = !s.enabled;
    link.value = s.token ? shareLink(s.token) : '';
    toggle.textContent = s.enabled ? 'Shared ✓' : 'Share…';
  };
  /** Runs an API call with the panel's controls disabled, showing any error. */
  const run = async (call: () => Promise<ShareInfo>) => {
    errorEl.hidden = true;
    for (const c of [enabled, map, reset]) c.disabled = true;
    try { render(await call()); }
    catch (err) { errorEl.textContent = errorMessage(err); errorEl.hidden = false; }
    finally { for (const c of [enabled, map, reset]) c.disabled = false; }
  };

  toggle.onclick = async () => {
    const open = panel.hidden;
    panel.hidden = !open;
    toggle.setAttribute('aria-expanded', String(open));
    if (open && !loaded) { loaded = true; await run(() => SharingApi.get(albumId)); }
  };
  enabled.onchange = () => run(() => SharingApi.update(albumId, { enabled: enabled.checked }));
  map.onchange = () => run(() => SharingApi.update(albumId, { showMap: map.checked }));
  reset.onclick = () => {
    if (confirm('Get a new link? The current link will stop working for everyone who has it.')) void run(() => SharingApi.newLink(albumId));
  };

  copy.onclick = async () => {
    try { await navigator.clipboard.writeText(link.value); }
    catch { link.select(); document.execCommand('copy'); } // older browsers
    copy.textContent = 'Copied ✓';
    setTimeout(() => { copy.textContent = 'Copy link'; }, 2000);
  };
  link.onfocus = () => link.select();

  // The device's own share sheet (Messages, Mail, …) where the browser offers one.
  if (typeof navigator.share === 'function') {
    native.hidden = false;
    native.onclick = () => navigator.share({ title: albumName(), text: `Watch "${albumName()}"`, url: link.value })
      .catch(() => undefined); // closed without sharing
  }

  // The button shows whether the album is shared even before the panel is opened.
  void SharingApi.get(albumId).then(s => { toggle.textContent = s.enabled ? 'Shared ✓' : 'Share…'; }).catch(() => undefined);
}
