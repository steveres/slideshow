// What the player plays, independent of where it comes from (a local folder now, the API next).

export type MediaKind = 'image' | 'video';

/** A photo or video. `taken` is "yyyy-MM-dd HH:mm:ss" when known. */
export interface MediaItem { name: string; kind: MediaKind; getFile(): Promise<File>; taken?: string; }

export interface Track { name: string; getFile(): Promise<File>; }

/** Everything a slideshow needs: the slides in play order and the background music. */
export interface SlideshowContents { items: MediaItem[]; tracks: Track[]; }

export interface LatLon { lat: number; lon: number; }

// Keep in step with prepare.ps1 at the repo root.
const IMAGE_EXT = new Set(['jpg', 'jpeg', 'png', 'gif', 'webp', 'avif', 'bmp', 'svg']);
const VIDEO_EXT = new Set(['mp4', 'm4v', 'webm', 'mov', 'ogv']);

export const isMusic = (name: string) => name.toLowerCase().endsWith('.mp3');

export function kindOf(name: string): MediaKind | null {
  const ext = name.slice(name.lastIndexOf('.') + 1).toLowerCase();
  return IMAGE_EXT.has(ext) ? 'image' : VIDEO_EXT.has(ext) ? 'video' : null;
}
