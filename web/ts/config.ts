// Settings: per-environment values (read at startup) and tunable constants.

import type { AppConfig } from './app-config-types.js';

// /app-config.json is written by build.mjs (locally) or infra/deploy.ps1 (Azure), so the same compiled
// scripts run everywhere. Modules that import this file wait for it (top-level await).
const loaded = await (await fetch('/app-config.json', { cache: 'no-cache' })).json() as AppConfig & { googleMapsApiKey?: string };

export const APP_CONFIG: AppConfig = loaded;
export const GOOGLE_MAPS_API_KEY = loaded.googleMapsApiKey ?? '';

// Slideshow timing and layout
export const FADE_MS = 1000;        // cross-fade length
export const MAP_ZOOM_MS = 800;     // zoom/pan to the new framing
export const MAP_SEGMENT_MS = 600;  // draw the line to the new stop and move the marker along it
export const FIRST_ZOOM = 10;       // zoom while the route is a single place
export const MAP_SPAN = 1.5;        // view is 1.5x the size of the route's bounding box
export const MIN_ZOOM = 1, MAX_ZOOM = 17;
export const MAX_DURATION = 5;      // seconds per image: 1..5 (speed slider and settings field)
export const MAX_PHOTO_ZOOM = 8;    // wheel zoom limit on the photo
export const TIMELINE_LABELS = 10;  // date labels along the timeline
export const TILE = 256;            // Web Mercator world size in px at zoom 0
export const OVERLAY_IDLE_MS = 3000; // hide the controls, timeline and buttons after this long without input
export const BACKDROP_CROP = 0.25;  // a photo that would lose more than this to cropping is shown whole, over a blurred copy
export const VIDEO_NUDGE_MS = 1000; // a video not loaded by then gets a muted play() to start loading (iOS Safari)
export const MUSIC_FADE_MS = 4000;  // music fade-out when the slideshow ends (Repeat off)
