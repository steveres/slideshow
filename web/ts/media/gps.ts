// Reads where a photo or video was taken, from the file itself.

import type { LatLon, MediaKind } from './media-types.js';

export async function readLocation(file: File, kind: MediaKind): Promise<LatLon | null> {
  try { return kind === 'image' ? await exifGps(file) : await videoGps(file); }
  catch { return null; } // malformed metadata: treat as "no location"
}

/** Reads GPS from a JPEG's EXIF block. Only the file head is read. */
async function exifGps(file: File): Promise<LatLon | null> {
  const view = new DataView(await file.slice(0, 256 * 1024).arrayBuffer());
  if (view.byteLength < 4 || view.getUint16(0) !== 0xffd8) return null;
  for (let off = 2; off + 4 <= view.byteLength;) {
    const marker = view.getUint16(off);
    if ((marker & 0xff00) !== 0xff00 || marker === 0xffda) break;
    if (marker === 0xffe1 && view.getUint32(off + 4) === 0x45786966 /* "Exif" */) return tiffGps(view, off + 10);
    off += 2 + view.getUint16(off + 2);
  }
  return null;
}

function tiffGps(view: DataView, tiff: number): LatLon | null {
  const le = view.getUint16(tiff) === 0x4949;
  const u16 = (o: number) => view.getUint16(tiff + o, le);
  const u32 = (o: number) => view.getUint32(tiff + o, le);
  const findTag = (ifd: number, tag: number): number | null => {
    for (let i = 0, n = u16(ifd); i < n; i++) {
      const entry = ifd + 2 + i * 12;
      if (u16(entry) === tag) return entry;
    }
    return null;
  };
  const gpsPtr = findTag(u32(4), 0x8825);
  if (gpsPtr === null) return null;
  const gps = u32(gpsPtr + 8);

  const coord = (refTag: number, valTag: number, negativeRef: string): number | null => {
    const ref = findTag(gps, refTag), val = findTag(gps, valTag);
    if (ref === null || val === null) return null;
    const p = u32(val + 8);
    let deg = 0; // degrees, minutes, seconds as three rationals
    for (let i = 0; i < 3; i++) {
      const den = u32(p + i * 8 + 4);
      if (den) deg += u32(p + i * 8) / den / 60 ** i;
    }
    return String.fromCharCode(view.getUint8(tiff + ref + 8)) === negativeRef ? -deg : deg;
  };
  const lat = coord(1, 2, 'S'), lon = coord(3, 4, 'W');
  if (lat === null || lon === null || (lat === 0 && lon === 0)) return null;
  return { lat, lon };
}

/** Best effort: phones store an ISO 6709 string like "+37.7749-122.4194" in MP4/MOV metadata. */
async function videoGps(file: File): Promise<LatLon | null> {
  const CHUNK = 1 << 20;
  const parts = [file.slice(0, CHUNK)];
  if (file.size > CHUNK) parts.push(file.slice(Math.max(CHUNK, file.size - CHUNK)));
  const iso6709 = /([+-]\d{2}\.\d{2,})([+-]\d{3}\.\d{2,})/;
  for (const part of parts) {
    const m = iso6709.exec(new TextDecoder('latin1').decode(await part.arrayBuffer()));
    if (m) return { lat: Number(m[1]), lon: Number(m[2]) };
  }
  return null;
}
