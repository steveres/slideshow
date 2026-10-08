// albums.html: the user's albums, and creating a new one.

import { AlbumsApi, type AlbumInfo } from '../api/albums-api.js';
import { requireSignIn } from '../auth/session.js';
import { mountNavigation } from '../components/navigation.js';
import { thumbnail } from '../components/thumbnail.js';
import { query } from '../utils/dom.js';
import { albumSummary, errorMessage, formatDay } from '../utils/format.js';

await requireSignIn();
void mountNavigation();

const page = query(document, '.albums');
const list = query<HTMLUListElement>(page, '.albums__list');
const errorEl = query(page, '.albums__error');
const showError = (err: unknown) => { errorEl.textContent = errorMessage(err); errorEl.hidden = false; };

function card(a: AlbumInfo): HTMLLIElement {
  const li = document.createElement('li');
  const link = document.createElement('a');
  link.className = 'card album-card';
  link.href = `/album.html?id=${encodeURIComponent(a.id)}`;
  link.append(thumbnail(a.coverThumbnailUrl, 'image', 'thumb--cover'));

  const name = document.createElement('h2');
  name.className = 'album-card__name';
  name.textContent = a.name;
  link.append(name);

  if (a.description) {
    const description = document.createElement('p');
    description.className = 'album-card__description';
    description.textContent = a.description;
    link.append(description);
  }

  const summary = document.createElement('p');
  summary.className = 'album-card__summary';
  summary.textContent = albumSummary(a);
  link.append(summary);

  const meta = document.createElement('p');
  meta.className = 'album-card__meta';
  meta.textContent = `Updated ${formatDay(a.updatedAt)}`;
  if (a.isShared) {
    const shared = document.createElement('span');
    shared.className = 'badge badge--ok';
    shared.textContent = 'Shared';
    meta.append(' ', shared);
  }
  if (a.excludedCount > 0) {
    const badge = document.createElement('span');
    badge.className = 'badge badge--warning';
    badge.textContent = `${a.excludedCount} not in slideshow`;
    meta.append(' ', badge);
  }
  link.append(meta);

  li.append(link);
  return li;
}

async function load(): Promise<void> {
  try {
    const albums = await AlbumsApi.list();
    list.replaceChildren(...albums.map(card));
    query(page, '.albums__empty').hidden = albums.length > 0;
  } catch (err) {
    showError(err);
  } finally {
    query(page, '.albums__loading').hidden = true;
  }
}

const form = query<HTMLFormElement>(page, '.albums__new');
form.onsubmit = async e => {
  e.preventDefault();
  errorEl.hidden = true;
  const button = query<HTMLButtonElement>(form, 'button');
  button.disabled = true;
  try {
    const name = query<HTMLInputElement>(form, '[name=name]').value.trim();
    const description = query<HTMLInputElement>(form, '[name=description]').value.trim();
    const album = await AlbumsApi.create(name, description || undefined);
    location.assign(`/album.html?id=${encodeURIComponent(album.id)}`); // straight to adding files
  } catch (err) {
    showError(err);
    button.disabled = false;
  }
};

await load();
