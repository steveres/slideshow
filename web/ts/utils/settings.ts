// Viewer preferences, kept in localStorage.

export interface Settings {
  duration: number;
  showMap: boolean;
  muted: boolean;
  /** Ken Burns motion: a gentle zoom or pan on each photo. */
  motion: boolean;
  /** Photos fill the frame (cropping the overhang) instead of fitting inside it with bars. */
  fill: boolean;
  /** At the end: start again (on), or stay on the last slide and fade the music out (off). */
  repeat: boolean;
}

const SETTINGS_KEY = 'slideshow.settings.v2'; // bumped so the new default duration applies
const prefersReducedMotion = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
export const DEFAULTS: Settings = { duration: 2, showMap: true, muted: false, motion: !prefersReducedMotion, fill: true, repeat: true };

export function loadSettings(): Settings {
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
    return raw ? { ...DEFAULTS, ...(JSON.parse(raw) as Partial<Settings>) } : { ...DEFAULTS };
  } catch { return { ...DEFAULTS }; }
}

export function saveSettings(s: Settings): void {
  try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(s)); } catch { /* storage unavailable */ }
}
