// Entry point for slideshow.html.
//   slideshow.html?album=<id>  plays an album from the API (compiling it first if it has changed)
//   slideshow.html             plays a folder from this computer

import { AlbumsApi } from '../api/albums-api.js';
import { requireSignIn } from '../auth/session.js';
import { mountFolderPicker } from '../components/slideshow/folder-picker.js';
import { mountSlideshow } from '../components/slideshow/slideshow.js';
import { contentsFromManifest } from '../media/api-source.js';
import { query } from '../utils/dom.js';
import { errorMessage } from '../utils/format.js';

const root = query(document, '.slideshow');
const start = query(root, '.start-screen');
const back = query<HTMLAnchorElement>(root, '.slideshow__back');
const backLabel = query(back, '.slideshow__back-label');
const albumId = new URLSearchParams(location.search).get('album');

if (albumId) await playAlbum(albumId);
else await playFolder();

async function playAlbum(id: string): Promise<void> {
  await requireSignIn();
  const albumPage = `/album.html?id=${encodeURIComponent(id)}`;
  back.href = albumPage;
  backLabel.textContent = 'Album';
  back.setAttribute('aria-label', 'Back to the album');
  const message = query(start, '.start-screen__message');
  query(start, '.start-screen__actions').hidden = true; // folder buttons
  query(root, '.settings__change').textContent = 'Back to album';
  message.textContent = 'Loading album…';
  const slideshow = mountSlideshow(root, { onChangeSource: () => location.assign(albumPage) });

  const showMessage = (text: string) => {
    const link = document.createElement('a');
    link.href = albumPage;
    link.textContent = 'Back to the album';
    message.replaceChildren(text, ' ', link);
  };
  try {
    const manifest = await AlbumsApi.playable(id);
    document.title = `${manifest.name} - Slideshow`;
    backLabel.textContent = manifest.name;
    back.setAttribute('aria-label', `Back to the album ${manifest.name}`);
    query(start, '.start-screen__title').textContent = manifest.name;
    if (!manifest.slides.length) {
      showMessage('Nothing to show: none of the photos or videos in this album have both a capture date and a location.');
      return;
    }
    start.hidden = true;
    slideshow.play(contentsFromManifest(manifest));
  } catch (err) {
    showMessage(errorMessage(err));
  }
}

async function playFolder(): Promise<void> {
  back.href = '/';
  back.setAttribute('aria-label', 'Back to the home page');
  let picker: { show(): void } | undefined;
  const slideshow = mountSlideshow(root, { onChangeSource: () => picker?.show() });
  picker = await mountFolderPicker(start, contents => slideshow.play(contents));
}
