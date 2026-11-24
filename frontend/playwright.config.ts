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
  projects: [{ name: 'chromium' }],
});
