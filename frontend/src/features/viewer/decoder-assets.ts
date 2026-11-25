/**
 * Local decoder asset URLs (step 3c.5, ADR 0014).
 *
 * The decoder files (Draco WASM/JS, BasisU transcoder) are copied from
 * node_modules/three into public/ by scripts/sync-decoders.mjs and served
 * from the same origin as the SPA (immutable cache under /assets/**,
 * nginx; Gateway /assets/** -> frontend). The three version in the path
 * busts the cache on a three upgrade.
 *
 * No CDN, no third-party origins — the only non-blob/data: URLs the
 * viewer's LoadingManager may resolve (see createTrustedLoadingManager).
 */
import { DECODER_THREE_VERSION } from '../../generated/decoder-version';

export const DECODERS_BASE_URL = `/assets/decoders/three-${DECODER_THREE_VERSION}/`;

export function isDecoderAssetUrl(url: string): boolean {
  // The versioned prefix itself is a directory, not a file: require a path
  // segment after it (draco/..., basis/...).
  return url.startsWith(DECODERS_BASE_URL) && url.length > DECODERS_BASE_URL.length;
}
