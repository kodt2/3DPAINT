#!/usr/bin/env node
/**
 * Inspects a GLB file (step 3c.5): container header, glTF JSON summary and
 * the compression extensions in use.
 *
 * Usage:
 *   node scripts/inspect-glb.mjs <file.glb>
 *
 * Prints (machine-readable-ish, for humans and CI logs):
 *   - header: magic, glTF version, declared vs actual size;
 *   - extensionsUsed / extensionsRequired;
 *   - whether the viewer (step 3c.5) needs a decoder: draco / meshopt / ktx2;
 *   - geometry summary: meshes, vertices, triangles, textures, materials.
 *
 * Pure node, no dependencies: the GLB container is parsed by hand and only
 * the JSON chunk is needed for the extension list.
 */
import { readFileSync } from 'node:fs';

const GLB_MAGIC = 0x46546c67; // 'glTF'
const JSON_CHUNK = 0x4e4f534a; // 'JSON'
const BIN_CHUNK = 0x004e4942; // 'BIN\0'

const DRACO = 'KHR_draco_mesh_compression';
const MESHOPT = 'EXT_meshopt_compression';
const KTX2 = 'KHR_texture_basisu';

function fail(message) {
  console.error(`inspect-glb: ${message}`);
  process.exit(1);
}

const path = process.argv[2];
if (!path) {
  fail('usage: node scripts/inspect-glb.mjs <file.glb>');
}

const bytes = readFileSync(path);
const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
if (bytes.byteLength < 12) {
  fail('file is shorter than the 12-byte GLB header');
}

const magic = view.getUint32(0, true);
const version = view.getUint32(4, true);
const declaredLength = view.getUint32(8, true);

console.log('header:');
console.log(`  magic: 0x${magic.toString(16).padStart(8, '0')} ${magic === GLB_MAGIC ? "'glTF' ok" : 'NOT glTF'}`);
console.log(`  version: ${version} ${version === 2 ? 'ok' : 'unexpected'}`);
console.log(
  `  size: declared=${declaredLength} actual=${bytes.byteLength}`
    + (declaredLength === bytes.byteLength ? ' (consistent)' : ' (MISMATCH)'),
);
if (magic !== GLB_MAGIC || version !== 2) {
  fail('not a glTF 2.0 binary container');
}

let offset = 12;
let jsonText = null;
let binSize = null;
while (offset + 8 <= bytes.byteLength) {
  const chunkLength = view.getUint32(offset, true);
  const chunkType = view.getUint32(offset + 4, true);
  offset += 8;
  if (offset + chunkLength > bytes.byteLength) {
    fail(`chunk overruns the file (${chunkType === JSON_CHUNK ? 'JSON' : chunkType === BIN_CHUNK ? 'BIN' : 'unknown'})`);
  }
  if (chunkType === JSON_CHUNK) {
    jsonText = new TextDecoder().decode(bytes.subarray(offset, offset + chunkLength));
  } else if (chunkType === BIN_CHUNK) {
    binSize = chunkLength;
  } else {
    console.log(`  chunk: unknown type 0x${chunkType.toString(16)} (length ${chunkLength})`);
  }
  offset += chunkLength;
}
if (!jsonText) {
  fail('no JSON chunk found');
}

let json;
try {
  json = JSON.parse(jsonText);
} catch (error) {
  fail(`JSON chunk is not valid JSON: ${error.message}`);
}

const used = Array.isArray(json.extensionsUsed) ? json.extensionsUsed : [];
const required = Array.isArray(json.extensionsRequired) ? json.extensionsRequired : [];
const needs = {
  draco: used.includes(DRACO) || required.includes(DRACO),
  meshopt: used.includes(MESHOPT) || required.includes(MESHOPT),
  ktx2: used.includes(KTX2) || required.includes(KTX2),
};

console.log('chunks:');
console.log(`  json: ok`);
console.log(`  bin: ${binSize ?? 'none'}`);
console.log('extensions:');
console.log(`  used: ${used.length ? used.join(', ') : '(none)'}`);
console.log(`  required: ${required.length ? required.join(', ') : '(none)'}`);
console.log('viewer decoders needed (step 3c.5):');
console.log(`  draco: ${needs.draco}`);
console.log(`  meshopt: ${needs.meshopt}`);
console.log(`  ktx2: ${needs.ktx2}`);

// Geometry summary from the JSON (accessors), no decoding involved.
const meshes = Array.isArray(json.meshes) ? json.meshes : [];
const accessors = Array.isArray(json.accessors) ? json.accessors : [];
const bufferViews = Array.isArray(json.bufferViews) ? json.bufferViews : [];
let vertices = 0;
let triangles = 0;
for (const mesh of meshes) {
  for (const primitive of Array.isArray(mesh?.primitives) ? mesh.primitives : []) {
    const positionAccessor = accessors[primitive.attributes?.POSITION];
    const indexAccessor = primitive.indices !== undefined ? accessors[primitive.indices] : undefined;
    if (positionAccessor) {
      vertices += positionAccessor.count;
    }
    if (indexAccessor) {
      triangles += Math.floor(indexAccessor.count / 3);
    } else if (positionAccessor) {
      triangles += Math.floor(positionAccessor.count / 3);
    }
  }
}
const textures = Array.isArray(json.textures) ? json.textures.length : 0;
const materials = Array.isArray(json.materials) ? json.materials.length : 0;
console.log('geometry (from glTF JSON, before decoding):');
console.log(`  meshes: ${meshes.length}`);
console.log(`  vertices: ${vertices}`);
console.log(`  triangles: ${triangles}`);
console.log(`  materials: ${materials}, textures: ${textures}`);
console.log(`  bufferViews: ${bufferViews.length}`);
