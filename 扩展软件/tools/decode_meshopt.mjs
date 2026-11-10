import fs from 'node:fs/promises';
import path from 'node:path';
import { MeshoptDecoder } from './node_modules/meshoptimizer/index.module.js';

const [, , inputPath, outputPath] = process.argv;
if (!inputPath || !outputPath) {
  throw new Error('Usage: node decode_meshopt.mjs <input.glb> <output.glb>');
}

const source = await fs.readFile(inputPath);
const magic = source.toString('ascii', 0, 4);
if (magic !== 'glTF') {
  throw new Error('Input is not a GLB file');
}

let offset = 12;
let document = null;
let binary = null;
while (offset < source.length) {
  const chunkLength = source.readUInt32LE(offset);
  const chunkType = source.readUInt32LE(offset + 4);
  offset += 8;
  const data = source.subarray(offset, offset + chunkLength);
  offset += chunkLength;
  if (chunkType === 0x4e4f534a) {
    document = JSON.parse(data.toString('utf8').trim());
  } else if (chunkType === 0x004e4942) {
    binary = data;
  }
}
if (!document || !binary) {
  throw new Error('GLB is missing JSON or BIN data');
}

await MeshoptDecoder.ready;
const outputChunks = [];
let outputOffset = 0;
for (const view of document.bufferViews ?? []) {
  const extension = view.extensions?.EXT_meshopt_compression;
  if (!extension) {
    continue;
  }
  const decoded = new Uint8Array(view.byteLength);
  const compressed = binary.subarray(
    extension.byteOffset,
    extension.byteOffset + extension.byteLength,
  );
  MeshoptDecoder.decodeGltfBuffer(
    decoded,
    extension.count,
    extension.byteStride,
    compressed,
    extension.mode,
    extension.filter,
  );
  outputChunks.push(decoded);
  view.buffer = 0;
  view.byteOffset = outputOffset;
  delete view.byteStride;
  delete view.extensions;
  outputOffset += decoded.byteLength;
  const padding = (4 - (outputOffset % 4)) % 4;
  if (padding) {
    outputChunks.push(new Uint8Array(padding));
    outputOffset += padding;
  }
}

const outputBinary = Buffer.concat(outputChunks);
document.buffers = [{ byteLength: outputBinary.length }];
document.extensionsUsed = (document.extensionsUsed ?? []).filter(
  (name) => name !== 'EXT_meshopt_compression',
);
document.extensionsRequired = (document.extensionsRequired ?? []).filter(
  (name) => name !== 'EXT_meshopt_compression',
);
if (document.extensionsUsed.length === 0) {
  delete document.extensionsUsed;
}
if (document.extensionsRequired.length === 0) {
  delete document.extensionsRequired;
}

const jsonData = Buffer.from(JSON.stringify(document), 'utf8');
const jsonPadding = (4 - (jsonData.length % 4)) % 4;
const binaryPadding = (4 - (outputBinary.length % 4)) % 4;
const jsonChunk = Buffer.concat([jsonData, Buffer.alloc(jsonPadding, 0x20)]);
const binaryChunk = Buffer.concat([outputBinary, Buffer.alloc(binaryPadding)]);
const totalLength = 12 + 8 + jsonChunk.length + 8 + binaryChunk.length;
const header = Buffer.alloc(12);
header.write('glTF', 0, 'ascii');
header.writeUInt32LE(2, 4);
header.writeUInt32LE(totalLength, 8);
const jsonHeader = Buffer.alloc(8);
jsonHeader.writeUInt32LE(jsonChunk.length, 0);
jsonHeader.writeUInt32LE(0x4e4f534a, 4);
const binaryHeader = Buffer.alloc(8);
binaryHeader.writeUInt32LE(binaryChunk.length, 0);
binaryHeader.writeUInt32LE(0x004e4942, 4);

await fs.mkdir(path.dirname(outputPath), { recursive: true });
await fs.writeFile(
  outputPath,
  Buffer.concat([header, jsonHeader, jsonChunk, binaryHeader, binaryChunk]),
);
