// Slideshow contents from a folder on this computer (File System Access API, with an
// <input webkitdirectory> fallback). The chosen folder is remembered in IndexedDB.

import { isMusic, kindOf, type MediaItem, type SlideshowContents, type Track } from './media-types.js';

// Minimal File System Access API typings (Chromium only; not in every lib.dom).
interface FsFileHandle { kind: 'file'; name: string; getFile(): Promise<File>; }
export interface FsDirHandle {
  kind: 'directory'; name: string;
  values(): AsyncIterable<FsFileHandle | FsDirHandle>;
  queryPermission(o: { mode: 'read' }): Promise<PermissionState>;
  requestPermission(o: { mode: 'read' }): Promise<PermissionState>;
}
interface PickerWindow { showDirectoryPicker?(o?: { mode: 'read' }): Promise<FsDirHandle>; }

/** Whether the browser can open a folder directly (Chrome, Edge). */
export const canPickFolder = () => typeof (window as unknown as PickerWindow).showDirectoryPicker === 'function';

/** Asks the user for a folder. Rejects with an AbortError if they cancel. */
export function pickFolder(): Promise<FsDirHandle> {
  return (window as unknown as PickerWindow).showDirectoryPicker!({ mode: 'read' });
}

// ───────── Remembered folder ─────────

// The folder handle can't go in localStorage; IndexedDB can store it.
function idb<T>(mode: IDBTransactionMode, op: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open('slideshow', 1);
    open.onupgradeneeded = () => open.result.createObjectStore('kv');
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
      const req = op(open.result.transaction('kv', mode).objectStore('kv'));
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    };
  });
}
export const saveFolder = (h: FsDirHandle) => idb('readwrite', s => s.put(h, 'dir')).then(() => undefined, () => undefined);
export const loadFolder = () => idb<FsDirHandle | undefined>('readonly', s => s.get('dir')).catch(() => undefined);

// ───────── Reading a folder ─────────

const byName = (a: MediaItem, b: MediaItem) =>
  a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });

/** Optional file written by prepare.bat: lists the media in date-taken order. */
const CONFIG_NAME = 'slideshow.json';
interface OrderConfig { files?: { name?: unknown; taken?: unknown }[]; }

/** Orders items as listed in slideshow.json; anything unlisted (or no config) goes by filename. */
function applyOrder(items: MediaItem[], configText: string | null): MediaItem[] {
  items.sort(byName);
  if (configText === null) return items;
  try {
    const config = JSON.parse(configText.replace(/^﻿/, '')) as OrderConfig;
    const rank = new Map<string, number>();
    const taken = new Map<string, string>();
    (config.files ?? []).forEach((f, i) => {
      if (typeof f.name !== 'string') return;
      rank.set(f.name, i);
      if (typeof f.taken === 'string') taken.set(f.name, f.taken);
    });
    for (const item of items) item.taken = taken.get(item.name);
    const at = (m: MediaItem) => rank.get(m.name) ?? Infinity;
    return items.sort((a, b) => (at(a) - at(b)) || byName(a, b)); // both unlisted -> NaN -> byName
  } catch (err) {
    console.warn(`Ignoring unreadable ${CONFIG_NAME}:`, err);
    return items;
  }
}

export async function contentsFromFolder(dir: FsDirHandle): Promise<SlideshowContents> {
  const items: MediaItem[] = [], tracks: Track[] = [];
  let configText: string | null = null;
  for await (const entry of dir.values()) {
    if (entry.kind !== 'file') continue;
    if (entry.name === CONFIG_NAME) { configText = await (await entry.getFile()).text(); continue; }
    if (isMusic(entry.name)) { tracks.push({ name: entry.name, getFile: () => entry.getFile() }); continue; }
    const kind = kindOf(entry.name);
    if (kind) items.push({ name: entry.name, kind, getFile: () => entry.getFile() });
  }
  return { items: applyOrder(items, configText), tracks };
}

/** Fallback for browsers without a folder picker (Safari, Firefox, mobile). */
export async function contentsFromFiles(files: FileList): Promise<SlideshowContents> {
  const items: MediaItem[] = [], tracks: Track[] = [];
  let configText: string | null = null;
  for (const file of Array.from(files)) {
    const depth = file.webkitRelativePath ? file.webkitRelativePath.split('/').length : 2;
    if (depth !== 2) continue; // top level only
    if (file.name === CONFIG_NAME) { configText = await file.text(); continue; }
    if (isMusic(file.name)) { tracks.push({ name: file.name, getFile: () => Promise.resolve(file) }); continue; }
    const kind = kindOf(file.name);
    if (kind) items.push({ name: file.name, kind, getFile: () => Promise.resolve(file) });
  }
  return { items: applyOrder(items, configText), tracks };
}
