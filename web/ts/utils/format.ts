// Display formatting.

const UNITS = ['bytes', 'KB', 'MB', 'GB', 'TB'];

/** 1536 -> "1.5 KB" */
export function formatBytes(bytes: number): string {
  let value = bytes, unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) { value /= 1024; unit++; }
  return `${unit === 0 ? value : value.toFixed(value < 10 ? 1 : 0)} ${UNITS[unit]}`;
}

/** ISO timestamp -> "October 5, 2026" in the viewer's locale. */
export function formatDay(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
}

/** Capture time as recorded ("2026-07-04T09:31:05", no zone) -> "Jul 4, 2026, 9:31 AM". Never shifted by time zone. */
export function formatTaken(taken: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2}):(\d{2})/.exec(taken);
  if (!m) return taken;
  const [, y, mo, d, h, mi, s] = m.map(Number);
  return new Date(y, mo - 1, d, h, mi, s).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

/** "1 photo", "3 photos" */
export const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;

/** "12 photos · 2 videos · 3 tracks · 45 MB" */
export function albumSummary(a: { imageCount: number; videoCount: number; musicCount: number; totalBytes: number }): string {
  const parts = [plural(a.imageCount, 'photo'), plural(a.videoCount, 'video'), plural(a.musicCount, 'track')];
  return `${parts.join(' · ')} · ${formatBytes(a.totalBytes)}`;
}

/** An error as a sentence for the page. */
export function errorMessage(err: unknown): string {
  if (err instanceof TypeError) return 'Could not reach the server. Check your connection, and that the API is running.';
  return err instanceof Error ? err.message : String(err);
}
