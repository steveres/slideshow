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

/** An error as a sentence for the page. */
export function errorMessage(err: unknown): string {
  if (err instanceof TypeError) return 'Could not reach the server. Check your connection, and that the API is running.';
  return err instanceof Error ? err.message : String(err);
}
