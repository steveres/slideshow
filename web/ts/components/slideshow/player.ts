// Plays the slides: cross-fades between two layers, auto-advances, and keeps the map in step.

import { BACKDROP_CROP, FADE_MS } from '../../config.js';
import { readLocation } from '../../media/gps.js';
import type { LatLon, MediaItem, MediaKind } from '../../media/media-types.js';
import type { Settings } from '../../utils/settings.js';
import { startKenBurns } from './ken-burns.js';
import type { MapView } from './map-view.js';

/** A portrait photo that filling `frame` would crop by more than BACKDROP_CROP of its height. */
function tooTallFor(img: HTMLImageElement, frame: HTMLElement): boolean {
  const frameAspect = frame.clientWidth / Math.max(1, frame.clientHeight);
  const photoAspect = img.naturalWidth / Math.max(1, img.naturalHeight);
  return photoAspect < 1 && photoAspect < frameAspect && 1 - photoAspect / frameAspect > BACKDROP_CROP;
}

export class Player {
  /** Called whenever play/pause state changes, so the UI can update its button. */
  onState: (playing: boolean) => void = () => {};
  /** Called when a new item has been put on screen. */
  onShow: () => void = () => {};
  /** Called when the current position changes. */
  onIndex: (index: number) => void = () => {};
  /** Called when the slideshow reaches its end with Repeat off (it stays on the last slide). */
  onEnded: () => void = () => {};

  private items: MediaItem[] = [];
  private locs: (LatLon | null | undefined)[] = []; // per item; undefined = not read yet
  private index = 0;
  private generation = 0;            // bumping this abandons any navigation still loading
  private playing = true;
  private ended = false;             // stopped on the last slide (Repeat off); Play starts again from the top
  private scrubbing = false;         // timeline is being dragged: no map updates, no auto-advance
  private timer: number | undefined; // pending auto-advance
  private shownAt = 0;               // when the current image's display time started
  private video: HTMLVideoElement | null = null; // current item, if it is a video
  private front = 0;                 // which layer is currently visible
  private urls: (string | null)[] = [null, null];
  private motions: (Animation | null)[] = [null, null]; // Ken Burns motion per layer

  constructor(private layers: HTMLElement[], private map: MapView, private settings: Settings) {}

  start(items: MediaItem[]): void {
    this.items = items;
    this.locs = [];
    this.ended = false;
    this.setPlaying(true);
    this.restart();
  }

  restart(): void {
    this.scrubbing = false;
    this.map.reset();
    void this.go(0, true);
  }

  stop(): void {
    this.generation++;
    this.ended = false;
    clearTimeout(this.timer);
    this.scrubbing = false;
    this.items = [];
    this.video = null;
    this.map.reset();
    this.layers.forEach((layer, i) => { layer.classList.remove('slideshow__layer--visible'); this.clearLayer(i); });
  }

  /** Next slide; after the last one, the first again (Repeat on) or nowhere (Repeat off). */
  next(): void {
    if (!this.items.length) return;
    const atEnd = this.index + 1 >= this.items.length;
    if (atEnd && !this.settings.repeat) return;
    void this.go(atEnd ? 0 : this.index + 1, true);
  }
  previous(): void { if (this.index > 0) void this.go(this.index - 1, false); }
  toggle(): void { this.setPlaying(!this.playing); }

  setPlaying(playing: boolean): void {
    if (playing && this.ended) { // Play after the end: from the top
      this.ended = false;
      this.playing = true;
      this.onState(true);
      this.restart();
      return;
    }
    this.playing = playing;
    this.onState(playing);
    if (playing) {
      this.shownAt = performance.now();
      this.schedule();
    } else {
      clearTimeout(this.timer);
      this.video?.pause();
    }
    for (const motion of this.motions) {
      if (!motion) continue;
      if (!playing) motion.pause();
      else if (motion.playState === 'paused') motion.play(); // (a finished one would restart)
    }
  }

  /** Applies the "Animate photos" setting to the photo on screen right away. */
  applyMotionSetting(): void {
    if (this.settings.motion) this.animate(this.front);
    else this.motions.forEach((motion, i) => { motion?.cancel(); this.motions[i] = null; });
  }

  /** The element holding the image or video on screen (the wheel/drag zoom moves this, the motion moves what's inside). */
  currentMedia(): HTMLElement | null { return this.layers[this.front].firstElementChild as HTMLElement | null; }

  /** Timeline drag: flip straight to item i, leaving the map alone until the drag ends. */
  scrubTo(i: number): void {
    this.scrubbing = true;
    if (i !== this.index && i >= 0 && i < this.items.length) void this.go(i, false);
  }

  /** Timeline released: bring the map up to date and carry on from here. */
  async endScrub(): Promise<void> {
    if (!this.scrubbing) return;
    this.scrubbing = false;
    const gen = this.generation, upTo = this.index;
    // The route needs the location of every item up to here, including ones jumped over.
    const missing = Array.from({ length: upTo + 1 }, (_, i) => i).filter(i => this.locs[i] === undefined);
    for (let k = 0; k < missing.length; k += 8) {
      await Promise.all(missing.slice(k, k + 8).map(async i => {
        const item = this.items[i];
        try { this.locs[i] = await this.locationOf(item); } catch { this.locs[i] = null; }
      }));
      if (gen !== this.generation || this.scrubbing) return; // moved on meanwhile
    }
    this.updateMap(false);
    this.animate(this.front); // shown still while dragging; moves from here
    this.shownAt = performance.now();
    this.schedule();
  }

  /** Re-arms the auto-advance (and stretches the current motion) after the duration setting changed. */
  retime(): void {
    this.motions[this.front]?.effect?.updateTiming({ duration: this.motionMs() });
    this.schedule();
  }

  /** Brings the map in line with the current item (e.g. after the panel was switched on). */
  syncMap(): void { this.updateMap(false); }

  /** Auto-advance: at the end, loop (Repeat on) or stop on the last slide (Repeat off). */
  private advance(): void {
    if (this.index + 1 >= this.items.length && !this.settings.repeat) {
      this.ended = true;
      this.onEnded(); // before the pause, so the music can fade rather than cut
      this.setPlaying(false);
      return;
    }
    this.next();
  }

  /** Shows item i. `forward` is false when stepping back. */
  private async go(i: number, forward: boolean): Promise<void> {
    const gen = ++this.generation;
    this.ended = false; // stepping somewhere: Play continues from here
    const live = () => gen === this.generation;
    clearTimeout(this.timer);
    this.index = i;
    this.onIndex(i);
    const item = this.items[i];
    try {
      const file = await item.getFile();
      if (this.locs[i] === undefined) this.locs[i] = item.location !== undefined ? item.location : await readLocation(file, item.kind);
      if (!live() || !(await this.present(file, item.kind, live))) return;
      this.prefetch(i + 1);
      if (this.scrubbing) return; // endScrub() takes it from here
      this.updateMap(forward && !!this.locs[i]);
      this.shownAt = performance.now();
      this.schedule();
    } catch (err) {
      console.warn(`Skipping ${item.name}:`, err);
      if (!live()) return;
      // Step over the unplayable file in the direction of travel (delay avoids a hot loop).
      this.timer = window.setTimeout(() => (forward ? this.next() : this.previous()), 250);
    }
  }

  /** Where an item was taken: known by the source, or read from the file. */
  private async locationOf(item: MediaItem): Promise<LatLon | null> {
    return item.location !== undefined ? item.location : readLocation(await item.getFile(), item.kind);
  }

  /** Starts loading the next item so it's ready when its turn comes (sources cache what they load). */
  private prefetch(i: number): void {
    if (i < this.items.length) void this.items[i].getFile().catch(() => undefined);
  }

  /** The route is the located items from the first up to the current one. */
  private updateMap(grow: boolean): void {
    if (!this.settings.showMap || !this.items.length || this.layers[0].closest('.slideshow--no-map')) return;
    const path = this.locs.slice(0, this.index + 1).filter((l): l is LatLon => !!l);
    void this.map.update(path, grow);
  }

  private schedule(): void {
    clearTimeout(this.timer);
    if (!this.playing || this.scrubbing || !this.items.length) return;
    if (this.video) { void this.video.play().catch(() => undefined); return; } // advances when it ends
    const remaining = Math.max(0, this.settings.duration * 1000 - (performance.now() - this.shownAt));
    this.timer = window.setTimeout(() => this.advance(), remaining);
  }

  /** A photo moves from the start of its fade-in to the end of its fade-out. */
  private motionMs(): number { return this.settings.duration * 1000 + FADE_MS; }

  /**
   * Starts the Ken Burns motion on the photo in layer i (if it holds a photo and motion is on).
   * New photos always move, even while paused (stepping with Next/Prev); Pause freezes the one on screen.
   */
  private animate(i: number): void {
    const img = this.layers[i].querySelector<HTMLImageElement>('img.slideshow__photo');
    if (!img || !this.settings.motion || this.scrubbing) return;
    this.motions[i]?.cancel();
    this.motions[i] = startKenBurns(img, this.layers[i], this.motionMs());
  }

  /** Loads the file into the hidden layer, then cross-fades to it. False if superseded meanwhile. */
  private async present(file: File, kind: MediaKind, live: () => boolean): Promise<boolean> {
    const url = URL.createObjectURL(file);
    let el: HTMLElement;
    let video: HTMLVideoElement | null = null;
    try {
      if (kind === 'image') {
        const img = new Image();
        img.alt = '';
        img.src = url;
        await img.decode();
        el = img;
      } else {
        video = document.createElement('video');
        const v = video;
        v.muted = true;           // required for autoplay
        v.playsInline = true;
        v.src = url;
        await new Promise<void>((resolve, reject) => {
          v.onloadeddata = () => resolve();
          v.onerror = () => reject(new Error('unsupported video'));
        });
        v.onended = v.onerror = () => { if (this.video === v && this.playing) this.advance(); };
        el = v;
      }
    } catch (err) {
      URL.revokeObjectURL(url);
      throw err;
    }
    if (!live()) { URL.revokeObjectURL(url); return false; }

    const backIdx = 1 - this.front, oldIdx = this.front;
    this.clearLayer(backIdx);
    // The wrapper takes the wheel/drag zoom; the photo inside takes the Ken Burns motion.
    const holder = document.createElement('div');
    holder.className = 'slideshow__media';
    el.classList.add('slideshow__photo');
    holder.append(el);
    this.setBackdrop(holder, this.layers[1 - this.front]);
    this.layers[backIdx].replaceChildren(holder);
    this.urls[backIdx] = url;
    this.layers[backIdx].classList.add('slideshow__layer--visible');
    this.layers[oldIdx].classList.remove('slideshow__layer--visible');
    this.front = backIdx;
    this.animate(backIdx);
    this.video = video; // schedule() starts playback if we're playing
    this.onShow();

    // Free the outgoing media once it has faded, unless the layer was reused meanwhile.
    const oldUrl = this.urls[oldIdx];
    setTimeout(() => { if (this.urls[oldIdx] === oldUrl && this.front !== oldIdx) this.clearLayer(oldIdx); }, FADE_MS);
    return true;
  }

  /**
   * Photos shown whole get a blurred copy of themselves behind them instead of black bars: all photos
   * with "Fill the screen" off, and portrait photos that filling the frame would crop too much.
   */
  private setBackdrop(holder: HTMLElement, frame: HTMLElement): void {
    const photo = holder.querySelector<HTMLImageElement>('img.slideshow__photo');
    const want = !!photo && (!this.settings.fill || tooTallFor(photo, frame));
    let backdrop = holder.querySelector<HTMLImageElement>('img.slideshow__backdrop');
    if (want && !backdrop) {
      backdrop = new Image();
      backdrop.alt = '';
      backdrop.className = 'slideshow__backdrop';
      backdrop.src = photo!.src;
      holder.prepend(backdrop);
    } else if (!want) backdrop?.remove();
    holder.classList.toggle('slideshow__media--backdrop', want);
  }

  /** Applies the "Fill the screen" setting to the photos on screen right away. */
  applyFillSetting(): void {
    this.layers.forEach(layer => {
      const holder = layer.querySelector<HTMLElement>('.slideshow__media');
      if (holder) this.setBackdrop(holder, layer);
    });
  }

  private clearLayer(i: number): void {
    this.motions[i]?.cancel();
    this.motions[i] = null;
    this.layers[i].querySelector('video')?.pause();
    this.layers[i].replaceChildren();
    const url = this.urls[i];
    if (url) URL.revokeObjectURL(url);
    this.urls[i] = null;
  }
}
