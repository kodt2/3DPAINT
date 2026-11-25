import { describe, expect, it } from 'vitest';
import { readGlbJson } from './glb-json';
import { buildCubeGlb } from '../../test/glbfactory';

/**
 * Builds a minimal GLB container with the given JSON chunk text and
 * optional header/chunk overrides, so each failure branch of readGlbJson
 * can be exercised in isolation.
 */
function makeGlb(
  jsonText: string,
  overrides: {
    magic?: number;
    version?: number;
    declaredLength?: number;
    jsonChunkLength?: number;
    chunkType?: number;
  } = {},
): ArrayBuffer {
  const jsonBytes = new TextEncoder().encode(jsonText);
  const length = 20 + jsonBytes.length;
  const out = new ArrayBuffer(length);
  const view = new DataView(out);
  view.setUint32(0, overrides.magic ?? 0x46546c67, true); // 'glTF'
  view.setUint32(4, overrides.version ?? 2, true);
  view.setUint32(8, overrides.declaredLength ?? length, true);
  view.setUint32(12, overrides.jsonChunkLength ?? jsonBytes.length, true);
  view.setUint32(16, overrides.chunkType ?? 0x4e4f534a, true); // 'JSON'
  new Uint8Array(out, 20).set(jsonBytes);
  return out;
}

const VALID_JSON = '{"asset":{"version":"2.0"}}';

describe('readGlbJson', () => {
  it('reads the JSON chunk of a valid GLB', () => {
    const json = readGlbJson(makeGlb(VALID_JSON));
    expect(json).toEqual({ asset: { version: '2.0' } });
  });

  it('reads a real fixture built by glbfactory', () => {
    const json = readGlbJson(buildCubeGlb());
    expect((json.asset as { version: string }).version).toBe('2.0');
    expect(Array.isArray(json.scenes)).toBe(true);
  });

  it('rejects a file shorter than the container header', () => {
    const tiny = new ArrayBuffer(8);
    expect(() => readGlbJson(tiny)).toThrow(/shorter than the container header/);
  });

  it('rejects a bad magic', () => {
    expect(() => readGlbJson(makeGlb(VALID_JSON, { magic: 0xdeadbeef }))).toThrow(/bad magic/);
  });

  it('rejects a non-2.0 glTF version', () => {
    expect(() => readGlbJson(makeGlb(VALID_JSON, { version: 1 }))).toThrow(/not glTF 2\.0/);
  });

  it('rejects a declared length that exceeds the file size', () => {
    const glb = makeGlb(VALID_JSON);
    expect(() => readGlbJson(makeGlb(VALID_JSON, { declaredLength: glb.byteLength + 10 }))).toThrow(
      /declared length exceeds file size/,
    );
  });

  it('rejects a file whose first chunk is not JSON', () => {
    // 'BIN\0' chunk type: a GLB must start with the JSON chunk.
    expect(() => readGlbJson(makeGlb(VALID_JSON, { chunkType: 0x004e4942 }))).toThrow(
      /first chunk is not JSON/,
    );
  });

  it('rejects a JSON chunk that overruns the file', () => {
    const glb = makeGlb(VALID_JSON);
    const over = makeGlb(VALID_JSON, { jsonChunkLength: 12 + glb.byteLength });
    expect(() => readGlbJson(over)).toThrow(/JSON chunk overruns the file/);
  });

  it('rejects a JSON chunk that is not valid JSON', () => {
    expect(() => readGlbJson(makeGlb('{oops'))).toThrow(/not valid JSON/);
  });

  it('rejects a JSON chunk that is not an object', () => {
    expect(() => readGlbJson(makeGlb('[1,2,3]'))).toThrow(/not a glTF object/);
  });
});
