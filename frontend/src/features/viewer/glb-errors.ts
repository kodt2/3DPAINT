/**
 * GLB loading error types (step 3c.3, extended in step 3c.5).
 *
 * 'compression' means "compressed data failed to decode" (corrupt
 * Draco/Meshopt/KTX2 payload) — since step 3c.5 the decoders are installed
 * locally, so a 'compression' error is a decode failure, not a missing
 * decoder.
 *
 * 'unsupported-extension' names (in `detail`) the required glTF extension
 * the viewer cannot handle; the file is rejected before any decode attempt.
 *
 * 'decode-timeout' is the decode-phase budget (viewer-config.ts) exceeded.
 */

export type GlbErrorKind =
  | 'http'
  | 'network'
  | 'aborted'
  | 'parse'
  | 'compression'
  | 'unsupported-extension'
  | 'decode-timeout';

export class GlbLoadError extends Error {
  constructor(
    message: string,
    public readonly kind: GlbErrorKind,
    public readonly detail?: string,
  ) {
    super(message);
    this.name = 'GlbLoadError';
  }
}
