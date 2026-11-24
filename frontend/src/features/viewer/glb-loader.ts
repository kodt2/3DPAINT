import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';

/**
 * GLB loading for the viewer (step 3c.3).
 *
 * Autonomy and trust model:
 * - the GLB itself is fetched with a plain fetch() (the presigned S3 URL is
 *   trusted — the API minted it);
 * - parsing runs GLTFLoader.parse() on that buffer, so the only URLs that
 *   can reach the network from inside the document come from the glTF's
 *   own "images" section — those are untrusted;
 * - a LoadingManager URL modifier blocks every URI that is not a local
 *   blob:/data: resource. A blocked URI rejects the parse with a clear
 *   error instead of issuing a request (no CDN, no SSRF, no tracking).
 *
 * No Draco/Meshopt decoders are installed on purpose (no WASM from a CDN);
 * compressed geometry fails the parse and the UI shows a clear message.
 */

export type GlbErrorKind = 'http' | 'network' | 'aborted' | 'parse' | 'compression';

export class GlbLoadError extends Error {
  constructor(
    message: string,
    public readonly kind: GlbErrorKind,
  ) {
    super(message);
    this.name = 'GlbLoadError';
  }
}

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
    if (url.startsWith('blob:') || url.startsWith('data:')) {
      return url;
    }
    // Truncate: the URI is untrusted input and must not be echoed in full.
    throw new Error(`external resource inside glTF is not allowed: ${url.slice(0, 120)}`);
  });
  return manager;
}

/**
 * Parses an in-memory GLB buffer. Embedded image URIs must be blob:/data:
 * or the parse rejects (see createTrustedLoadingManager).
 */
export function parseGlb(arrayBuffer: ArrayBuffer): Promise<ParsedGlb> {
  return new Promise((resolve, reject) => {
    const loader = new GLTFLoader(createTrustedLoadingManager());
    try {
      loader.parse(
        arrayBuffer,
        '',
        (gltf) => resolve(gltf as ParsedGlb),
        (error) => reject(toGlbError(error, 'parse')),
      );
    } catch (error) {
      // GLTFLoader throws synchronously for a broken container (bad magic,
      // invalid JSON chunk).
      reject(toGlbError(error, 'parse'));
    }
  });
}

export function toGlbError(error: unknown, kind: GlbErrorKind): GlbLoadError {
  const message = error instanceof Error ? error.message : String(error);
  if (kind === 'parse' && /draco|meshopt/i.test(message)) {
    return new GlbLoadError(message, 'compression');
  }
  return new GlbLoadError(message, kind);
}

export interface LoadGlbOptions {
  signal: AbortSignal;
  onProgress?: (progress: GlbProgress) => void;
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

  try {
    return await parseGlb(buffer);
  } catch (error) {
    if (error instanceof GlbLoadError) {
      throw error;
    }
    throw toGlbError(error, 'parse');
  }
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
