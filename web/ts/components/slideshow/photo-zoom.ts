// Wheel-zoom (towards the cursor) and drag-pan for the photo currently on screen.

import { MAX_PHOTO_ZOOM } from '../../config.js';
import { clamp } from '../../utils/dom.js';

export class PhotoZoom {
  private scale = 1;
  private tx = 0;   // translation in px; screen = t + scale * point
  private ty = 0;
  private drag: { x: number; y: number } | null = null;

  constructor(private frame: HTMLElement, private media: () => HTMLElement | null, private onZoomIn: () => void) {
    frame.addEventListener('wheel', e => this.onWheel(e), { passive: false });
    frame.addEventListener('dblclick', e => { if (!this.onControls(e)) this.reset(); });
    frame.addEventListener('pointerdown', e => {
      if (this.scale === 1 || e.button !== 0 || this.onControls(e)) return;
      e.preventDefault(); // stop the browser's own image drag
      this.drag = { x: e.clientX - this.tx, y: e.clientY - this.ty };
      frame.setPointerCapture(e.pointerId);
    });
    frame.addEventListener('pointermove', e => {
      if (!this.drag) return;
      this.tx = e.clientX - this.drag.x;
      this.ty = e.clientY - this.drag.y;
      this.apply();
    });
    const endDrag = () => { this.drag = null; };
    frame.addEventListener('pointerup', endDrag);
    frame.addEventListener('pointercancel', endDrag);
  }

  reset(): void {
    this.scale = 1;
    this.tx = this.ty = 0;
    this.drag = null;
    this.apply();
  }

  private onControls(e: Event): boolean {
    return e.target instanceof Element && e.target.closest('.controls, .timeline') !== null;
  }

  private onWheel(e: WheelEvent): void {
    if (this.onControls(e)) return;
    e.preventDefault();
    const delta = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY; // some browsers report lines, not pixels
    const next = clamp(this.scale * Math.exp(-delta * 0.0015), 1, MAX_PHOTO_ZOOM);
    if (next === this.scale) return;
    if (this.scale === 1) this.onZoomIn();
    // Keep the point under the cursor fixed while the scale changes.
    const rect = this.frame.getBoundingClientRect();
    const cx = e.clientX - rect.left, cy = e.clientY - rect.top;
    this.tx = cx - (cx - this.tx) * next / this.scale;
    this.ty = cy - (cy - this.ty) * next / this.scale;
    this.scale = next;
    this.apply();
  }

  private apply(): void {
    const w = this.frame.clientWidth, h = this.frame.clientHeight;
    this.tx = clamp(this.tx, w - w * this.scale, 0); // never pull the frame's edge into view
    this.ty = clamp(this.ty, h - h * this.scale, 0);
    const el = this.media();
    if (el) {
      el.style.transformOrigin = '0 0';
      el.style.transform = this.scale === 1 ? '' : `translate(${this.tx}px, ${this.ty}px) scale(${this.scale})`;
    }
    this.frame.classList.toggle('slideshow__photos--zoomed', this.scale > 1);
  }
}
