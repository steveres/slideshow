// album.html?id=…: one album — upload files, see which will be shown, play, delete.

import { AlbumsApi, collectionFor, MediaApi, type AlbumInfo, type Collection, type MediaInfo } from '../api/albums-api.js';
import { requireSignIn } from '../auth/session.js';
import { mountNavigation } from '../components/navigation.js';
import { thumbnail } from '../components/thumbnail.js';
import { query } from '../utils/dom.js';
import { albumSummary, errorMessage, formatBytes, formatTaken } from '../utils/format.js';

const UPLOAD_CONCURRENCY = 3;

await requireSignIn();
void mountNavigation();

const albumId = new URLSearchParams(location.search).get('id') ?? '';
const page = query(document, '.album');
const errorEl = query(page, '.album__error');
const showError = (err: unknown) => { errorEl.textContent = errorMessage(err); errorEl.hidden = false; };

// ───────── Rendering ─────────

/** A <td> with text (never HTML: file names come from users). */
function cell(text: string, className?: string): HTMLTableCellElement {
  const td = document.createElement('td');
  td.textContent = text;
  if (className) td.className = className;
  return td;
}

function deleteCell(m: MediaInfo, collection: Collection): HTMLTableCellElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'button button--quiet media-table__delete';
  button.textContent = 'Delete';
  button.setAttribute('aria-label', `Delete ${m.fileName}`);
  button.onclick = async () => {
    if (!confirm(`Delete "${m.fileName}" from this album?`)) return;
    button.disabled = true;
    try { await MediaApi.remove(albumId, collection, m.id); await load(); }
    catch (err) { showError(err); button.disabled = false; }
  };
  const td = document.createElement('td');
  td.append(button);
  return td;
}

const missingText = (missing: MediaInfo['missing']) =>
  missing.length === 2 ? 'No date or location' : missing[0] === 'date' ? 'No capture date' : 'No location';

function imageRow(m: MediaInfo): HTMLTableRowElement {
  const tr = document.createElement('tr');
  if (!m.playable) tr.className = 'media-table__row--excluded';
  const name = cell(`${m.fileName}${m.kind === 'video' ? ' (video)' : ''}`, 'media-table__name');
  const status = document.createElement('td');
  const badge = document.createElement('span');
  badge.className = m.playable ? 'badge badge--ok' : 'badge badge--warning';
  badge.textContent = m.playable ? 'Shown' : `Not shown: ${missingText(m.missing)}`;
  status.append(badge);
  const preview = document.createElement('td');
  preview.className = 'media-table__preview';
  preview.append(thumbnail(m.thumbnailUrl, m.kind, 'thumb--row'));
  tr.append(
    preview,
    name,
    cell(m.taken ? formatTaken(m.taken) : '—', m.taken ? '' : 'media-table__missing'),
    cell(m.location ? `${m.location.lat.toFixed(4)}, ${m.location.lon.toFixed(4)}` : '—', m.location ? '' : 'media-table__missing'),
    status,
    cell(formatBytes(m.sizeBytes), 'media-table__size'),
    deleteCell(m, 'images'));
  return tr;
}

function musicRow(m: MediaInfo): HTMLTableRowElement {
  const tr = document.createElement('tr');
  tr.append(cell(m.fileName, 'media-table__name'), cell(formatBytes(m.sizeBytes), 'media-table__size'), deleteCell(m, 'music'));
  return tr;
}

/** Slideshow order first (by capture time), then the ones that won't be shown. */
function slideshowOrder(a: MediaInfo, b: MediaInfo): number {
  if (a.playable !== b.playable) return a.playable ? -1 : 1;
  return (a.taken ?? '').localeCompare(b.taken ?? '') || a.fileName.localeCompare(b.fileName, undefined, { numeric: true });
}

function renderAlbum(a: AlbumInfo): void {
  document.title = `${a.name} - Slideshow`;
  query(page, '.album__name').textContent = a.name;
  const description = query(page, '.album__description');
  description.textContent = a.description ?? '';
  description.hidden = !a.description;
  query(page, '.album__summary').textContent = albumSummary(a);

  const shown = a.imageCount + a.videoCount - a.excludedCount;
  const play = query<HTMLAnchorElement>(page, '.album__play');
  if (shown > 0) {
    play.href = `/slideshow.html?album=${encodeURIComponent(a.id)}`;
    play.removeAttribute('aria-disabled');
    play.title = '';
  } else {
    play.removeAttribute('href');
    play.setAttribute('aria-disabled', 'true');
    play.title = 'Add photos or videos that have a capture date and a location first.';
  }

  const excluded = query(page, '.album__excluded');
  excluded.hidden = a.excludedCount === 0;
  const n = a.excludedCount;
  excluded.textContent = n === 1
    ? "1 photo or video won't be shown in the slideshow because it is missing a capture date or a location (GPS). It's marked below."
    : `${n} photos or videos won't be shown in the slideshow because they are missing a capture date or a location (GPS). They're marked below.`;
}

function renderList(collection: Collection, media: MediaInfo[]): void {
  const images = collection === 'images';
  const body = query(page, images ? '.album__images' : '.album__music');
  body.replaceChildren(...(images ? [...media].sort(slideshowOrder).map(imageRow) : media.map(musicRow)));
  query(page, images ? '.album__images-table' : '.album__music-table').hidden = media.length === 0;
  query(page, images ? '.album__none--images' : '.album__none--music').hidden = media.length > 0;
}

async function load(): Promise<void> {
  try {
    const [info, images, music] = await Promise.all([
      AlbumsApi.info(albumId), MediaApi.list(albumId, 'images'), MediaApi.list(albumId, 'music'),
    ]);
    renderAlbum(info);
    renderList('images', images);
    renderList('music', music);
    query(page, '.album__body').hidden = false;
  } catch (err) {
    showError(err);
  } finally {
    query(page, '.album__loading').hidden = true;
  }
}

// ───────── Upload ─────────

const queueEl = query<HTMLUListElement>(page, '.upload__queue');

/** One line in the upload list, with progress and the outcome. */
function queueItem(file: File) {
  const li = document.createElement('li');
  li.className = 'upload__item';
  const name = document.createElement('span');
  name.className = 'upload__name';
  name.textContent = file.name;
  const progress = document.createElement('progress');
  progress.max = 1;
  progress.value = 0;
  const status = document.createElement('span');
  status.className = 'upload__status';
  status.textContent = 'Waiting…';
  li.append(name, progress, status);
  queueEl.prepend(li);
  return {
    progress: (fraction: number) => { progress.value = fraction; status.textContent = `${Math.round(fraction * 100)}%`; },
    done(m: MediaInfo) {
      progress.remove();
      li.classList.add(m.playable ? 'upload__item--ok' : 'upload__item--warning');
      status.textContent = m.playable ? 'Added' : `Added, but not shown: ${missingText(m.missing).toLowerCase()}`;
      if (m.playable) setTimeout(() => li.remove(), 4000); // keep the ones that need attention
    },
    failed(err: unknown) {
      progress.remove();
      li.classList.add('upload__item--error');
      status.textContent = errorMessage(err);
    },
  };
}

async function uploadAll(files: File[]): Promise<void> {
  if (!files.length) return;
  errorEl.hidden = true;
  const pending = files.map(file => ({ file, ui: queueItem(file) }));
  const worker = async () => {
    for (let next = pending.shift(); next; next = pending.shift()) {
      try { next.ui.done(await MediaApi.upload(albumId, collectionFor(next.file), next.file, next.ui.progress)); }
      catch (err) { next.ui.failed(err); }
    }
  };
  await Promise.all(Array.from({ length: Math.min(UPLOAD_CONCURRENCY, files.length) }, worker));
  await load();
}

const drop = query(page, '.upload__drop');
const input = query<HTMLInputElement>(page, '.upload__input');
input.onchange = () => { void uploadAll(Array.from(input.files ?? [])); input.value = ''; };
drop.addEventListener('dragover', e => { e.preventDefault(); drop.classList.add('upload__drop--over'); });
drop.addEventListener('dragleave', () => drop.classList.remove('upload__drop--over'));
drop.addEventListener('drop', e => {
  e.preventDefault();
  drop.classList.remove('upload__drop--over');
  void uploadAll(Array.from(e.dataTransfer?.files ?? []));
});

// ───────── Delete album ─────────

query<HTMLButtonElement>(page, '.album__delete').onclick = async () => {
  const name = query(page, '.album__name').textContent;
  if (!confirm(`Delete the album "${name}" and all its photos, videos and music? This can't be undone.`)) return;
  try { await AlbumsApi.remove(albumId); location.assign('/albums.html'); }
  catch (err) { showError(err); }
};

if (!albumId) location.replace('/albums.html');
else await load();
