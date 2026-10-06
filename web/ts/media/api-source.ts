// Slideshow contents from a compiled album on the API. Files are downloaded when the player asks
// for them (with the access token), and the few most recent are kept so prefetching pays off.

import { MediaApi, type AlbumManifest, type MediaInfo } from '../api/albums-api.js';
import type { MediaItem, SlideshowContents, Track } from './media-types.js';

/** Downloads on demand and keeps the `keep` most recently used files in memory. */
function downloader(keep: number) {
  const cache = new Map<string, Promise<File>>(); // insertion order = least recently used first
  return (m: MediaInfo) => (): Promise<File> => {
    let file = cache.get(m.id);
    if (file) {
      cache.delete(m.id); // re-inserted below as most recent
    } else {
      file = MediaApi.download(m.url).then(blob => new File([blob], m.fileName, { type: m.contentType }));
      file.catch(() => cache.delete(m.id)); // don't keep failures
    }
    cache.set(m.id, file);
    while (cache.size > keep) cache.delete(cache.keys().next().value!);
    return file;
  };
}

export function contentsFromManifest(manifest: AlbumManifest): SlideshowContents {
  const slide = downloader(4); // current, next, and a couple for stepping back
  const track = downloader(1);

  const items: MediaItem[] = manifest.slides.map(m => ({
    name: m.fileName,
    kind: m.kind === 'video' ? 'video' : 'image',
    taken: m.taken ?? undefined,
    location: m.location,
    getFile: slide(m),
  }));
  const tracks: Track[] = manifest.music.map(m => ({ name: m.fileName, getFile: track(m) }));
  return { items, tracks };
}
