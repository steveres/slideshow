// "Ken Burns" motion for photos: a gentle zoom or pan over the time a photo is on screen, like the
// Windows Photos slideshow. Each photo gets a random move suited to its shape and a random pace.

/** How far zooms go (12% bigger at the close end). */
const ZOOM = 1.12;
/** Pans drift about as far as a zoom moves (7% of the frame), on a slightly enlarged photo so there's
 *  room to travel without showing an edge. */
const PAN_SCALE = 1.08;
const PAN_SHIFT = '3.5%'; // each way; stays inside the extra room PAN_SCALE gives
/** Paces: steady, or starting a little faster and easing off. */
const PACES = ['linear', 'cubic-bezier(0.25, 0.6, 0.4, 1)'];

const pick = <T>(choices: readonly T[]): T => choices[Math.floor(Math.random() * choices.length)];

type Move = 'zoom-in' | 'zoom-out' | 'pan-left' | 'pan-right' | 'pan-up' | 'pan-down';

/**
 * Starts the motion on `img` (already decoded) shown in `frame`, lasting `durationMs`; returns the
 * running animation so the player can pause, resume or cancel it. Photos taller than the frame pan
 * up or down, wider ones left or right.
 */
export function startKenBurns(img: HTMLImageElement, frame: HTMLElement, durationMs: number): Animation {
  const frameAspect = frame.clientWidth / Math.max(1, frame.clientHeight);
  const photoAspect = img.naturalWidth / Math.max(1, img.naturalHeight);
  // Taller than the frame (portrait photos, mostly): the crop is at the top and bottom, so pan vertically.
  const tall = photoAspect < frameAspect;
  const move: Move = pick(tall ? ['zoom-in', 'zoom-out', 'pan-up', 'pan-down'] : ['zoom-in', 'zoom-out', 'pan-left', 'pan-right']);

  img.style.transformOrigin = 'center';
  return img.animate(keyframes(move), { duration: durationMs, easing: pick(PACES), fill: 'forwards' });
}

function keyframes(move: Move): Keyframe[] {
  switch (move) {
    case 'zoom-in':
      return [{ transform: 'scale(1)' }, { transform: `scale(${ZOOM})` }];
    case 'zoom-out':
      return [{ transform: `scale(${ZOOM})` }, { transform: 'scale(1)' }];
    // "pan-right" = the view travels right across the photo (the photo itself slides left).
    case 'pan-right':
      return [{ transform: `scale(${PAN_SCALE}) translateX(${PAN_SHIFT})` }, { transform: `scale(${PAN_SCALE}) translateX(-${PAN_SHIFT})` }];
    case 'pan-left':
      return [{ transform: `scale(${PAN_SCALE}) translateX(-${PAN_SHIFT})` }, { transform: `scale(${PAN_SCALE}) translateX(${PAN_SHIFT})` }];
    case 'pan-down':
      return [{ transform: `scale(${PAN_SCALE}) translateY(${PAN_SHIFT})` }, { transform: `scale(${PAN_SCALE}) translateY(-${PAN_SHIFT})` }];
    case 'pan-up':
      return [{ transform: `scale(${PAN_SCALE}) translateY(-${PAN_SHIFT})` }, { transform: `scale(${PAN_SCALE}) translateY(${PAN_SHIFT})` }];
  }
}
