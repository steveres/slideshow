// Slideshow contents from the API: a compiled album (signed in) or a shared album (share link, no
// sign-in). Files are downloaded when the player asks for them, and the few most recent are kept so
// prefetching pays off.

import { MediaApi, SharingApi, type MediaInfo } from '../api/albums-api.js';
import type { MediaItem, SlideshowContents, Track } from './media-types.js';

type Download = (url: string) => Promise<Blob>;

/** Downloads on demand and keeps the `keep` most recently used files in memory. */
function downloader(keep: number, download: Download) {
  const cache = new Map<string, Promise<File>>(); // insertion order = least recently used first
  return (m: MediaInfo) => (): Promise<File> => {
    let file = cache.get(m.id);
    if (file) {
      cache.delete(m.id); // re-inserted below as most recent
    } else {
      file = download(m.url).then(blob => new File([blob], m.fileName, { type: m.contentType }));
      file.catch(() => cache.delete(m.id)); // don't keep failures
    }
    cache.set(m.id, file);
    while (cache.size > keep) cache.delete(cache.keys().next().value!);
    return file;
  };
}

function contents(slides: MediaInfo[], music: MediaInfo[], download: Download): SlideshowContents {
  const slide = downloader(4, download); // current, next, and a couple for stepping back
  const track = downloader(1, download);
  const items: MediaItem[] = slides.map(m => ({
    name: m.fileName,
    kind: m.kind === 'video' ? 'video' : 'image',
    taken: m.taken ?? undefined,
    location: m.location, // null when the album's owner hid the map from viewers
    getFile: slide(m),
  }));
  const tracks: Track[] = music.map(m => ({ name: m.fileName, getFile: track(m) }));
  return { items, tracks };
}

/** The signed-in owner's compiled album. */
export const contentsFromManifest = (manifest: { slides: MediaInfo[]; music: MediaInfo[] }): SlideshowContents =>
  contents(manifest.slides, manifest.music, MediaApi.download);

/** A shared album, played from its share link without signing in. */
export const contentsFromSharedAlbum = (shared: { slides: MediaInfo[]; music: MediaInfo[] }): SlideshowContents =>
  contents(shared.slides, shared.music, SharingApi.download);
