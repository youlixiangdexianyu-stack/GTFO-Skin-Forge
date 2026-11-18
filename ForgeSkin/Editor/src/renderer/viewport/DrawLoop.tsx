// When the 3D view draws. Its canvas draws on demand (Canvas frameloop="demand") and this decides the demand: every frame
// for a second after something it shows changed or the pointer or a key worked on it; 60 times a second while the skin has
// something that moves (and the window has the focus); else four times a second, which also shows a change nothing
// announced here (a picture that finished loading) within a quarter of a second.
import { useThree } from '@react-three/fiber';
import { useEffect, useRef } from 'react';
import { isGifDecal } from '../livery';
import type { SkinDocument } from '../protocol';
import { useStore } from '../store';
import { ATTACHMENT_EVENT, SCENE_EVENT } from './refs';

let busyUntil = 0;
/** The view is about to change: it draws every frame for the next second. */
export const wake = () => void (busyUntil = performance.now() + 1000);

/** The skin has something that moves without the author touching it: code effects, animated layers, attachments (screens,
 * charms, holograms and their effects) or an animated decal. */
const moves = (doc: SkinDocument) =>
  !!doc.codeEffects?.length || !!doc.attachments?.length || !!doc.materials?.some((m) => m?.layers?.length || m?.livery?.some((l) => l && l.visible !== false && isGifDecal(l)));

/** What of the editor's state the view draws. */
const DRAWN = ['doc', 'selection', 'hover', 'preview', 'previewLoading', 'prefs', 'fileEpoch', 'snapToSurface', 'weaponId', 'subParts'] as const;

export function DrawLoop() {
  const invalidate = useThree((s) => s.invalidate);
  const gl = useThree((s) => s.gl);
  const animated = useStore((s) => s.prefs.autoRotate || moves(s.doc));
  const live = useRef(animated);
  live.current = animated;

  useEffect(() => {
    wake();
    const el = gl.domElement;
    const onView = ['pointerdown', 'pointermove', 'wheel'];
    const anywhere = ['pointerup', 'keydown', 'keyup', 'resize', SCENE_EVENT, ATTACHMENT_EVENT];
    onView.forEach((e) => el.addEventListener(e, wake, { capture: true, passive: true }));
    anywhere.forEach((e) => window.addEventListener(e, wake, true));
    const stop = useStore.subscribe((s, old) => DRAWN.some((k) => s[k] !== old[k]) && wake());
    let frame = 0;
    let last = 0;
    const tick = (now: number) => {
      const gap = now < busyUntil ? 0 : live.current && document.hasFocus() ? 1000 / 61 : 250;
      if (now - last >= gap) {
        last = now;
        invalidate();
      }
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => {
      cancelAnimationFrame(frame);
      stop();
      onView.forEach((e) => el.removeEventListener(e, wake, { capture: true }));
      anywhere.forEach((e) => window.removeEventListener(e, wake, true));
    };
  }, [gl, invalidate]);
  return null;
}
