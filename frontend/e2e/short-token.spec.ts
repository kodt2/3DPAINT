import { expect, test } from '@playwright/test';
import { apiHeaders, baseUrl, registerViaUi, uniqueLogin } from './helpers';

/**
 * Step 4.3b E2E, short-token stand only (Makefile `e2e-short`):
 *
 *   AUTH_JWT_EXPIRY_SECONDS=5   — Auth issues 5-second access tokens;
 *   JWT_CLOCK_SKEW_SECONDS=0    — the monolith validates with no tolerance,
 *                                 so an expired token really is a 401.
 *
 * The access token therefore expires mid-test and the SPA's refresh-on-401
 * flow (step 4.2) is exercised for real against the Auth service through the
 * Gateway: one shared POST /api/auth/refresh (single-flight), then the
 * request is retried with the new token — the user stays on the page, no
 * redirect to login.
 */

test.describe.configure({ timeout: 90_000 });

test('an expired 5s access token is refreshed transparently, without a redirect to login', async ({
  page,
}) => {
  const login = uniqueLogin('e2e_short');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  await expect(page.getByText('У вас пока нет моделей.')).toBeVisible();

  // Let the access token (5 s lifetime) expire.
  await page.waitForTimeout(7000);

  const refreshRequests: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/auth/refresh')) {
      refreshRequests.push(request.url());
    }
  });

  // A page load re-runs the protected /api/me: it 401s (expired token), the
  // single-flight handler performs ONE refresh, retries with the new token,
  // and the app stays on the library — not on /spa/login.
  await page.reload();
  await expect(page).toHaveURL(/\/spa\/library$/);
  await expect(page.getByText('У вас пока нет моделей.')).toBeVisible();

  await page.waitForTimeout(1500);
  expect(refreshRequests.length).toBe(1);

  // The refreshed session really works through the Gateway: /api/me with the
  // rotated jwt cookie answers 200.
  const me = await page.request.get(`${baseUrl}/api/me`, { headers: await apiHeaders(page) });
  expect(me.status()).toBe(200);
  expect(((await me.json()) as { login: string }).login).toBe(login);
});
