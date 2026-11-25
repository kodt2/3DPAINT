import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { GlbLoadError, type GlbErrorKind } from './glb-errors';
import { isDecoderAssetUrl } from './decoder-assets';
import {
  decoderRequirements,
  hasDecoderRequirements,
  unsupportedRequiredExtension,
} from './decoder-requirements';
import { readGlbJson } from './glb-json';
import { DECODE_TIMEOUT_MS } from './viewer-config';

export { GlbLoadError, type GlbErrorKind } from './glb-errors';

/**
 * GLB loading for the viewer (step 3c.3, decoders in step 3c.5).
 *
 * Autonomy and trust model (ADR 0014):
 * - the GLB itself is fetched with a plain fetch() (the presigned S3 URL is
 *   trusted — the API minted it);
 * - parsing runs GLTFLoader.parse() on that buffer, so the only URLs that
 *   can reach the network from inside the document come from the glTF's
 *   own "images" section — those are untrusted;
 * - a LoadingManager URL modifier blocks every URI except local
 *   blob:/data: resources and the viewer's own decoder assets
 *   (/assets/decoders/three-<version>/ — same origin, local files). A
 *   blocked URI rejects the parse with a clear error instead of issuing a
 *   request (no CDN, no SSRF, no tracking).
 *
 * Decoders (step 3c.5): before parsing, the GLB JSON chunk is inspected.
 * If the file needs Draco/Meshopt/KTX2, the decoder chunk (glb-decoders)
 * is imported lazily and exactly the required decoders are installed. If
 * the file REQUIRES an extension the viewer cannot handle, it is rejected
 * up front with the extension named (no decode attempt). The decode phase
 * is bounded by DECODE_TIMEOUT_MS; on timeout/abort the decoders are
 * disposed.
 */

export interface GlbProgress {
  loaded: number;
  /** Null when the response has no Content-Length. */
  total: number | null;
}

/**
 * Minimal glTF shape the viewer actually uses; avoids importing three's
 * example-file types into app code.
 */
export interface ParsedGlb {
  scene: THREE.Group;
}

export function createTrustedLoadingManager(): THREE.LoadingManager {
  const manager = new THREE.LoadingManager();
  manager.setURLModifier((url) => {
    if (url.startsWith('blob:') || url.startsWith('data:') || isDecoderAssetUrl(url)) {
      return url;
    }
    // Truncate: the URI is untrusted input and must not be echoed in full.
    throw new Error(`external resource inside glTF is not allowed: ${url.slice(0, 120)}`);
  });
  return manager;
}

export interface LoadGlbOptions {
  signal: AbortSignal;
  onProgress?: (progress: GlbProgress) => void;
  /**
   * Decode-phase timeout in ms (default DECODE_TIMEOUT_MS). The download
   * is not bounded by it — only GLTFLoader.parse() + decoder workers.
   */
  decodeTimeoutMs?: number;
}

/**
 * Fetches and parses a GLB from a presigned URL. Aborting the signal (user
 * navigates away) rejects with kind 'aborted' — callers must not treat that
 * as a failure state.
 */
export async function loadGlb(url: string, options: LoadGlbOptions): Promise<ParsedGlb> {
  const response = await fetch(url, { signal: options.signal }).catch((error: unknown) => {
    if (options.signal.aborted || isAbortError(error)) {
      throw new GlbLoadError('aborted', 'aborted');
    }
    throw new GlbLoadError(
      `network: ${error instanceof Error ? error.message : String(error)}`,
      'network',
    );
  });

  if (!response.ok) {
    throw new GlbLoadError(`http ${response.status}`, 'http');
  }

  const totalHeader = response.headers.get('content-length');
  const total =
    totalHeader !== null && Number.isFinite(Number(totalHeader)) ? Number(totalHeader) : null;

  const buffer = await readBody(response, options.signal, (loaded) => {
    options.onProgress?.({ loaded, total });
  });

  // Inspect the container + glTF JSON BEFORE parsing: which decoders the
  // file needs and whether it requires an unsupported extension (3c.5).
  // readGlbJson throws GlbLoadError('parse') on a broken container —
  // the same failure GLTFLoader would report, one network round-trip
  // earlier.
  const gltfJson = readGlbJson(buffer);
  const unsupported = unsupportedRequiredExtension(gltfJson);
  if (unsupported !== null) {
    throw new GlbLoadError(
      `file requires a glTF extension the viewer does not support: ${unsupported}`,
      'unsupported-extension',
      unsupported,
    );
  }

  const manager = createTrustedLoadingManager();
  let loader: GLTFLoader;
  let disposeDecoders: (() => void) | null = null;
  const requirements = decoderRequirements(gltfJson);
  if (hasDecoderRequirements(requirements)) {
    // Lazy chunk: Draco/KTX2 loaders + Meshopt wasm are fetched only when
    // the file actually needs them (3c.5).
    const { configureGlbLoader } = await import('./glb-decoders');
    const configured = configureGlbLoader(manager, requirements);
    loader = configured.loader;
    disposeDecoders = configured.dispose;
  } else {
    loader = new GLTFLoader(manager);
  }

  try {
    return await parseWithGuards(
      loader,
      buffer,
      options.signal,
      options.decodeTimeoutMs ?? DECODE_TIMEOUT_MS,
    );
  } finally {
    // Success, error, timeout or abort: the decoder workers are done.
    // (Meshopt's pool cannot be terminated in three r187 — TD-29.)
    disposeDecoders?.();
  }
}

/**
 * Parses an in-memory GLB buffer with a plain (no-decoder) GLTFLoader.
 * Kept for tests and for callers that do not need compression support;
 * the full pipeline is loadGlb.
 */
export function parseGlb(arrayBuffer: ArrayBuffer): Promise<ParsedGlb> {
  return parseWithGuards(
    new GLTFLoader(createTrustedLoadingManager()),
    arrayBuffer,
    new AbortController().signal,
    DECODE_TIMEOUT_MS,
  );
}

/**
 * GLTFLoader.parse with decode-phase guards:
 *  - timeout (decodeTimeoutMs) → GlbLoadError('decode-timeout');
 *  - abort → GlbLoadError('aborted');
 *  - synchronous throws (broken container) → toGlbError('parse');
 *  - decode failures for compressed data → kind 'compression'.
 *
 * The parse itself cannot be cancelled (GLTFLoader has no cancel API); on
 * timeout/abort the caller disposes the decoders, which terminates the
 * worker pools and leaves the hung parse to be garbage-collected.
 */
function parseWithGuards(
  loader: GLTFLoader,
  buffer: ArrayBuffer,
  signal: AbortSignal,
  decodeTimeoutMs: number,
): Promise<ParsedGlb> {
  return new Promise<ParsedGlb>((resolve, reject) => {
    let settled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const settle = (fn: () => void) => {
      if (settled) return;
      settled = true;
      if (timer !== null) {
        clearTimeout(timer);
      }
      signal.removeEventListener('abort', onAbort);
      fn();
    };

    const onAbort = () => {
      settle(() => reject(new GlbLoadError('aborted', 'aborted')));
    };

    timer = setTimeout(
      () =>
        settle(() =>
          reject(
            new GlbLoadError(
              `decode did not finish within ${Math.round(decodeTimeoutMs / 1000)} s`,
              'decode-timeout',
            ),
          ),
        ),
      decodeTimeoutMs,
    );

    if (signal.aborted) {
      onAbort();
      return;
    }
    signal.addEventListener('abort', onAbort, { once: true });

    try {
      loader.parse(
        buffer,
        '',
        (gltf) => settle(() => resolve(gltf as ParsedGlb)),
        (error) => settle(() => reject(toGlbError(error, 'parse'))),
      );
    } catch (error) {
      // GLTFLoader throws synchronously for a broken container (bad magic,
      // invalid JSON chunk).
      settle(() => reject(toGlbError(error, 'parse')));
    }
  });
}

export function toGlbError(error: unknown, kind: GlbErrorKind): GlbLoadError {
  const message = error instanceof Error ? error.message : String(error);
  // Compressed-data decode failures (Draco/Meshopt/KTX2/BasisU) are a
  // distinct user-facing case: the file is supported, the payload is bad.
  if (kind === 'parse' && /draco|meshopt|ktx2|basis/i.test(message)) {
    return new GlbLoadError(message, 'compression');
  }
  return new GlbLoadError(message, kind);
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

async function readBody(
  response: Response,
  signal: AbortSignal,
  onLoaded: (loaded: number) => void,
): Promise<ArrayBuffer> {
  const body = response.body;
  if (!body) {
    return await response.arrayBuffer().catch(() => {
      throwAbortedOrNetwork(signal);
    });
  }

  const reader = body.getReader();
  const chunks: Uint8Array[] = [];
  let loaded = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      if (value) {
        chunks.push(value);
        loaded += value.length;
        onLoaded(loaded);
      }
    }
  } catch (error) {
    if (signal.aborted || isAbortError(error)) {
      throw new GlbLoadError('aborted', 'aborted');
    }
    throw new GlbLoadError(
      `network: ${error instanceof Error ? error.message : String(error)}`,
      'network',
    );
  }

  const out = new Uint8Array(loaded);
  let offset = 0;
  for (const chunk of chunks) {
    out.set(chunk, offset);
    offset += chunk.length;
  }
  return out.buffer;
}

function throwAbortedOrNetwork(signal: AbortSignal): never {
  if (signal.aborted) {
    throw new GlbLoadError('aborted', 'aborted');
  }
  throw new GlbLoadError('network: cannot read the response body', 'network');
}
