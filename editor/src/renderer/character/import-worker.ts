// The character import runs here, off the page: the site's own converter and VRM reading (Web/src/forge/character-import.ts) turn the
// file into the character format's model files and the filled-in bones, springs and colliders. Refusals come back as the converter's codes.
import { importCharacter, type CharacterImport } from '../../../../../../Web/src/forge/character-import';
import { ImportedModelError } from '../../../../../../Web/src/forge/imported-model-format';
import { reencodeAsPng } from '../../../../../../Web/src/forge/lib/png-reencode';

export type ImportReply = { result: CharacterImport } | { code: string; params: Record<string, string | number>; message: string };

const scope = globalThis as unknown as {
  onmessage: (event: MessageEvent<Uint8Array>) => void;
  postMessage: (reply: ImportReply, transfer?: Transferable[]) => void;
};
scope.onmessage = ({ data }) => {
  void importCharacter(data, reencodeAsPng).then(
    (result) => scope.postMessage({ result }, [...new Set([result.model.mesh.buffer, ...result.model.textures.map((x) => x.buffer)])] as ArrayBuffer[]),
    (e: unknown) =>
      scope.postMessage(
        e instanceof ImportedModelError ? { code: e.code, params: e.params, message: e.message } : { code: 'unreadable', params: {}, message: e instanceof Error ? e.message : String(e) },
      ),
  );
};
