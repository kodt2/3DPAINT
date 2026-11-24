/**
 * GLB (glTF 2.0 binary) factory for tests (step 3c.3).
 *
 * Builds small valid GLB files in memory — a unit cube (24 vertices,
 * 12 triangles) with or without a UV set, optionally with a PBR material,
 * and optionally with an external image reference (to verify that the
 * viewer's trusted LoadingManager blocks URIs embedded in untrusted glTF).
 * No binary fixtures are committed: every test builds the file on the fly.
 */

export interface CubeGlbOptions {
  /** Add a TEXCOORD_0 attribute (default true). */
  withUv?: boolean;
  /** Add a red PBR material to the primitive (default false). */
  withMaterial?: boolean;
  /**
   * Point the material's base color texture at an external URI. The viewer
   * must never fetch such a URI; the test asserts the parse fails and no
   * request is made.
   */
  externalImageUri?: string;
}

/** 6 faces x 4 vertices, face order: +Z, -Z, +X, -X, +Y, -Y. */
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

const CUBE_NORMALS = new Float32Array([
  0,
  0,
  1,
  0,
  0,
  1,
  0,
  0,
  1,
  0,
  0,
  1, // +Z
  0,
  0,
  -1,
  0,
  0,
  -1,
  0,
  0,
  -1,
  0,
  0,
  -1, // -Z
  1,
  0,
  0,
  1,
  0,
  0,
  1,
  0,
  0,
  1,
  0,
  0, // +X
  -1,
  0,
  0,
  -1,
  0,
  0,
  -1,
  0,
  0,
  -1,
  0,
  0, // -X
  0,
  1,
  0,
  0,
  1,
  0,
  0,
  1,
  0,
  0,
  1,
  0, // +Y
  0,
  -1,
  0,
  0,
  -1,
  0,
  0,
  -1,
  0,
  0,
  -1,
  0, // -Y
]);

/** Same (0,0)-(1,1) square on every face. */
const CUBE_UVS = new Float32Array([
  0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0,
  0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1,
]);

const CUBE_INDICES = new Uint16Array([
  0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11, 12, 13, 14, 12, 14, 15, 16, 17, 18, 16,
  18, 19, 20, 21, 22, 20, 22, 23,
]);

export const CUBE_VERTEX_COUNT = 24;
export const CUBE_TRIANGLE_COUNT = 12;

export function buildCubeGlb(options: CubeGlbOptions = {}): ArrayBuffer {
  const { withUv = true, withMaterial = false, externalImageUri } = options;
  const hasTexture = externalImageUri !== undefined;

  const positionBytes = CUBE_POSITIONS.byteLength;
  const normalBytes = CUBE_NORMALS.byteLength;
  const uvBytes = withUv ? CUBE_UVS.byteLength : 0;
  const indexBytes = CUBE_INDICES.byteLength;

  const uvOffset = positionBytes + normalBytes;
  const indexOffset = uvOffset + uvBytes;
  const binLength = indexOffset + indexBytes;

  const attributes: Record<string, number> = { POSITION: 0, NORMAL: 1 };
  const bufferViews: Record<string, unknown>[] = [
    { buffer: 0, byteOffset: 0, byteLength: positionBytes, target: 34962 },
    { buffer: 0, byteOffset: positionBytes, byteLength: normalBytes, target: 34962 },
  ];
  const accessors: Record<string, unknown>[] = [
    {
      bufferView: 0,
      componentType: 5126,
      count: CUBE_VERTEX_COUNT,
      type: 'VEC3',
      min: [-0.5, -0.5, -0.5],
      max: [0.5, 0.5, 0.5],
    },
    { bufferView: 1, componentType: 5126, count: CUBE_VERTEX_COUNT, type: 'VEC3' },
  ];
  if (withUv) {
    attributes.TEXCOORD_0 = 2;
    bufferViews.push({
      buffer: 0,
      byteOffset: uvOffset,
      byteLength: uvBytes,
      target: 34962,
    });
    accessors.push({ bufferView: 2, componentType: 5126, count: CUBE_VERTEX_COUNT, type: 'VEC2' });
  }
  bufferViews.push({ buffer: 0, byteOffset: indexOffset, byteLength: indexBytes, target: 34963 });
  accessors.push({
    bufferView: bufferViews.length - 1,
    componentType: 5123,
    count: CUBE_INDICES.length,
    type: 'SCALAR',
  });

  const json: Record<string, unknown> = {
    asset: { version: '2.0', generator: '3dpaint-test-glbfactory' },
    scene: 0,
    scenes: [{ nodes: [0] }],
    nodes: [{ mesh: 0, name: 'test-cube' }],
    meshes: [
      {
        primitives: [
          {
            attributes,
            indices: accessors.length - 1,
            ...(withMaterial || hasTexture ? { material: 0 } : {}),
          },
        ],
      },
    ],
    buffers: [{ byteLength: binLength }],
    bufferViews,
    accessors,
  };
  if (withMaterial || hasTexture) {
    const pbr: Record<string, unknown> = {
      baseColorFactor: [1, 0, 0, 1],
      metallicFactor: 0,
      roughnessFactor: 0.8,
    };
    if (hasTexture) {
      pbr.baseColorTexture = { index: 0 };
      json.images = [{ uri: externalImageUri }];
      json.textures = [{ source: 0 }];
    }
    json.materials = [{ name: 'test-red', pbrMetallicRoughness: pbr }];
  }

  const bin = new Uint8Array(binLength);
  bin.set(CUBE_POSITIONS, 0);
  bin.set(CUBE_NORMALS, positionBytes);
  if (withUv) {
    bin.set(CUBE_UVS, uvOffset);
  }
  bin.set(CUBE_INDICES, indexOffset);

  return assembleGlb(json, bin);
}

export interface LargeGlbOptions {
  /** Vertex count of the single indexed mesh (default 2 500 000). */
  vertexCount?: number;
  /** Triangle count of the single indexed mesh (default 2 000 000). */
  triangleCount?: number;
}

/**
 * A large GLB (~50 MB at the defaults: 2.5M vertices + 2M triangles) for
 * the streaming/progress test of the GLB loader. The geometry is degenerate
 * on purpose (repeated pattern) — the test cares about size, not shape.
 */
export function buildLargeGlb(options: LargeGlbOptions = {}): ArrayBuffer {
  const vertexCount = options.vertexCount ?? 2_500_000;
  const triangleCount = options.triangleCount ?? 2_000_000;

  const positions = new Float32Array(vertexCount * 3);
  const pattern = new Float32Array([
    -0.5, -0.5, 0.5, 0.5, -0.5, 0.5, 0.5, 0.5, 0.5, -0.5, 0.5, 0.5,
  ]);
  for (let i = 0; i < positions.length; i += 12) {
    positions.set(pattern, i);
  }

  const indices = new Uint32Array(triangleCount * 3);
  for (let t = 0; t < triangleCount; t++) {
    indices[t * 3] = t % vertexCount;
    indices[t * 3 + 1] = (t + 1) % vertexCount;
    indices[t * 3 + 2] = (t + 2) % vertexCount;
  }

  const positionBytes = positions.byteLength;
  const indexBytes = indices.byteLength;
  const bin = new Uint8Array(positionBytes + indexBytes);
  bin.set(new Uint8Array(positions.buffer), 0);
  bin.set(new Uint8Array(indices.buffer), positionBytes);

  const json: Record<string, unknown> = {
    asset: { version: '2.0', generator: '3dpaint-test-glbfactory' },
    scene: 0,
    scenes: [{ nodes: [0] }],
    nodes: [{ mesh: 0, name: 'large-mesh' }],
    meshes: [{ primitives: [{ attributes: { POSITION: 0 }, indices: 1 }] }],
    buffers: [{ byteLength: bin.byteLength }],
    bufferViews: [
      { buffer: 0, byteOffset: 0, byteLength: positionBytes, target: 34962 },
      { buffer: 0, byteOffset: positionBytes, byteLength: indexBytes, target: 34963 },
    ],
    accessors: [
      {
        bufferView: 0,
        componentType: 5126,
        count: vertexCount,
        type: 'VEC3',
        min: [-0.5, -0.5, -0.5],
        max: [0.5, 0.5, 0.5],
      },
      { bufferView: 1, componentType: 5125, count: triangleCount * 3, type: 'SCALAR' },
    ],
  };

  return assembleGlb(json, bin);
}

/** Bytes that look like nothing glTF-related: the parse must fail. */
export function buildCorruptGlb(): ArrayBuffer {
  const bytes = new TextEncoder().encode(
    'not a glb at all - corrupt file for the viewer error-path test',
  );
  return bytes.buffer;
}

function assembleGlb(json: object, bin: Uint8Array): ArrayBuffer {
  const jsonBytes = new TextEncoder().encode(JSON.stringify(json));
  const jsonPad = (4 - (jsonBytes.length % 4)) % 4;
  const jsonChunk = new Uint8Array(jsonBytes.length + jsonPad);
  jsonChunk.set(jsonBytes);
  jsonChunk.fill(0x20, jsonBytes.length); // space padding (spec: 0x20)

  const binPad = (4 - (bin.byteLength % 4)) % 4;
  const binChunk = new Uint8Array(bin.byteLength + binPad);
  binChunk.set(bin); // zero padding is implicit

  const totalLength = 12 + 8 + jsonChunk.length + 8 + binChunk.length;
  const out = new ArrayBuffer(totalLength);
  const bytes = new Uint8Array(out);
  const view = new DataView(out);
  view.setUint32(0, 0x46546c67, true); // magic 'glTF'
  view.setUint32(4, 2, true); // glTF version
  view.setUint32(8, totalLength, true);
  let offset = 12;
  view.setUint32(offset, jsonChunk.length, true);
  offset += 4;
  view.setUint32(offset, 0x4e4f534a, true); // 'JSON'
  offset += 4;
  bytes.set(jsonChunk, offset);
  offset += jsonChunk.length;
  view.setUint32(offset, binChunk.length, true);
  offset += 4;
  view.setUint32(offset, 0x004e4942, true); // 'BIN\0' (little-endian bytes 42 49 4E 00)
  offset += 4;
  bytes.set(binChunk, offset);
  return out;
}
