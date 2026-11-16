// A charm's settings (Attachment.charm) as the editor holds them. The rope itself is the game's: the backend moves it with the
// plugin's own code (charm.simulate) and the 3D view plays the frames back (Attachments.tsx).

export type CharmStyle = 'cord' | 'chain' | 'beads';

export interface CharmSettings {
  length?: number;
  segments?: number;
  weight?: number;
  damping?: number;
  stiffness?: number;
  style?: CharmStyle;
  thickness?: number;
  color?: number[];
  collide?: boolean;
}

/** Charm defaults (Core/SkinDocument.cs `Charm`): a short silver chain. */
export const CHARM_DEFAULTS = {
  length: 0.05,
  segments: 6,
  weight: 1,
  damping: 0.3,
  stiffness: 0.25,
  style: 'chain' as CharmStyle,
  thickness: 0.002,
  color: [0.8, 0.8, 0.82],
  collide: true,
};

export const MAX_SEGMENTS = 12,
  MAX_PER_GEAR = 4,
  MAX_BOXES = 12;
