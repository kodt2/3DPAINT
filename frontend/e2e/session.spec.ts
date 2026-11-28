import { expect, test } from '@playwright/test';

/**
 * Step 4.2 E2E (updated for 4.3b): a session that cannot be refreshed must
 * land on /spa/login — and stop there, without a refresh/redirect loop.
 *
 * Since step 4.3b the refresh is served by the Auth service through the
 * Gateway: with no rt cookie it answers exactly 401 (the same ProblemDetails
 * body as any other refresh failure). The expected flow:
 *
 *   /spa/library -> /api/me 401 -> one refresh (401, no rt) -> /spa/login
 *   /spa/login   -> /api/me 401 -> one refresh (401, no rt) -> stop (the app
 *                 is already on the login page; the form is shown).
 *
 * So the refresh fires at most twice, every answer is a 401 from Auth, and
 * the page must become stable.
 */
test.describe.configure({ timeout: 60_000 });

test('an unauthenticated /spa/library visit redirects to /spa/login and stops', async ({
  page,
}) => {
  const refreshRequests: string[] = [];
  const refreshStatuses: number[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/auth/refresh')) {
      refreshRequests.push(request.url());
    }
  });
  page.on('response', (response) => {
    if (response.url().includes('/api/auth/refresh')) {
      refreshStatuses.push(response.status());
    }
  });

  // Fresh context, no cookies.
  await page.goto('/spa/library');

  // The redirect landed on the login page and the form rendered...
  await expect(page).toHaveURL(/\/spa\/login$/);
  await expect(page.getByRole('heading', { name: 'Вход' })).toBeVisible();

  // ...the refresh went to the Auth service through the Gateway and was
  // answered with exactly 401 (no rt cookie)...
  expect(refreshStatuses.length).toBeGreaterThan(0);
  expect(refreshStatuses.every((status) => status === 401)).toBe(true);

  // ...and the page is STABLE: the single-flight handler fired a bounded
  // number of refreshes (one per page load) and nothing keeps reloading.
  await page.waitForTimeout(2000);
  expect(refreshRequests.length).toBeLessThanOrEqual(2);
  const stableCount = refreshRequests.length;
  await page.waitForTimeout(2000);
  expect(refreshRequests.length).toBe(stableCount);
});
