import { describe, expect, it } from 'vitest';
import {
  DRACO_EXTENSION,
  MESHOPT_EXTENSION,
  KTX2_EXTENSION,
  SUPPORTED_GLTF_EXTENSIONS,
  decoderRequirements,
  hasDecoderRequirements,
  unsupportedRequiredExtension,
} from './decoder-requirements';

function gltf(used: string[] = [], required: string[] = []): object {
  const json: Record<string, unknown> = { asset: { version: '2.0' } };
  if (used.length > 0) json.extensionsUsed = used;
  if (required.length > 0) json.extensionsRequired = required;
  return json;
}

describe('decoderRequirements', () => {
  it('is empty for a plain GLB', () => {
    expect(decoderRequirements(gltf())).toEqual({ draco: false, meshopt: false, ktx2: false });
  });

  it('detects each decoder from extensionsUsed', () => {
    expect(decoderRequirements(gltf([DRACO_EXTENSION, MESHOPT_EXTENSION, KTX2_EXTENSION]))).toEqual({
      draco: true,
      meshopt: true,
      ktx2: true,
    });
  });

  it('detects decoders from extensionsRequired as well', () => {
    expect(decoderRequirements(gltf([], [DRACO_EXTENSION]))).toEqual({
      draco: true,
      meshopt: false,
      ktx2: false,
    });
  });

  it('hasDecoderRequirements is true iff any decoder is needed', () => {
    expect(hasDecoderRequirements(decoderRequirements(gltf()))).toBe(false);
    expect(
      hasDecoderRequirements(decoderRequirements(gltf([], [KTX2_EXTENSION]))),
    ).toBe(true);
  });

  it('ignores missing or malformed extensions fields', () => {
    expect(decoderRequirements({ asset: { version: '2.0' } })).toEqual({
      draco: false,
      meshopt: false,
      ktx2: false,
    });
    expect(
      decoderRequirements({ extensionsUsed: 'draco?', extensionsRequired: [42, DRACO_EXTENSION] }),
    ).toEqual({ draco: true, meshopt: false, ktx2: false });
  });
});

describe('unsupportedRequiredExtension', () => {
  it('is null for a file with no required extensions', () => {
    expect(unsupportedRequiredExtension(gltf())).toBeNull();
  });

  it('is null for required extensions the viewer supports', () => {
    expect(
      unsupportedRequiredExtension(gltf([], [DRACO_EXTENSION, 'KHR_mesh_quantization'])),
    ).toBeNull();
  });

  it('names the first unsupported required extension', () => {
    expect(unsupportedRequiredExtension(gltf([], ['FOO_first', 'BAR_second']))).toBe('FOO_first');
  });

  it('never rejects on extensionsUsed alone (optional extensions may be skipped)', () => {
    expect(unsupportedRequiredExtension(gltf(['FOO_unknown_used']))).toBeNull();
  });
});

describe('SUPPORTED_GLTF_EXTENSIONS', () => {
  it('includes the three decoder extensions', () => {
    for (const extension of [DRACO_EXTENSION, MESHOPT_EXTENSION, KTX2_EXTENSION]) {
      expect(SUPPORTED_GLTF_EXTENSIONS.has(extension)).toBe(true);
    }
  });

  it('includes the quantization and legacy meshopt names', () => {
    expect(SUPPORTED_GLTF_EXTENSIONS.has('KHR_mesh_quantization')).toBe(true);
    expect(SUPPORTED_GLTF_EXTENSIONS.has('KHR_meshopt_compression')).toBe(true);
  });
});
