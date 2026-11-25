/**
 * Compressed GLB fixtures (step 3c.5).
 *
 * Builds a unit cube with compressed geometry IN MEMORY using
 * @gltf-transform + draco3dgltf (devDependencies, Node-only):
 *   - buildDracoCubeGlb()        — KHR_draco_mesh_compression;
 *   - buildMeshoptCubeGlb()      — EXT_meshopt_compression;
 *   - buildMeshoptQuantCubeGlb() — EXT_meshopt_compression +
 *                                  KHR_mesh_quantization (quantized
 *                                  Int16 accessors).
 *
 * Same topology as src/test/glbfactory.ts (24 vertices, 12 triangles), so a
 * correctly decoded model must report the same counts in the viewer info
 * panel — that is the e2e acceptance for "the decoder works". No binary
 * fixtures are committed: every test builds the file on the fly.
 */
import { Document, NodeIO } from '@gltf-transform/core';
import {
  EXTMeshoptCompression,
  KHRDracoMeshCompression,
  KHRMeshQuantization,
} from '@gltf-transform/extensions';
import { quantize } from '@gltf-transform/functions';
import draco3d from 'draco3dgltf';
import { MeshoptEncoder } from 'meshoptimizer';

/** 6 faces x 4 vertices — same topology as glbfactory's CUBE_POSITIONS. */
const CUBE_POSITIONS = new Float32Array([
  // +Z
  -0.5, -0.5, 0.5, 0.5, -0.5, 0.5, 0.5, 0.5, 0.5, -0.5, 0.5, 0.5,
  // -Z
  0.5, -0.5, -0.5, -0.5, -0.5, -0.5, -0.5, 0.5, -0.5, 0.5, 0.5, -0.5,
  // +X
  0.5, -0.5, 0.5, 0.5, -0.5, -0.5, 0.5, 0.5, -0.5, 0.5, 0.5, 0.5,
  // -X
  -0.5, -0.5, -0.5, -0.5, -0.5, 0.5, -0.5, 0.5, 0.5, -0.5, 0.5, -0.5,
  // +Y
  -0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, -0.5, -0.5, 0.5, -0.5,
  // -Y
  -0.5, -0.5, -0.5, 0.5, -0.5, -0.5, 0.5, -0.5, 0.5, -0.5, -0.5, 0.5,
]);

const CUBE_INDICES = new Uint16Array([
  0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11, 12, 13, 14, 12,
  14, 15, 16, 17, 18, 16, 18, 19, 20, 21, 22, 20, 22, 23,
]);

let sharedIo: NodeIO | null = null;

async function getIo(): Promise<NodeIO> {
  if (sharedIo) {
    return sharedIo;
  }
  const io = new NodeIO()
    .registerExtensions([KHRDracoMeshCompression, EXTMeshoptCompression, KHRMeshQuantization])
    .registerDependencies({
      'draco3d.encoder': await draco3d.createEncoderModule(),
      'meshopt.encoder': MeshoptEncoder,
    });
  sharedIo = io;
  return io;
}

function buildCubeDocument(generation: string): Document {
  const doc = new Document();
  const root = doc.getRoot();
  const asset = root.getAsset();
  asset.version = '2.0';
  asset.generator = generation;

  const buffer = doc.createBuffer();
  // min/max are computed by the writer at write time.
  const position = doc
    .createAccessor()
    .setType('VEC3')
    .setArray(CUBE_POSITIONS)
    .setBuffer(buffer);
  const indices = doc
    .createAccessor()
    .setType('SCALAR')
    .setArray(CUBE_INDICES)
    .setBuffer(buffer);

  const primitive = doc
    .createPrimitive()
    .setAttribute('POSITION', position)
    .setIndices(indices);
  const mesh = doc.createMesh('test-cube').addPrimitive(primitive);
  const node = doc.createNode('test-cube').setMesh(mesh);
  // createScene() attaches to the root; a single scene becomes the default.
  doc.createScene('scene').addChild(node);
  return doc;
}

export async function buildDracoCubeGlb(): Promise<ArrayBuffer> {
  const io = await getIo();
  const doc = buildCubeDocument('3dpaint-e2e-draco');
  doc.createExtension(KHRDracoMeshCompression).setRequired(true);
  return toArrayBuffer(await io.writeBinary(doc));
}

export async function buildMeshoptCubeGlb(): Promise<ArrayBuffer> {
  const io = await getIo();
  const doc = buildCubeDocument('3dpaint-e2e-meshopt');
  doc.createExtension(EXTMeshoptCompression).setRequired(true);
  return toArrayBuffer(await io.writeBinary(doc));
}

export async function buildMeshoptQuantCubeGlb(): Promise<ArrayBuffer> {
  const io = await getIo();
  const doc = buildCubeDocument('3dpaint-e2e-meshopt-quant');
  await doc.transform(quantize());
  doc.createExtension(EXTMeshoptCompression).setRequired(true);
  return toArrayBuffer(await io.writeBinary(doc));
}

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  const out = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(out).set(bytes);
  return out;
}
