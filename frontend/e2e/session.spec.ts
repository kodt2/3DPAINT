import { expect, test } from '@playwright/test';

/**
 * Step 4.2 E2E: a session that cannot be refreshed must land on
 * /spa/login — and stop there, without a refresh/redirect loop.
 *
 * While the monolith does not implement /api/auth/refresh yet (step 4.3),
 * the refresh answers non-2xx. The expected flow:
 *
 *   /spa/library -> /api/me 401 -> one refresh (fails) -> /spa/login
 *   /spa/login   -> /api/me 401 -> one refresh (fails) -> stop (the app is
 *                 already on the login page; the form is shown).
 *
 * So the refresh fires at most twice and the page must become stable.
 */
test.describe.configure({ timeout: 60_000 });

test('an unauthenticated /spa/library visit redirects to /spa/login and stops', async ({
  page,
}) => {
  const refreshRequests: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/auth/refresh')) {
      refreshRequests.push(request.url());
    }
  });

  // Fresh context, no cookies.
  await page.goto('/spa/library');

  // The redirect landed on the login page and the form rendered...
  await expect(page).toHaveURL(/\/spa\/login$/);
  await expect(page.getByRole('heading', { name: 'Вход' })).toBeVisible();

  // ...and the page is STABLE: the single-flight handler fired a bounded
  // number of refreshes (one per page load) and nothing keeps reloading.
  await page.waitForTimeout(2000);
  expect(refreshRequests.length).toBeLessThanOrEqual(2);
  const stableCount = refreshRequests.length;
  await page.waitForTimeout(2000);
  expect(refreshRequests.length).toBe(stableCount);
});
