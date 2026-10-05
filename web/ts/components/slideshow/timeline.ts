// Vertical scrubber down the left of the photo frame: drag to move through the set.

import { TIMELINE_LABELS } from '../../config.js';
import type { MediaItem } from '../../media/media-types.js';
import { clamp, query } from '../../utils/dom.js';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** "2026-07-04 09:31:05" -> "Jul 04, 2026". Done by hand to avoid time-zone shifts. */
export function formatDate(taken: string | undefined): string | null {
  const m = taken ? /^(\d{4})-(\d{2})-(\d{2})/.exec(taken) : null;
  const month = m ? MONTHS[Number(m[2]) - 1] : undefined;
  return m && month ? `${month} ${m[3]}, ${m[1]}` : null;
}

export class Timeline {
  private rail: HTMLElement;
  private thumb: HTMLElement;
  private labels: HTMLElement;
  private items: MediaItem[] = [];
  private index = 0;
  private dragging = false;

  /** onScrub fires for each new position while dragging; onRelease once when the drag ends. */
  constructor(private root: HTMLElement, private onScrub: (index: number) => void, private onRelease: () => void) {
    this.rail = query(root, '.timeline__rail');
    this.thumb = query(root, '.timeline__thumb');
    this.labels = query(root, '.timeline__labels');
    const rail = this.rail;
    rail.addEventListener('pointerdown', e => {
      if (e.button !== 0) return;
      e.preventDefault();
      rail.focus();
      rail.setPointerCapture(e.pointerId);
      this.dragging = true;
      root.classList.add('timeline--dragging');
      this.scrubAt(e.clientY);
    });
    rail.addEventListener('pointermove', e => { if (this.dragging) this.scrubAt(e.clientY); });
    const end = () => {
      if (!this.dragging) return;
      this.dragging = false;
      root.classList.remove('timeline--dragging');
      this.onRelease();
    };
    rail.addEventListener('pointerup', end);
    rail.addEventListener('pointercancel', end);
    rail.addEventListener('keydown', e => {
      const last = this.items.length - 1;
      const to = e.key === 'ArrowUp' ? this.index - 1 : e.key === 'ArrowDown' ? this.index + 1
        : e.key === 'Home' ? 0 : e.key === 'End' ? last : null;
      if (to === null) return;
      e.preventDefault();
      this.scrub(clamp(to, 0, last));
      this.onRelease();
    });
  }

  setItems(items: MediaItem[]): void {
    this.items = items;
    this.root.hidden = items.length < 2;
    const last = items.length - 1;
    this.rail.setAttribute('aria-valuemin', '1');
    this.rail.setAttribute('aria-valuemax', String(items.length));
    // First and last items, plus the rest spread evenly between them.
    const count = Math.min(TIMELINE_LABELS, items.length);
    this.labels.replaceChildren(...Array.from({ length: count }, (_, k) => {
      const i = count > 1 ? Math.round(k * last / (count - 1)) : 0;
      const label = document.createElement('span');
      label.textContent = this.describe(i);
      label.style.top = `${count > 1 ? k / (count - 1) * 100 : 0}%`;
      return label;
    }));
    this.setIndex(0);
  }

  /** Moves the elevator to match the item on screen. */
  setIndex(i: number): void {
    this.index = i;
    const last = this.items.length - 1;
    this.thumb.style.top = `${last > 0 ? i / last * 100 : 0}%`;
    this.rail.setAttribute('aria-valuenow', String(i + 1));
    this.rail.setAttribute('aria-valuetext', this.describe(i));
  }

  private describe(i: number): string {
    return formatDate(this.items[i]?.taken) ?? `#${i + 1}`; // no date known: fall back to position
  }

  private scrubAt(clientY: number): void {
    const rect = this.rail.getBoundingClientRect();
    const frac = clamp((clientY - rect.top) / rect.height, 0, 1);
    this.scrub(Math.round(frac * (this.items.length - 1)));
  }

  private scrub(i: number): void {
    if (i === this.index) return;
    this.setIndex(i);
    this.onScrub(i);
  }
}
