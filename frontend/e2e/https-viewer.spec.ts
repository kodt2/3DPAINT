/**
 * Step H (ADR 0016): the full viewer flow over HTTPS, behind Caddy with its
 * internal CA (`make e2e-https`).
 *
 * Chromium trusts the stand's Caddy CA via --ignore-certificate-errors-
 * spki-list (E2E_CUSTOM_CA, playwright.config.ts) and Node-side requests
 * verify the chain against the copied root (NODE_EXTRA_CA_CERTS), so these
 * assertions cover the real failure modes a LAN client would hit:
 *   - the Draco GLB actually renders (the page and the model both load);
 *   - decoders are served from the SITE origin over https
 *     (/assets/decoders/** — no CDN, no http fallback);
 *   - the presigned S3 URL is https (S3_HTTPS_PORT) — a misconfigured
 *     http:// presigned URL would be blocked as mixed content;
 *   - no mixed-content / CORS / cert errors in the console at all;
 *   - HSTS is NOT sent in lan mode (production-only header, TLS_MODE=prod).
 *
 * The spec is skipped unless E2E_S3_HTTPS_URL is set (i.e. only under
 * `make e2e-https`), so plain `make e2e` runs are unaffected.
 */
import { expect, test } from '@playwright/test';
import { buildDracoCubeGlb } from '../src/test/gltf-fixture';
import {
  baseUrl,
  infoRow,
  openViewer,
  registerViaUi,
  uniqueLogin,
  uploadGlbBuffer,
  waitForDrawn,
} from './helpers';

const s3HttpsUrl = process.env.E2E_S3_HTTPS_URL;

test.skip(
  !s3HttpsUrl,
  'requires the https e2e stand (make e2e-https): E2E_S3_HTTPS_URL is not set',
);

test('a Draco cube opens in the viewer over https; decoders and S3 stay on https', async ({
  page,
}) => {
  const consoleErrors: string[] = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') consoleErrors.push(msg.text());
  });
  page.on('pageerror', (err) => consoleErrors.push(String(err)));

  const decoderUrls: string[] = [];
  const s3Urls: string[] = [];
  page.on('request', (req) => {
    const url = req.url();
    if (url.includes('/assets/decoders/')) decoderUrls.push(url);
    if (url.startsWith(s3HttpsUrl)) s3Urls.push(url);
  });

  // No HSTS in lan mode: the header is production-only (TLS_MODE=prod).
  const live = await page.request.get(`${baseUrl}/health/live`);
  expect(live.status()).toBe(200);
  expect(live.headers()['strict-transport-security']).toBeUndefined();

  // The GLB must be on S3 before the viewer opens it.
  const login = uniqueLogin('e2e_https');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const draco = await buildDracoCubeGlb();
  const model = await uploadGlbBuffer(page, 'draco-cube.glb', draco);

  await openViewer(page, model.id);
  await waitForDrawn(page);

  // The Draco cube renders (8 deduplicated vertices, 12 triangles).
  await expect(infoRow(page, 'Вершины')).toHaveText('Вершины8');
  await expect(infoRow(page, 'Треугольники')).toHaveText('Треугольники12');

  // Decoders were fetched — from the site origin, over https, no CDN.
  expect(decoderUrls.length).toBeGreaterThan(0);
  for (const url of decoderUrls) {
    const u = new URL(url);
    expect(u.origin).toBe(new URL(baseUrl).origin);
    expect(u.protocol).toBe('https:');
  }

  // The model came from the presigned S3 endpoint — over https only.
  expect(s3Urls.length).toBeGreaterThan(0);
  for (const url of s3Urls) {
    expect(new URL(url).protocol).toBe('https:');
  }

  // No mixed content, no CORS failures, no cert errors in the console.
  const bad = consoleErrors.filter((t) =>
    /mixed content|cors|access to (fetch|script|xmlhttprequest|style|websocket|link)|blocked/i.test(
      t,
    ),
  );
  expect(bad, `console errors: ${bad.join(' | ')}`).toEqual([]);
});

test('the root redirect preserves the https origin and non-standard port', async ({
  page,
}) => {
  // A fresh context (no session): the gateway 302-redirects / to /spa/ (a
  // RELATIVE Location) and the SPA root routes the guest to /spa/login.
  await page.goto('/');
  await expect(page).toHaveURL(/\/spa\//);

  // The browser followed the relative redirect and stayed on the very origin
  // of the stand: https scheme, host "localhost" and the non-standard port.
  // A Location built from the Gateway's internal host/port (or downgraded to
  // http) would break this (ADR 0016, 3d.1).
  const final = new URL(page.url());
  expect(final.origin).toBe(new URL(baseUrl).origin);
  expect(final.protocol).toBe('https:');
  expect(final.port).toBe(new URL(baseUrl).port);
});
