/**
 * Step 3d.1 E2E: the Gateway's root redirect (GET/HEAD / -> 302 /spa/, a
 * RELATIVE Location) and the SPA root routing through /api/me.
 *
 * The https-specific assertion (the redirect preserves the origin and the
 * non-standard port behind Caddy) lives in https-viewer.spec.ts, which is
 * skipped outside the `make e2e-https` stand.
 */
import { expect, test } from '@playwright/test';

test('GET / redirects into the SPA and a guest lands on the login screen', async ({
  page,
}) => {
  // A fresh context has no session cookie: /api/me answers 401, so the SPA
  // root routes the anonymous visitor to the login screen.
  await page.goto('/');
  await expect(page).toHaveURL(/\/spa\/login/);
});
