// The route map: the trip so far drawn over Google Maps, with a marker on the photo on screen.

import { FIRST_ZOOM, GOOGLE_MAPS_API_KEY, MAP_SEGMENT_MS, MAP_SPAN, MAP_ZOOM_MS, MAX_ZOOM, MIN_ZOOM, TILE } from '../../config.js';
import type { LatLon } from '../../media/media-types.js';
import { clamp, ease, lerp, query } from '../../utils/dom.js';

// ───────── Google Maps loader ─────────

// Minimal typings for the parts of the Google Maps API used here.
interface GLatLng { lat(): number; lng(): number; }
interface GMapView { center: { lat: number; lng: number }; zoom: number; }
interface GMap {
  moveCamera(o: GMapView): void; // immediate, no built-in animation
  setOptions(o: Record<string, unknown>): void;
  getZoom(): number | undefined;
  getCenter(): GLatLng | undefined;
}
interface GMaps {
  Map: new (el: HTMLElement, opts: GMapView & Record<string, unknown>) => GMap;
  event: { addListener(map: GMap, event: string, fn: () => void): unknown };
}
interface GoogleWindow { google?: { maps: GMaps }; __mapsReady?: () => void; gm_authFailure?: () => void; }
const gWindow = window as unknown as GoogleWindow;

let mapsApi: Promise<GMaps> | null = null;

/** Loads Google's script once, on first use. */
function loadGoogleMaps(): Promise<GMaps> {
  mapsApi ??= new Promise<GMaps>((resolve, reject) => {
    if (!GOOGLE_MAPS_API_KEY) {
      reject(new Error('No Google Maps API key set. Add it to web/apikey.txt and rebuild.'));
      return;
    }
    gWindow.__mapsReady = () => {
      const maps = gWindow.google?.maps;
      if (maps) resolve(maps); else reject(new Error('Google Maps did not start.'));
    };
    const script = document.createElement('script');
    script.src = `https://maps.googleapis.com/maps/api/js?key=${encodeURIComponent(GOOGLE_MAPS_API_KEY)}&loading=async&callback=__mapsReady`;
    script.onerror = () => {
      script.remove();
      mapsApi = null; // allow a retry next time a map is due
      reject(new Error('Google Maps could not be loaded. Check the internet connection.'));
    };
    document.head.append(script);
  });
  return mapsApi;
}

// ───────── Controls ─────────

/** The phone layouts (same rules as slideshow.css): upright, or sideways with the map as a small card. */
const phoneLayout = window.matchMedia('(orientation: portrait) and (max-width: 768px), (orientation: landscape) and (max-height: 500px)');

/** Desktop: zoom buttons only (no map-type menu). Phones: no controls; drag and pinch still work. */
const controlOptions = (): Record<string, unknown> => ({ disableDefaultUI: true, zoomControl: !phoneLayout.matches });

// ───────── Projection ─────────

interface Point { x: number; y: number; }

/** Lat/lon -> Web Mercator world coordinates in 0..1 (same projection Google uses). */
function project(p: LatLon): Point {
  const rad = clamp(p.lat, -85, 85) * Math.PI / 180;
  return { x: (p.lon + 180) / 360, y: (1 - Math.log(Math.tan(rad) + 1 / Math.cos(rad)) / Math.PI) / 2 };
}
function unproject(p: Point): LatLon {
  return { lat: Math.atan(Math.sinh(Math.PI * (1 - 2 * p.y))) * 180 / Math.PI, lon: p.x * 360 - 180 };
}

interface Camera { centre: Point; zoom: number; }
/** What the overlay shows: dots on the first `dotCount` stops; line and marker positions as
 *  (fractional) indexes along the route. */
interface OverlayState { dotCount: number; lineTo: number; markerAt: number; }

// ───────── Map view ─────────

/** The always-visible map panel: route so far (dots + line) with a marker on the current photo. */
export class MapView {
  private canvas: HTMLElement;
  private marker: HTMLElement;
  private notice: HTMLElement;
  private overlay: SVGSVGElement;
  private line: SVGPolylineElement;
  private dots: SVGGElement;
  private gmap: GMap | null = null;
  private authFailed = false;
  private renderId = 0;                     // bumping this cancels any running animation
  private animating = false;
  private applying = false;                 // inside our own moveCamera call
  private pts: Point[] = [];                // route currently drawn (world coordinates)
  private cam: Camera | null = null;        // camera the overlay is drawn for
  private state: OverlayState | null = null;
  private userZoom: number | null = null;   // set once the user zooms (buttons or wheel)
  private w = 0;
  private h = 0;

  constructor(readonly root: HTMLElement) {
    this.canvas = query(root, '.route-map__canvas');
    this.marker = query(root, '.route-map__pin');
    this.notice = query(root, '.route-map__notice');
    this.overlay = query<SVGSVGElement>(root, '.route-map__path');
    this.line = query<SVGPolylineElement>(root, '.route-map__path polyline');
    this.dots = query<SVGGElement>(root, '.route-map__dots');
    // Google calls this global when the key is invalid, restricted or unbilled.
    gWindow.gm_authFailure = () => {
      this.authFailed = true;
      this.unavailable('Google rejected the API key. Check the key, its restrictions and billing.');
    };
  }

  /**
   * Shows the route; its last entry is the photo now on screen.
   * The camera glides from where it is to the new framing. With `grow` (a step forward onto a
   * located photo) the line is then drawn to the new stop with the marker travelling along it.
   */
  async update(path: readonly LatLon[], grow: boolean): Promise<void> {
    if (!path.length) { this.clearOverlay(); return; }
    const pts = path.map(project), last = pts.length - 1;
    const hadRoute = this.pts.length > 0;
    this.pts = pts;
    if (!this.measure()) return;
    const id = ++this.renderId;
    const live = () => id === this.renderId;

    try {
      const maps = await loadGoogleMaps();
      if (this.authFailed || !live()) return;
      const final = this.target(pts);
      const from = this.cam;
      this.ensureMap(maps, final);
      this.notice.hidden = true;
      this.canvas.hidden = false;
      this.overlay.toggleAttribute('hidden', false);

      if (!from || !hadRoute) { // nothing to animate from
        this.animating = false;
        this.setCamera(final);
        this.draw(pts, last, last, last);
        return;
      }

      this.animating = true;
      const growing = grow && last > 0;
      const held = growing ? last - 1 : last; // while the camera moves, the marker stays on this stop
      this.draw(pts, held, held, held);

      const moved = Math.abs(from.zoom - final.zoom) > 0.01 || Math.hypot(from.centre.x - final.centre.x, from.centre.y - final.centre.y) * TILE * 2 ** final.zoom > 1;
      if (moved) {
        await this.tween(MAP_ZOOM_MS, live, t => {
          const e = ease(t);
          this.setCamera({
            centre: { x: lerp(from.centre.x, final.centre.x, e), y: lerp(from.centre.y, final.centre.y, e) },
            zoom: lerp(from.zoom, final.zoom, e),
          });
          this.draw(pts, held, held, held);
        });
        if (!live()) return;
      }
      if (growing) {
        // The marker rides the tip of the line and leaves a dot behind.
        await this.tween(MAP_SEGMENT_MS, live, t => {
          const head = last - 1 + ease(t);
          this.draw(pts, last, head, head);
        });
        if (!live()) return;
      }
      this.draw(pts, last, last, last);
      this.animating = false;
    } catch (err) {
      this.unavailable((err as Error).message);
    }
  }

  /** Redraws the current route in its finished state (after a resize or the panel reappearing). */
  refresh(): void {
    if (!this.gmap || !this.pts.length || !this.measure()) return;
    this.renderId++;
    this.animating = false;
    const last = this.pts.length - 1;
    this.setCamera(this.target(this.pts));
    this.draw(this.pts, last, last, last);
  }

  /** Empties the route and returns to automatic zoom (restart or new folder). */
  reset(): void {
    this.clearOverlay();
    this.userZoom = null;
  }

  /** Message shown in place of the map when Google Maps can't be used. */
  unavailable(reason: string): void {
    this.renderId++;
    this.animating = false;
    this.canvas.hidden = true;
    this.marker.hidden = true;
    this.overlay.toggleAttribute('hidden', true);
    this.notice.textContent = reason;
    this.notice.hidden = false;
  }

  private clearOverlay(): void {
    this.renderId++;
    this.animating = false;
    this.pts = [];
    this.state = null;
    this.line.setAttribute('points', '');
    this.dots.replaceChildren();
    this.marker.hidden = true;
  }

  private ensureMap(maps: GMaps, initial: Camera): void {
    if (this.gmap) return;
    this.cam = initial;
    const c = unproject(initial.centre);
    const gmap = this.gmap = new maps.Map(this.canvas, {
      center: { lat: c.lat, lng: c.lon }, zoom: initial.zoom, isFractionalZoomEnabled: true,
      ...controlOptions(),
      gestureHandling: 'greedy', // drag to pan; wheel, pinch or buttons to zoom
      keyboardShortcuts: false, clickableIcons: false,
    });
    phoneLayout.addEventListener('change', () => gmap.setOptions(controlOptions())); // rotated or resized
    maps.event.addListener(gmap, 'zoom_changed', () => this.onGoogleCamera());
    maps.event.addListener(gmap, 'center_changed', () => this.onGoogleCamera());
  }

  /** Google's camera changed. If it wasn't us (zoom buttons, wheel, dragging), follow it; a user zoom locks the zoom level. */
  private onGoogleCamera(): void {
    if (this.animating || !this.gmap || !this.cam) return;
    const gc = this.gmap.getCenter(), gz = this.gmap.getZoom();
    if (!gc || gz === undefined) return;
    const centre = project({ lat: gc.lat(), lon: gc.lng() });
    const dz = Math.abs(gz - this.cam.zoom);
    const dpx = Math.hypot(centre.x - this.cam.centre.x, centre.y - this.cam.centre.y) * TILE * 2 ** gz;
    if (dz < 1e-6 && dpx < 0.5) return;   // echo of our own setCamera
    if (!this.applying && dz > 1e-6) this.userZoom = gz; // changed by the user (buttons or wheel): lock it
    this.cam = { centre, zoom: gz };
    if (this.state) this.draw(this.pts, this.state.dotCount, this.state.lineTo, this.state.markerAt);
  }

  /** Where the camera should end up: fit the route, or (after a user zoom) their zoom centred on the current photo. */
  private target(pts: Point[]): Camera {
    return this.userZoom !== null ? { centre: pts[pts.length - 1], zoom: this.userZoom } : this.frame(pts);
  }

  /** Records the panel size; false if the panel is hidden. */
  private measure(): boolean {
    this.w = this.root.clientWidth;
    this.h = this.root.clientHeight;
    return this.w > 0 && this.h > 0;
  }

  /** Camera that fits all points with MAP_SPAN breathing room; a single place gets the fixed zoom. */
  private frame(pts: Point[]): Camera {
    const xs = pts.map(p => p.x), ys = pts.map(p => p.y);
    const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
    const centre = { x: (minX + maxX) / 2, y: (minY + maxY) / 2 };
    if (maxX - minX < 1e-9 && maxY - minY < 1e-9) return { centre, zoom: FIRST_ZOOM };
    const zoom = Math.min(Math.log2(this.w / (TILE * MAP_SPAN * (maxX - minX))), Math.log2(this.h / (TILE * MAP_SPAN * (maxY - minY))));
    return { centre, zoom: clamp(zoom, MIN_ZOOM, MAX_ZOOM) };
  }

  private setCamera(cam: Camera): void {
    this.cam = cam; // set first so the change event is recognised as ours
    const c = unproject(cam.centre);
    this.applying = true;
    try { this.gmap?.moveCamera({ center: { lat: c.lat, lng: c.lon }, zoom: cam.zoom }); }
    finally { this.applying = false; }
  }

  /** Draws the overlay for the current camera. */
  private draw(pts: Point[], dotCount: number, lineTo: number, markerAt: number): void {
    const cam = this.cam;
    if (!cam) return;
    this.state = { dotCount, lineTo, markerAt };
    const scale = TILE * 2 ** cam.zoom;
    const screen = pts.map(p => ({ x: this.w / 2 + (p.x - cam.centre.x) * scale, y: this.h / 2 + (p.y - cam.centre.y) * scale }));
    const along = (pos: number): Point => {
      const i = Math.min(Math.floor(pos), screen.length - 1), j = Math.min(i + 1, screen.length - 1), f = pos - i;
      return { x: lerp(screen[i].x, screen[j].x, f), y: lerp(screen[i].y, screen[j].y, f) };
    };

    const linePts = screen.slice(0, Math.floor(lineTo) + 1);
    if (lineTo > Math.floor(lineTo)) linePts.push(along(lineTo));
    this.line.setAttribute('points', linePts.map(p => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' '));

    while (this.dots.childElementCount < dotCount) {
      const dot = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
      dot.setAttribute('r', '5');
      this.dots.append(dot);
    }
    while (this.dots.childElementCount > dotCount) this.dots.lastElementChild?.remove();
    Array.from(this.dots.children).forEach((dot, i) => {
      dot.setAttribute('cx', screen[i].x.toFixed(1));
      dot.setAttribute('cy', screen[i].y.toFixed(1));
    });

    const m = along(markerAt);
    this.marker.style.left = `${m.x}px`;
    this.marker.style.top = `${m.y}px`;
    this.marker.hidden = false;
  }

  /** Calls step(t) every frame with t going 0..1 over `ms`; stops early if cancelled. */
  private tween(ms: number, live: () => boolean, step: (t: number) => void): Promise<void> {
    return new Promise(resolve => {
      const start = performance.now();
      const frame = (now: number) => {
        if (!live()) { resolve(); return; }
        const t = clamp((now - start) / ms, 0, 1);
        step(t);
        if (t < 1) requestAnimationFrame(frame); else resolve();
      };
      requestAnimationFrame(frame);
    });
  }
}
