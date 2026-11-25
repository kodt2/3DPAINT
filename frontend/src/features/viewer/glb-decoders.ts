/**
 * GLB decoders (step 3c.5) — lazy chunk.
 *
 * This module is dynamically imported by glb-loader ONLY when the file's
 * glTF JSON actually needs a decoder; a plain GLB never pays for it.
 *
 * All decoder assets are local (ADR 0014, no CDN):
 *   - Draco: DRACOLoader fetches the gltf-build wrapper + wasm
 *     (draco_wasm_wrapper.js, draco_decoder.wasm) from
 *     /assets/decoders/three-<version>/draco/ (copied from node_modules/three
 *     by scripts/sync-decoders.mjs); the decoder worker is created from a
 *     Blob URL, so the wrapper JS is fetched exactly once. The object form
 *     of setDecoderPath() leaves the pure-JS fallback (dep_js) unset —
 *     WebAssembly is required, and a clear error beats a ~700 KB JS
 *     fallback in a browser that has no WebAssembly.
 *   - Meshopt: meshopt_decoder.module.js inlines its wasm (base64) and
 *     spawns Blob-URL workers — zero network requests. three r187 exposes
 *     no dispose for its worker pool (workers live until page reload —
 *     TD-29).
 *   - KTX2: KTX2Loader fetches basis_transcoder.js + .wasm from
 *     /assets/decoders/three-<version>/basis/. `detectSupport()` reads GPU
 *     compressed-texture capabilities from a renderer; the viewer's real
 *     renderer only exists after the model is loaded, so we probe the same
 *     capabilities through a disposable WebGL context (same pattern as
 *     isWebGLAvailable in webgl.ts) and lose it right after.
 */
import * as THREE from 'three';
import { DRACOLoader } from 'three/examples/jsm/loaders/DRACOLoader.js';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { KTX2Loader } from 'three/examples/jsm/loaders/KTX2Loader.js';
import { MeshoptDecoder } from 'three/examples/jsm/libs/meshopt_decoder.module.js';
import { GlbLoadError } from './glb-errors';
import { DECODERS_BASE_URL } from './decoder-assets';
import type { DecoderRequirements } from './decoder-requirements';

export interface ConfiguredGlbLoader {
  loader: GLTFLoader;
  /**
   * Terminates the decoder workers that can be terminated (Draco pool,
   * KTX2 transcoder pool). Call it after the parse settles (success,
   * error, timeout or abort) — see glb-loader.ts.
   */
  dispose(): void;
}

/**
 * Builds a GLTFLoader (bound to the given trusted LoadingManager) with
 * exactly the decoders the file requires.
 */
export function configureGlbLoader(
  manager: THREE.LoadingManager,
  requirements: DecoderRequirements,
): ConfiguredGlbLoader {
  const loader = new GLTFLoader(manager);
  const disposers: Array<() => void> = [];

  if (requirements.draco) {
    const draco = new DRACOLoader(manager);
    // Explicit URLs (object form): the gltf build of the decoder, local
    // copies. dep_js stays null — no pure-JS fallback.
    draco.setDecoderPath({
      js: `${DECODERS_BASE_URL}draco/draco_wasm_wrapper.js`,
      wasm: `${DECODERS_BASE_URL}draco/draco_decoder.wasm`,
    });
    draco.preload(); // start the worker pool now, not mid-decode
    loader.setDRACOLoader(draco);
    disposers.push(() => draco.dispose());
  }

  if (requirements.meshopt) {
    // Module singleton (wasm embedded in the JS, Blob-URL workers): no
    // per-instance setup and no dispose in three r187 (TD-29).
    loader.setMeshoptDecoder(MeshoptDecoder);
  }

  if (requirements.ktx2) {
    const { probe, release } = probeKtx2Support();
    const ktx2 = new KTX2Loader(manager);
    ktx2.setTranscoderPath(`${DECODERS_BASE_URL}basis/`);
    // Synchronous read of the probe (extension has/get, incl.
    // getSupportedProfiles on the real extension objects).
    ktx2.detectSupport(probe as unknown as THREE.WebGLRenderer);
    release(); // the probe context is no longer needed
    loader.setKTX2Loader(ktx2);
    disposers.push(() => ktx2.dispose());
  }

  return {
    loader,
    dispose: () => {
      for (const dispose of disposers) {
        dispose();
      }
    },
  };
}

/**
 * Minimal shape of KTX2Loader.detectSupport's renderer argument (it reads
 * only `extensions.has/get` and `isWebGPURenderer`).
 */
interface Ktx2RendererProbe {
  isWebGPURenderer?: boolean;
  extensions: {
    has(name: string): boolean;
    get(name: string): unknown;
  };
}

interface Ktx2Probe {
  probe: Ktx2RendererProbe;
  /** Loses the probe context and detaches the canvas. */
  release: () => void;
}

function probeKtx2Support(): Ktx2Probe {
  const canvas = document.createElement('canvas');
  const gl = (canvas.getContext('webgl2') ??
    canvas.getContext('webgl')) as
    | (WebGLRenderingContext & { getSupportedExtensions(): string[] | null })
    | null;
  if (!gl) {
    // The viewer only loads when WebGL is available (webglOk gate), so a
    // missing context here means the browser lost WebGL mid-load.
    throw new GlbLoadError('cannot probe GPU texture formats for KTX2 (no WebGL context)', 'parse');
  }
  // detectSupport may call get() on the returned extension objects
  // (e.g. getSupportedProfiles), so hand out the real GL objects.
  const supported = new Set<string>(gl.getSupportedExtensions() ?? []);
  const probe: Ktx2RendererProbe = {
    extensions: {
      has: (name) => supported.has(name),
      get: (name) => gl.getExtension(name),
    },
  };
  return {
    probe,
    release: () => {
      gl.getExtension('WEBGL_lose_context')?.loseContext();
    },
  };
}
