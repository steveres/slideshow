// Viewer preferences, kept in localStorage.

export interface Settings { duration: number; showMap: boolean; muted: boolean; }

const SETTINGS_KEY = 'slideshow.settings.v2'; // bumped so the new default duration applies
export const DEFAULTS: Settings = { duration: 2, showMap: true, muted: false };

export function loadSettings(): Settings {
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
    return raw ? { ...DEFAULTS, ...(JSON.parse(raw) as Partial<Settings>) } : { ...DEFAULTS };
  } catch { return { ...DEFAULTS }; }
}

export function saveSettings(s: Settings): void {
  try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(s)); } catch { /* storage unavailable */ }
}
