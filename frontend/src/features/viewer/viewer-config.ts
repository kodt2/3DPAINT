/**
 * Viewer tuning values (step 3c.5).
 *
 * These are build-time constants of the SPA (no backend round-trip needed);
 * the only runtime user config is the upload limit from GET /api/config.
 */

/**
 * Decode-phase timeout: the time GLTFLoader.parse() + decoder workers have
 * to finish AFTER the file is fully downloaded. A 500 MiB model downloads
 * in well under this budget on the dev network, so the budget targets
 * decode stalls (bit-flipped Draco/Meshopt payloads, hung workers), not the
 * network. On timeout the decoders are disposed and the user sees a clear
 * error with a retry path.
 */
export const DECODE_TIMEOUT_MS = 60_000;
