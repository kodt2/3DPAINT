import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as THREE from 'three';
import { buildCorruptGlb, buildCubeGlb, buildLargeGlb } from '../../test/glbfactory';
import { DECODERS_BASE_URL } from './decoder-assets';
import {
  createTrustedLoadingManager,
  loadGlb,
  parseGlb,
} from './glb-loader';

/**
 * The lazy decoder chunk is mocked: real decoder workers cannot run under
 * jsdom, and the unit under test here is glb-loader's pipeline (when the
 * chunk is imported, what requirements are passed, when dispose runs),
 * not the decoders themselves (those are covered by E2E).
 */
const decodersMock = vi.hoisted(() => {
  const holder: {
    parse: (onLoad: (g: unknown) => void, onError: (e: unknown) => void) => void;
  } = { parse: () => {} };
  const dispose = vi.fn();
  const configureGlbLoader = vi.fn(
    (_manager: unknown, _requirements: unknown) => ({
      loader: {
        parse: (
          _buffer: ArrayBuffer,
          _path: string,
          onLoad: (g: unknown) => void,
          onError: (e: unknown) => void,
        ) => {
          holder.parse(onLoad, onError);
        },
      },
      dispose,
    }),
  );
  return { holder, dispose, configureGlbLoader };
});

vi.mock('./glb-decoders', () => ({
  configureGlbLoader: decodersMock.configureGlbLoader,
}));

/** A valid GLB container with only a JSON chunk (no BIN chunk). */
function buildJsonGlb(json: Record<string, unknown>): ArrayBuffer {
  const jsonBytes = new TextEncoder().encode(JSON.stringify(json));
  const out = new ArrayBuffer(20 + jsonBytes.length);
  const view = new DataView(out);
  view.setUint32(0, 0x46546c67, true);
  view.setUint32(4, 2, true);
  view.setUint32(8, out.byteLength, true);
  view.setUint32(12, jsonBytes.length, true);
  view.setUint32(16, 0x4e4f534a, true);
  new Uint8Array(out, 20).set(jsonBytes);
  return out;
}

function stubFetchWith(glb: ArrayBuffer) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => {
      const bytes = new Uint8Array(glb);
      return new Response(bytes, {
        status: 200,
        headers: { 'content-length': String(bytes.length) },
      });
    }),
  );
}

describe('createTrustedLoadingManager', () => {
  it('allows blob: and data: URIs', () => {
    const manager = createTrustedLoadingManager();
    expect(manager.resolveURL('blob:http://localhost:18090/abc-123')).toBe(
      'blob:http://localhost:18090/abc-123',
    );
    expect(manager.resolveURL('data:image/png;base64,iVBORw0KGgo=')).toBe(
      'data:image/png;base64,iVBORw0KGgo=',
    );
  });

  it('blocks every other scheme (external request inside untrusted glTF)', () => {
    const manager = createTrustedLoadingManager();
    expect(() => manager.resolveURL('https://evil.example.com/steal.png')).toThrow(
      /external resource inside glTF is not allowed/,
    );
    expect(() => manager.resolveURL('http://127.0.0.1:9999/x.png')).toThrow(
      /external resource inside glTF is not allowed/,
    );
    // A bare relative URI is not blob:/data: either — it must not resolve
    // against the page origin.
    expect(() => manager.resolveURL('images/texture.png')).toThrow(
      /external resource inside glTF is not allowed/,
    );
  });

  it('truncates the untrusted URI in the error message', () => {
    const manager = createTrustedLoadingManager();
    const longUri = `https://evil.example.com/${'a'.repeat(500)}.png`;
    try {
      manager.resolveURL(longUri);
      expect.unreachable('should have thrown');
    } catch (error) {
      expect((error as Error).message.length).toBeLessThanOrEqual(170);
    }
  });

  it('allows the viewer\'s own decoder assets (same origin, versioned path)', () => {
    const manager = createTrustedLoadingManager();
    const url = `${DECODERS_BASE_URL}draco/draco_decoder.wasm`;
    expect(manager.resolveURL(url)).toBe(url);
    expect(manager.resolveURL(`${DECODERS_BASE_URL}basis/basis_transcoder.js`)).toBe(
      `${DECODERS_BASE_URL}basis/basis_transcoder.js`,
    );
  });

  it('does not trust decoder assets of another three version', () => {
    const manager = createTrustedLoadingManager();
    expect(() =>
      manager.resolveURL('/assets/decoders/three-0.0.0/draco/draco_decoder.wasm'),
    ).toThrow(/external resource inside glTF is not allowed/);
  });
});

describe('parseGlb', () => {
  it('parses a valid cube GLB', async () => {
    const { scene } = await parseGlb(buildCubeGlb());
    let meshes = 0;
    scene.traverse((child) => {
      if ((child as { isMesh?: boolean }).isMesh) {
        meshes += 1;
      }
    });
    expect(meshes).toBe(1);
  });

  it('rejects a corrupt file with kind "parse"', async () => {
    await expect(parseGlb(buildCorruptGlb())).rejects.toMatchObject({
      name: 'GlbLoadError',
      kind: 'parse',
    });
  });

  it('never touches the network for an external image URI inside a GLB', async () => {
    const createElementSpy = vi.spyOn(document, 'createElementNS');

    // three r187 swallows per-texture failures (the texture becomes null),
    // so the observable guarantees are: the parse does not crash, no <img>
    // element is created for the untrusted URI (no request is issued), and
    // the mesh ends up with a null map.
    const { scene } = await parseGlb(
      buildCubeGlb({ withMaterial: true, externalImageUri: 'https://evil.example.com/steal.png' }),
    );

    expect(createElementSpy).not.toHaveBeenCalledWith(expect.anything(), 'img');
    let map: unknown = 'not-found';
    scene.traverse((child) => {
      const mesh = child as { isMesh?: boolean; material?: { map?: unknown } };
      if (mesh.isMesh) {
        map = mesh.material?.map;
      }
    });
    expect(map).toBeNull();
  });
});

describe('loadGlb', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('fetches, reports progress and parses the buffer', async () => {
    const glb = buildCubeGlb();
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, _init?: RequestInit) => {
        const bytes = new Uint8Array(glb);
        return new Response(bytes, {
          status: 200,
          headers: { 'content-length': String(bytes.length) },
        });
      }),
    );

    const progress: number[] = [];
    const { scene } = await loadGlb('http://storage.example/dl?sig=x', {
      signal: new AbortController().signal,
      onProgress: (p) => {
        if (p.total !== null) {
          progress.push(p.loaded);
        }
      },
    });

    let meshes = 0;
    scene.traverse((child) => {
      if ((child as { isMesh?: boolean }).isMesh) {
        meshes += 1;
      }
    });
    expect(meshes).toBe(1);
    expect(progress.length).toBeGreaterThan(0);
    expect(progress.at(-1)).toBe(glb.byteLength);
  });

  it('reports the final loaded byte count', async () => {
    const glb = buildCubeGlb();
    const bytes = new Uint8Array(glb);
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(bytes, { status: 200, headers: { 'content-length': String(bytes.length) } }),
      ),
    );

    const progress: { loaded: number; total: number | null }[] = [];
    await loadGlb('http://storage.example/dl', {
      signal: new AbortController().signal,
      onProgress: (p) => progress.push(p),
    });

    expect(progress.at(-1)?.loaded).toBe(bytes.length);
    expect(progress.at(-1)?.total).toBe(bytes.length);
  });

  it('maps a non-2xx response to kind "http"', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('forbidden', { status: 403 })),
    );

    await expect(
      loadGlb('http://storage.example/dl', { signal: new AbortController().signal }),
    ).rejects.toMatchObject({
      name: 'GlbLoadError',
      kind: 'http',
    });
  });

  it('maps a fetch network failure to kind "network"', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('fetch failed');
      }),
    );

    await expect(
      loadGlb('http://storage.example/dl', { signal: new AbortController().signal }),
    ).rejects.toMatchObject({
      name: 'GlbLoadError',
      kind: 'network',
    });
  });

  it('aborts cleanly when the signal fires (kind "aborted", not an error state)', async () => {
    const controller = new AbortController();
    vi.stubGlobal(
      'fetch',
      vi.fn(
        (_url: string, init?: RequestInit) =>
          new Promise<Response>((_resolve, reject) => {
            init?.signal?.addEventListener('abort', () => {
              reject(new DOMException('The operation was aborted.', 'AbortError'));
            });
          }),
      ),
    );

    const promise = loadGlb('http://storage.example/dl', { signal: controller.signal });
    controller.abort();

    await expect(promise).rejects.toMatchObject({ name: 'GlbLoadError', kind: 'aborted' });
  });

  // ~50 MB model (step 3c.3 requirement): the streaming reader must
  // survive a large body and report the full size, and the parse must
  // finish (measured locally: < 1 s for parse, a few seconds end-to-end).
  it('streams and parses a ~50 MB model', async () => {
    const glb = buildLargeGlb();
    expect(glb.byteLength / 1024 / 1024).toBeGreaterThan(45);

    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        const bytes = new Uint8Array(glb);
        return new Response(bytes, {
          status: 200,
          headers: { 'content-length': String(bytes.length) },
        });
      }),
    );

    const progress: { loaded: number; total: number | null }[] = [];
    const { scene } = await loadGlb('http://storage.example/big', {
      signal: new AbortController().signal,
      onProgress: (p) => progress.push(p),
    });

    let vertices = 0;
    let triangles = 0;
    scene.traverse((child) => {
      const mesh = child as {
        isMesh?: boolean;
        geometry?: {
          getAttribute: (n: string) => { count: number } | null;
          getIndex: () => { count: number } | null;
        };
      };
      if (mesh.isMesh && mesh.geometry) {
        vertices = mesh.geometry.getAttribute('position')?.count ?? 0;
        triangles = Math.floor((mesh.geometry.getIndex()?.count ?? 0) / 3);
      }
    });
    expect(vertices).toBe(2_500_000);
    expect(triangles).toBe(2_000_000);
    expect(progress.length).toBeGreaterThan(0);
    expect(progress.at(-1)?.loaded).toBe(glb.byteLength);
    expect(progress.at(-1)?.total).toBe(glb.byteLength);
  }, 60_000);

  it('classifies Draco-compressed geometry as "compression"', async () => {
    const { toGlbError } = await import('./glb-loader');
    expect(toGlbError(new Error('DRACOLoader: DRACO decoder not initialized'), 'parse').kind).toBe(
      'compression',
    );
    expect(toGlbError(new Error('Failed to find meshopt decoder'), 'parse').kind).toBe(
      'compression',
    );
    expect(toGlbError(new Error('something else'), 'parse').kind).toBe('parse');
  });
});

describe('decoder-aware loading (step 3c.5)', () => {
  beforeEach(() => {
    vi.unstubAllGlobals();
    decodersMock.configureGlbLoader.mockClear();
    decodersMock.dispose.mockClear();
    decodersMock.holder.parse = () => {}; // default: the decode hangs
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('rejects an unsupported required extension BEFORE importing the decoder chunk', async () => {
    stubFetchWith(
      buildJsonGlb({
        asset: { version: '2.0' },
        extensionsRequired: ['FOO_fake_extension'],
      }),
    );

    await expect(
      loadGlb('http://storage.example/dl', { signal: new AbortController().signal }),
    ).rejects.toMatchObject({
      name: 'GlbLoadError',
      kind: 'unsupported-extension',
      detail: 'FOO_fake_extension',
    });
    // The decoder chunk (and its worker pools) must never be created for a
    // file the viewer rejects up front.
    expect(decodersMock.configureGlbLoader).not.toHaveBeenCalled();
  });

  it('installs exactly the required decoders and disposes them after success', async () => {
    decodersMock.holder.parse = (onLoad) => onLoad({ scene: new THREE.Group() });
    stubFetchWith(
      buildJsonGlb({
        asset: { version: '2.0' },
        extensionsUsed: ['KHR_draco_mesh_compression'],
      }),
    );

    const { scene } = await loadGlb('http://storage.example/dl', {
      signal: new AbortController().signal,
    });

    expect(scene).toBeInstanceOf(THREE.Group);
    expect(decodersMock.configureGlbLoader).toHaveBeenCalledTimes(1);
    expect(decodersMock.configureGlbLoader).toHaveBeenCalledWith(
      expect.any(THREE.LoadingManager),
      { draco: true, meshopt: false, ktx2: false },
    );
    expect(decodersMock.dispose).toHaveBeenCalledTimes(1);
  });

  it('maps a decode error mentioning draco to kind "compression"', async () => {
    decodersMock.holder.parse = (_onLoad, onError) =>
      onError(new Error('DRACOLoader: decode failed'));
    stubFetchWith(
      buildJsonGlb({
        asset: { version: '2.0' },
        extensionsUsed: ['KHR_draco_mesh_compression'],
      }),
    );

    await expect(
      loadGlb('http://storage.example/dl', { signal: new AbortController().signal }),
    ).rejects.toMatchObject({ name: 'GlbLoadError', kind: 'compression' });
    expect(decodersMock.dispose).toHaveBeenCalledTimes(1);
  });

  it('maps a hung decode to kind "decode-timeout" and disposes the decoders', async () => {
    // holder.parse default: hang
    stubFetchWith(
      buildJsonGlb({
        asset: { version: '2.0' },
        extensionsUsed: ['EXT_meshopt_compression'],
      }),
    );

    await expect(
      loadGlb('http://storage.example/dl', {
        signal: new AbortController().signal,
        decodeTimeoutMs: 50,
      }),
    ).rejects.toMatchObject({ name: 'GlbLoadError', kind: 'decode-timeout' });
    expect(decodersMock.dispose).toHaveBeenCalledTimes(1);
  });

  it('aborts a hung decode with kind "aborted" and disposes the decoders', async () => {
    const controller = new AbortController();
    stubFetchWith(
      buildJsonGlb({
        asset: { version: '2.0' },
        extensionsUsed: ['KHR_texture_basisu'],
      }),
    );

    const promise = loadGlb('http://storage.example/dl', { signal: controller.signal });
    setTimeout(() => controller.abort(), 10);

    await expect(promise).rejects.toMatchObject({ name: 'GlbLoadError', kind: 'aborted' });
    expect(decodersMock.dispose).toHaveBeenCalledTimes(1);
  });

  it('never installs decoders for a plain GLB', async () => {
    stubFetchWith(buildCubeGlb());

    await loadGlb('http://storage.example/dl', { signal: new AbortController().signal });

    expect(decodersMock.configureGlbLoader).not.toHaveBeenCalled();
    expect(decodersMock.dispose).not.toHaveBeenCalled();
  });
});
