// The slideshow component: photo frame, controls, timeline, route map, music and settings.
// It plays whatever SlideshowContents it is given and doesn't know where they came from.

import { FADE_MS, MAX_DURATION, MUSIC_FADE_MS, OVERLAY_IDLE_MS } from '../../config.js';
import type { SlideshowContents } from '../../media/media-types.js';
import { clamp, query } from '../../utils/dom.js';
import { DEFAULTS, loadSettings, saveSettings } from '../../utils/settings.js';
import { MapView } from './map-view.js';
import { MusicPlayer } from './music-player.js';
import { PhotoZoom } from './photo-zoom.js';
import { Player } from './player.js';
import { Timeline } from './timeline.js';

export interface SlideshowOptions {
  /** The settings panel's "change" button was pressed (e.g. go back to choosing a folder or album). */
  onChangeSource?: () => void;
  /** False hides the map whatever the viewer's setting (a shared album whose owner hid it). Default true. */
  mapAllowed?: boolean;
}

export interface SlideshowHandle {
  play(contents: SlideshowContents): void;
  stop(): void;
}

/** Wires up the slideshow markup inside `root` (see slideshow.html). */
export function mountSlideshow(root: HTMLElement, options: SlideshowOptions = {}): SlideshowHandle {
  root.style.setProperty('--fade', `${FADE_MS}ms`);

  const settings = loadSettings();
  const photos = query(root, '.slideshow__photos');
  const map = new MapView(query(root, '.route-map'));
  const player = new Player(Array.from(root.querySelectorAll<HTMLElement>('.slideshow__layer')), map, settings);
  const music = new MusicPlayer();
  let active = false; // something is loaded and playing (or paused)

  const mapAllowed = options.mapAllowed ?? true;
  const applyMapVisibility = () => { root.classList.toggle('slideshow--no-map', !(settings.showMap && mapAllowed)); player.syncMap(); };
  if (!mapAllowed) query(root, '.settings__show-map').closest('label')!.hidden = true;
  applyMapVisibility();

  const photoZoom = new PhotoZoom(photos, () => player.currentMedia(), () => player.setPlaying(false)); // zooming in pauses
  player.onShow = () => photoZoom.reset();
  const timeline = new Timeline(query(root, '.timeline'),
    i => { photos.classList.add('slideshow__photos--scrubbing'); player.scrubTo(i); },   // flip without cross-fade
    () => { photos.classList.remove('slideshow__photos--scrubbing'); void player.endScrub(); });
  player.onIndex = i => timeline.setIndex(i);
  window.addEventListener('resize', () => { map.refresh(); photoZoom.reset(); });

  const gear = query<HTMLButtonElement>(root, '.settings__toggle'), panel = query(root, '.settings__panel');
  const duration = query<HTMLInputElement>(root, '.settings__duration'), showMap = query<HTMLInputElement>(root, '.settings__show-map');
  const muteBtn = query<HTMLButtonElement>(root, '.controls__mute');
  const playBtn = query<HTMLButtonElement>(root, '.controls__play');
  const speed = query<HTMLInputElement>(root, '.controls__speed-input'), speedOut = query(root, '.controls__speed-out');

  // Duration has two controls (settings field and speed slider); keep them in step.
  const setDuration = (seconds: number) => {
    settings.duration = clamp(Math.round(seconds) || DEFAULTS.duration, 1, MAX_DURATION);
    duration.value = String(settings.duration);
    speed.value = String(MAX_DURATION + 1 - settings.duration); // right = faster
    speedOut.textContent = `${settings.duration}s`;
    saveSettings(settings);
    player.retime();
  };
  setDuration(settings.duration);
  duration.onchange = () => setDuration(Number(duration.value));
  speed.oninput = () => setDuration(MAX_DURATION + 1 - Number(speed.value));

  // Music is silent while the slideshow is paused, or when muted; at the end (Repeat off) it fades out instead.
  let playing = true;
  let ending = false;
  const applyMuted = () => {
    music.setMuted(settings.muted || (!playing && !ending));
    muteBtn.setAttribute('aria-label', settings.muted ? 'Unmute music' : 'Mute music');
    muteBtn.querySelector('.icon-sound')?.toggleAttribute('hidden', settings.muted);
    muteBtn.querySelector('.icon-muted')?.toggleAttribute('hidden', !settings.muted);
  };
  applyMuted();
  muteBtn.onclick = () => { settings.muted = !settings.muted; saveSettings(settings); applyMuted(); };

  // Control bar
  player.onEnded = () => { ending = true; music.fadeOut(MUSIC_FADE_MS); };
  player.onState = isPlaying => {
    if (isPlaying && ending) { ending = false; music.resume(); }
    playing = isPlaying;
    applyMuted();
    wake();
    playBtn.setAttribute('aria-label', isPlaying ? 'Pause' : 'Play');
    playBtn.querySelector('.icon-pause')?.toggleAttribute('hidden', !isPlaying);
    playBtn.querySelector('.icon-play')?.toggleAttribute('hidden', isPlaying);
  };
  playBtn.onclick = () => player.toggle();
  query(root, '.controls__prev').onclick = () => player.previous();
  query(root, '.controls__next').onclick = () => player.next();
  document.addEventListener('keydown', e => {
    if (!active || e.target instanceof HTMLInputElement) return;
    if (e.key === 'ArrowLeft') player.previous();
    else if (e.key === 'ArrowRight') player.next();
    else if (e.key === ' ' && !(e.target instanceof HTMLButtonElement)) { e.preventDefault(); player.toggle(); }
  });

  // Settings panel
  const togglePanel = (open: boolean) => { panel.hidden = !open; gear.setAttribute('aria-expanded', String(open)); wake(); };
  gear.onclick = () => togglePanel(panel.hidden !== false);
  document.addEventListener('keydown', e => { if (e.key === 'Escape') togglePanel(false); });

  showMap.checked = settings.showMap;
  showMap.onchange = () => { settings.showMap = showMap.checked; saveSettings(settings); applyMapVisibility(); };

  // Photo display: Ken Burns motion, and fill the frame (crop) vs. fit inside it (bars)
  const motion = query<HTMLInputElement>(root, '.settings__motion'), fill = query<HTMLInputElement>(root, '.settings__fill');
  const applyFill = () => root.classList.toggle('slideshow--fit', !settings.fill);
  applyFill();
  motion.checked = settings.motion;
  fill.checked = settings.fill;
  motion.onchange = () => { settings.motion = motion.checked; saveSettings(settings); player.applyMotionSetting(); };

  const repeat = query<HTMLInputElement>(root, '.settings__repeat');
  repeat.checked = settings.repeat;
  repeat.onchange = () => { settings.repeat = repeat.checked; saveSettings(settings); };
  fill.onchange = () => { settings.fill = fill.checked; saveSettings(settings); applyFill(); player.applyFillSetting(); };

  query(root, '.settings__restart').onclick = () => { togglePanel(false); if (active) player.restart(); };
  query(root, '.settings__change').onclick = () => {
    togglePanel(false);
    handle.stop();
    options.onChangeSource?.();
  };

  // Auto-hide: after a few seconds without input, fade out the overlays (and the cursor over the photo).
  // They stay while paused, while the settings panel is open, and while the pointer or keyboard focus is on one.
  const OVERLAYS = '.controls, .timeline, .settings__toggle, .settings__panel, .slideshow__back';
  let idleTimer: number | undefined;
  let pointerOnOverlay = false;
  function wake(): void {
    root.classList.remove('slideshow--idle');
    clearTimeout(idleTimer);
    idleTimer = window.setTimeout(() => {
      const focusOnOverlay = !!document.activeElement?.closest(OVERLAYS) && document.activeElement?.matches(':focus-visible');
      if (active && playing && panel.hidden && !pointerOnOverlay && !focusOnOverlay) root.classList.add('slideshow--idle');
    }, OVERLAY_IDLE_MS);
  }
  root.addEventListener('pointermove', wake);
  root.addEventListener('pointerdown', wake);
  root.addEventListener('pointerover', e => { pointerOnOverlay = e.target instanceof Element && !!e.target.closest(OVERLAYS); });
  root.addEventListener('pointerleave', () => { pointerOnOverlay = false; });
  document.addEventListener('keydown', wake);

  const handle: SlideshowHandle = {
    play({ items, tracks }) {
      active = true;
      muteBtn.hidden = tracks.length === 0;
      music.start(tracks);
      timeline.setItems(items);
      player.start(items);
      wake();
    },
    stop() {
      active = false;
      player.stop();
      music.stop();
      wake();
    },
  };
  return handle;
}
