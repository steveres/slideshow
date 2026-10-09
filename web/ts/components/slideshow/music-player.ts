// Plays the album's music back to back in random order, never the same track twice in a row.

import type { Track } from '../../media/media-types.js';
import { sleep } from '../../utils/dom.js';

export class MusicPlayer {
  private audio = new Audio();
  private tracks: Track[] = [];
  private current = -1;
  private url: string | null = null;
  private failures = 0;      // consecutive unplayable tracks
  private session = 0;       // bumping this abandons a track still loading
  private waitingForGesture = false;
  private fadeTimer: number | undefined;
  private faded = false;     // faded out at the end of the slideshow; resume() starts again

  constructor() {
    this.audio.onended = () => void this.playRandom();
    this.audio.onerror = () => { if (this.url) void this.trackFailed(); };
  }

  start(tracks: Track[]): void {
    this.stop();
    this.audio.volume = 1;
    this.tracks = tracks;
    this.failures = 0;
    if (tracks.length) void this.playRandom();
  }

  stop(): void {
    clearInterval(this.fadeTimer);
    this.faded = false;
    this.session++;
    this.audio.pause();
    this.release();
    this.tracks = [];
    this.current = -1;
  }

  setMuted(muted: boolean): void { this.audio.muted = muted; }

  /** Lowers the volume to silence over `ms`, then pauses (the slideshow has ended). */
  fadeOut(ms: number): void {
    clearInterval(this.fadeTimer);
    const from = this.audio.volume, start = performance.now();
    this.fadeTimer = window.setInterval(() => {
      const t = Math.min(1, (performance.now() - start) / ms);
      this.audio.volume = from * (1 - t);
      if (t < 1) return;
      clearInterval(this.fadeTimer);
      this.audio.pause();
      this.faded = true;
    }, 50);
  }

  /** After a fade-out (or during one): full volume again, with a new track if it had stopped. */
  resume(): void {
    clearInterval(this.fadeTimer);
    this.audio.volume = 1;
    if (this.faded && this.tracks.length) {
      this.faded = false;
      this.current = -1;
      void this.playRandom();
    }
  }

  private async playRandom(): Promise<void> {
    const session = ++this.session;
    const n = this.tracks.length;
    if (!n) return;
    // With more than one track, pick among the others.
    const pick = this.current < 0 ? Math.floor(Math.random() * n)
      : n === 1 ? 0 : (this.current + 1 + Math.floor(Math.random() * (n - 1))) % n;
    this.current = pick;
    try {
      const file = await this.tracks[pick].getFile();
      if (session !== this.session) return;
      this.release();
      this.url = URL.createObjectURL(file);
      this.audio.src = this.url;
      await this.audio.play();
      this.failures = 0;
    } catch (err) {
      if (session !== this.session) return;
      if ((err as DOMException).name === 'NotAllowedError') this.playOnGesture(); // browser wants a click first
      else void this.trackFailed();
    }
  }

  private async trackFailed(): Promise<void> {
    console.warn(`Could not play ${this.tracks[this.current]?.name}`);
    if (++this.failures >= this.tracks.length) return; // nothing playable; give up quietly
    await sleep(250);
    void this.playRandom();
  }

  /** Browsers block sound until the user interacts with the page; start on the first click or key. */
  private playOnGesture(): void {
    if (this.waitingForGesture) return;
    this.waitingForGesture = true;
    const session = this.session;
    const resume = () => {
      document.removeEventListener('pointerdown', resume);
      document.removeEventListener('keydown', resume);
      this.waitingForGesture = false;
      if (session === this.session) void this.audio.play().catch(() => undefined);
    };
    document.addEventListener('pointerdown', resume);
    document.addEventListener('keydown', resume);
  }

  private release(): void {
    if (this.url) {
      const url = this.url;
      this.url = null; // cleared first so the resulting error event isn't treated as a bad track
      this.audio.removeAttribute('src');
      this.audio.load();
      URL.revokeObjectURL(url);
    }
  }
}
