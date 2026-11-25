import { createHash, X509Certificate } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { defineConfig } from '@playwright/test';

/**
 * E2E runs against the Gateway — the same entry point a real browser uses —
 * never straight to the monolith or Vite. The host is "localhost" (not
 * 127.0.0.1) on purpose: the browser sends it in the Origin header, and the
 * Gateway only accepts http://localhost:{WEB_PORT} by default (CSRF check).
 *
 * Base URL resolution:
 *   1. E2E_BASE_URL (explicit, e.g. http://localhost:18080);
 *   2. http://localhost:{WEB_PORT} (the port published by infra/compose.yml);
 *   3. http://localhost:8080 (project default from infra/env/.env.example).
 */
export const E2E_BASE_URL =
  process.env.E2E_BASE_URL ??
  (process.env.WEB_PORT ? `http://localhost:${process.env.WEB_PORT}` : 'http://localhost:8080');

/**
 * The monolith's direct port (compose.e2e.yml, Makefile `e2e` / `e2e-https`).
 * Used only by the upload-limit test that must verify the monolith enforces
 * MAX_UPLOAD_BYTES without the Gateway. This is a node-side request (not a
 * browser), so 127.0.0.1 is fine — the CSRF/Origin rules do not apply.
 */
export const E2E_MONOLITH_URL = process.env.E2E_MONOLITH_URL ?? 'http://127.0.0.1:18081';

/**
 * The https e2e stand (Makefile `e2e-https`, step H) serves the site and S3
 * through Caddy with its internal CA. Instead of installing that root into
 * the host trust store, Chromium is told to trust exactly those certificates:
 * the SHA-256 of each SubjectPublicKeyInfo (the spki-list flag ignores cert
 * errors only for the listed certificates — any other one is still rejected).
 * E2E_CUSTOM_CA = comma-separated paths to the CA PEMs (PEM). Caddy's
 * internal PKI is a root -> intermediate -> leaf hierarchy and it presents
 * only [leaf, intermediate] in the chain, so the INTERMEDIATE's SPKI must be
 * listed (the root is never presented and its hash would not match).
 */
function caddyCaArg(): string | undefined {
  const caList = process.env.E2E_CUSTOM_CA;
  if (!caList) return undefined;
  const spkiHashes = caList
    .split(',')
    .map((p) => p.trim())
    .filter(Boolean)
    .map((caPath) => {
      const cert = new X509Certificate(readFileSync(caPath, 'utf8'));
      const spki = cert.publicKey.export({ type: 'spki', format: 'der' });
      return createHash('sha256').update(spki).digest('base64');
    });
  return `--ignore-certificate-errors-spki-list=${spkiHashes.join(',')}`;
}

const chromiumArgs = caddyCaArg() ? [caddyCaArg()!] : [];

const baseURL = E2E_BASE_URL;

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
  },
  // The project name selects the browser; "browser" cannot be set in
  // "use" in current Playwright.
  projects: [{ name: 'chromium', use: { launchOptions: { args: chromiumArgs } } }],
});
