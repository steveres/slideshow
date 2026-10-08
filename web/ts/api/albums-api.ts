// /api/v1/albums and their images (photos + videos) and music.

import { api, ApiError } from './api-client.js';

export interface AlbumInfo {
  id: string; name: string; description: string | null; createdAt: string; updatedAt: string;
  imageCount: number; videoCount: number; musicCount: number;
  /** Photos/videos the slideshow leaves out (no capture date or no location). */
  excludedCount: number;
  totalBytes: number;
  compiledAt: string | null;
  /** Never compiled, or changed since. */
  isStale: boolean;
  /** Preview of the album's first photo (in slideshow order), or null. */
  coverThumbnailUrl: string | null;
  /** Anyone with the link can watch. */
  isShared: boolean;
}

/** Link sharing for an album. `token` goes in the share link; null when not shared. */
export interface ShareInfo { enabled: boolean; token: string | null; showMap: boolean; }

/** What a share link gives: the slideshow only. */
export interface SharedAlbum { name: string; showMap: boolean; slides: MediaInfo[]; music: MediaInfo[]; }

/** The address to send people: plays the album without signing in. */
export const shareLink = (token: string) => `${location.origin}/slideshow.html?share=${encodeURIComponent(token)}`;

export type MissingData = 'date' | 'location';

export interface MediaInfo {
  id: string; fileName: string; kind: 'image' | 'video' | 'audio'; contentType: string; sizeBytes: number; uploadedAt: string;
  /** Local wall-clock capture time, "2026-07-04T09:31:05", or null. */
  taken: string | null;
  takenSource: 'exif' | 'video' | null;
  location: { lat: number; lon: number } | null;
  /** Shown in the slideshow (music: always). */
  playable: boolean;
  /** Why not, when not playable. */
  missing: MissingData[];
  /** Path of the file's bytes on the API. */
  url: string;
  /** Path of a small JPEG preview (photos only), or null. */
  thumbnailUrl: string | null;
}

export interface AlbumManifest {
  albumId: string; name: string; compiledAt: string; isStale: boolean;
  /** Playable photos and videos in play order. */
  slides: MediaInfo[];
  music: MediaInfo[];
  excludedCount: number;
}

export type Collection = 'images' | 'music';

const albums = '/api/v1/albums';
const at = (id: string) => `${albums}/${encodeURIComponent(id)}`;

export const AlbumsApi = {
  list: () => api.get<AlbumInfo[]>(albums),
  info: (id: string) => api.get<AlbumInfo>(`${at(id)}/info`),
  async create(name: string, description?: string): Promise<AlbumInfo> {
    return (await api.post<AlbumInfo>(albums, { name, description }))!;
  },
  remove: (id: string) => api.delete(at(id)),
  async compile(id: string): Promise<AlbumManifest> {
    return (await api.post<AlbumManifest>(`${at(id)}/compile`))!;
  },
  /** The compiled album, compiling first if it never was or has changed since. */
  async playable(id: string): Promise<AlbumManifest> {
    try {
      const manifest = await api.get<AlbumManifest>(at(id));
      return manifest.isStale ? await AlbumsApi.compile(id) : manifest;
    } catch (err) {
      if (err instanceof ApiError && err.code === 'album_not_compiled') return AlbumsApi.compile(id);
      throw err;
    }
  },
};

export const SharingApi = {
  get: (albumId: string) => api.get<ShareInfo>(`${at(albumId)}/share`),
  update: (albumId: string, changes: { enabled?: boolean; showMap?: boolean }) => api.put<ShareInfo>(`${at(albumId)}/share`, changes),
  async newLink(albumId: string): Promise<ShareInfo> {
    return (await api.post<ShareInfo>(`${at(albumId)}/share/reset`))!;
  },
  /** For viewers: no sign-in. */
  shared: (token: string) => api.publicGet<SharedAlbum>(`/api/v1/shared/${encodeURIComponent(token)}`),
  download: (url: string) => api.publicBlob(url),
};

export const MediaApi = {
  list: (albumId: string, collection: Collection) => api.get<MediaInfo[]>(`${at(albumId)}/${collection}`),

  /** Uploads one file. The API reads its capture date and location from the file itself. */
  upload(albumId: string, collection: Collection, file: File, onProgress?: (fraction: number) => void): Promise<MediaInfo> {
    const form = new FormData();
    form.append('file', file, file.name);
    // Videos record UTC; the uploader's offset turns that into local time like a photo's EXIF date.
    form.append('utcOffsetMinutes', String(-new Date().getTimezoneOffset()));
    return api.upload<MediaInfo>(`${at(albumId)}/${collection}`, form, onProgress);
  },

  remove: (albumId: string, collection: Collection, id: string) => api.delete(`${at(albumId)}/${collection}/${encodeURIComponent(id)}`),

  /** The file's bytes, by the `url` the API gave for it. */
  download: (url: string) => api.blob(url),
};

/** Which collection a picked file belongs in. */
export const collectionFor = (file: File): Collection =>
  file.type === 'audio/mpeg' || file.name.toLowerCase().endsWith('.mp3') ? 'music' : 'images';
