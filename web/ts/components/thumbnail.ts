// Photo previews. They need the access token, so they can't be plain <img src>: each is fetched when
// it scrolls into view, a few at a time, and remembered (previews never change) so re-rendering a
// list doesn't download them again.

import { MediaApi } from '../api/albums-api.js';

const MAX_PARALLEL = 6;
const loaded = new Map<string, Promise<string>>(); // preview URL -> object URL
const queue: (() => Promise<void>)[] = [];
let running = 0;

function pump(): void {
  while (running < MAX_PARALLEL && queue.length) {
    const job = queue.shift()!;
    running++;
    void job().finally(() => { running--; pump(); });
  }
}

function objectUrl(url: string): Promise<string> {
  let p = loaded.get(url);
  if (!p) {
    p = MediaApi.download(url).then(blob => URL.createObjectURL(blob));
    p.catch(() => loaded.delete(url)); // try again next time
    loaded.set(url, p);
  }
  return p;
}

const observer = new IntersectionObserver(entries => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    observer.unobserve(entry.target);
    const img = entry.target as HTMLImageElement;
    queue.push(async () => {
      try {
        img.src = await objectUrl(img.dataset.src!);
        img.closest('.thumb')?.classList.add('thumb--loaded');
      } catch {
        img.closest('.thumb')?.classList.add('thumb--failed');
      }
    });
    pump();
  }
}, { rootMargin: '300px' }); // start a little before it's visible

/**
 * A preview box: the photo when `url` is set, otherwise a placeholder (▶ for videos).
 * Size and shape come from CSS (`.thumb` plus the given class).
 */
export function thumbnail(url: string | null, kind: 'image' | 'video' | 'audio', className = ''): HTMLElement {
  const box = document.createElement('span');
  box.className = `thumb ${className}`.trim();
  if (url) {
    const img = document.createElement('img');
    img.alt = '';
    img.decoding = 'async';
    img.dataset.src = url;
    box.append(img);
    observer.observe(img);
  } else {
    box.classList.add('thumb--none');
    box.textContent = kind === 'video' ? '▶' : '';
    if (kind === 'video') box.title = 'Video';
  }
  return box;
}
