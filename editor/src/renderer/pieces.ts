// A gun's original pieces (the transforms under a part, PartTransform.Path): a readable name guessed from the game's node
// name (Front_Flash -> 枪口焰, a_ChargingHandle -> 拉机柄), the original name kept for hovering; and whether the author
// changed one (hidden, moved, or given another model), which is what the parts tree lists.
import type { SkinDocument } from './protocol';
import { partTransformOf } from './store';
import { t } from './i18n';

// Words looked for in a node name, longer ones first so a compound wins over its parts; each has a `piece.<word>` text.
const WORDS = [
  'charginghandle',
  'flashlight',
  'flashhider',
  'speedloader',
  'magazine',
  'cylinder',
  'trigger',
  'hammer',
  'muzzle',
  'barrel',
  'safety',
  'battery',
  'antenna',
  'nozzle',
  'pommel',
  'scope',
  'sight',
  'flash',
  'slide',
  'guard',
  'stock',
  'laser',
  'light',
  'screen',
  'handle',
  'shell',
  'bullet',
  'spring',
  'button',
  'cover',
  'blade',
  'lens',
  'bolt',
  'grip',
  'rail',
  'tank',
  'mag',
  'cap',
] as const;

/** The piece's name for people: the words found in the node's own name, else the node name itself. */
export function pieceLabel(path: string): string {
  const name = path.split('/').pop() ?? path;
  const plain = name.toLowerCase().replace(/[^a-z]/g, '');
  const found: { at: number; end: number; word: string }[] = [];
  for (const word of WORDS) {
    for (let at = plain.indexOf(word); at >= 0; at = plain.indexOf(word, at + 1)) {
      if (!found.some((f) => at < f.end && at + word.length > f.at)) found.push({ at, end: at + word.length, word });
    }
  }
  if (!found.length) return name;
  const words = [...new Set(found.sort((a, b) => a.at - b.at).map((f) => t(`piece.${f.word}` as 'piece.mag')))].slice(0, 2);
  const number = /(\d+)$/.exec(name)?.[1];
  return words.join(t('piece.join')) + (number ? ` ${number}` : '');
}

/** Whether the author changed this piece: hidden it, moved, turned or scaled it, or gave it another model. */
export function pieceChanged(doc: SkinDocument, part: string, path: string): boolean {
  const x = partTransformOf(doc, part, path);
  return !!x && (x.visible === false || x.position != null || x.rotation != null || x.scale != null || !!x.mesh);
}
