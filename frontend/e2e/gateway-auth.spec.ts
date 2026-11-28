import { expect, test } from '@playwright/test';
import { buildCubeGlb } from '../src/test/glbfactory';
import {
  apiHeaders,
  baseUrl,
  loginViaUi,
  openViewer,
  registerViaUi,
  uniqueLogin,
  uploadGlbBuffer,
  waitForDrawn,
} from './helpers';

/**
 * Step 4.3b E2E: the full session lifecycle through the Gateway now that
 * /api/auth/** and /api/me are served by the Auth service (RS256, GUID sub)
 * and the rest of /api/** by the monolith.
 */

test.describe.configure({ timeout: 120_000 });

test('full cycle through the Gateway: register -> library -> upload -> view -> logout -> 401 -> login', async ({
  page,
}) => {
  const login = uniqueLogin('e2e_cycle');

  // Register through the Gateway (the route now lands on Auth, not the
  // monolith); the SPA auto-logs in on success.
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  await expect(page.getByText('У вас пока нет моделей.')).toBeVisible();

  // /api/me is served by Auth through the Gateway: it reports the Auth user.
  let me = await page.request.get(`${baseUrl}/api/me`, { headers: await apiHeaders(page) });
  expect(me.status()).toBe(200);
  expect(((await me.json()) as { login: string }).login).toBe(login);

  // Upload + view: the monolith routes, still behind the same Gateway.
  const model = await uploadGlbBuffer(page, 'cycle-cube.glb', buildCubeGlb({ withMaterial: true }));
  await openViewer(page, model.id);
  await waitForDrawn(page);

  // Logout: POST /api/auth/logout -> Auth through the Gateway, the SPA lands
  // on the login screen.
  await page.goto('/spa/library');
  await page.getByRole('button', { name: 'Выйти' }).click();
  await expect(page).toHaveURL(/\/spa\/login/);

  // The session is really gone: /api/me through the Gateway answers 401.
  me = await page.request.get(`${baseUrl}/api/me`, { headers: await apiHeaders(page) });
  expect(me.status()).toBe(401);

  // Login again through the Gateway: the library comes back with the model.
  await loginViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  await expect(page.getByText('cycle-cube.glb')).toBeVisible();
});

test('five failed logins lock the account; the correct password still gets the same 401', async ({
  page,
}) => {
  const login = uniqueLogin('e2e_lock');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  await page.getByRole('button', { name: 'Выйти' }).click();
  await expect(page).toHaveURL(/\/spa\/login/);

  const postLogin = async (password: string) => {
    const response = await page.request.post(`${baseUrl}/api/auth/login`, {
      headers: await apiHeaders(page),
      data: { login, password },
    });
    // The 401 ProblemDetails carries a per-request traceId (correlation id),
    // which legitimately differs between requests: the enumeration check is
    // on the rest of the body.
    const body = (await response.json()) as {
      type: string;
      title: string;
      status: number;
      detail: string;
      traceId?: string;
    };
    return { status: response.status(), body };
  };

  // Five wrong passwords: every 401 body is identical apart from the
  // per-request traceId (no user/state enumeration through the response).
  const wrongBodies: Array<Record<string, unknown>> = [];
  for (let i = 1; i <= 5; i++) {
    const result = await postLogin(`wrong-password-${i}`);
    expect(result.status).toBe(401);
    const { traceId: _ignored, ...withoutTrace } = result.body;
    wrongBodies.push(withoutTrace);
  }
  for (let i = 1; i < 5; i++) {
    expect(wrongBodies[i]).toEqual(wrongBodies[0]);
  }

  // The account is locked now: even the CORRECT password answers with the
  // same 401 body as a wrong one (step 4.2 lockout, served by Auth through
  // the Gateway).
  const locked = await postLogin('e2e-secret-1');
  expect(locked.status).toBe(401);
  const { traceId: _ignoredLocked, ...lockedWithoutTrace } = locked.body;
  expect(lockedWithoutTrace).toEqual(wrongBodies[0]);
});
