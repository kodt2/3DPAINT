import { expect, test } from '@playwright/test';
import { apiHeaders, baseUrl, registerViaUi, uniqueLogin, uploadModel } from './helpers';

/**
 * Step 3c.2 E2E: the library screen against /api/models through the
 * Gateway. Every test uses a fresh browser context and a unique login, so
 * tests never depend on each other or on leftover data.
 */

test('models uploaded by the user are listed in the library', async ({ page }) => {
  const login = uniqueLogin('e2e_lib');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  await expect(page.getByText('У вас пока нет моделей.')).toBeVisible();

  await uploadModel(page, 'alpha.glb');
  await uploadModel(page, 'beta.glb');

  await page.reload();
  await expect(page.getByText('alpha.glb')).toBeVisible();
  await expect(page.getByText('beta.glb')).toBeVisible();
  expect(await page.locator('.library-card').count()).toBe(2);
});

test('the model list DTO contains no internal addresses, S3 keys or owner data', async ({
  page,
}) => {
  const login = uniqueLogin('e2e_dto');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  await uploadModel(page, 'dto-check.glb');

  const response = await page.request.get(`${baseUrl}/api/models`, {
    headers: await apiHeaders(page),
  });
  expect(response.status()).toBe(200);
  const body = (await response.json()) as {
    page: number;
    pageSize: number;
    totalCount: number;
    items: Array<Record<string, unknown>>;
  };

  // Page metadata plus exactly the card fields — nothing else.
  expect(body.page).toBe(1);
  expect(body.totalCount).toBe(1);
  expect(Object.keys(body.items[0]).sort()).toEqual(
    ['createdAt', 'id', 'name', 'sizeBytes', 'thumbnailUrl'].sort(),
  );

  const raw = JSON.stringify(body);
  // No internal compose host (e.g. "storage:9000"), no raw S3 object key as
  // a field, no owner/user data in the model DTO. The presigned thumbnail
  // URL is the public endpoint on purpose (the browser loads it directly).
  expect(raw).not.toContain('storage:');
  expect(raw).not.toContain('storagePath');
  expect(raw).not.toContain('thumbnailPath');
  expect(raw).not.toContain('user_id');
  expect(raw).not.toContain('login');
  expect(raw).not.toContain('fullName');
});

test('another user cannot read or delete a foreign model (404, no leak)', async ({
  page,
  browser,
}) => {
  const loginA = uniqueLogin('e2e_idor_a');
  await registerViaUi(page, loginA);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadModel(page, 'a-model.glb');

  // User B: a separate context with its own cookie.
  const contextB = await browser.newContext();
  const pageB = await contextB.newPage();
  const loginB = uniqueLogin('e2e_idor_b');
  await registerViaUi(pageB, loginB);
  await expect(pageB).toHaveURL(/\/spa\/library/);

  // B sees no models of A at all.
  const list = (await (
    await pageB.request.get(`${baseUrl}/api/models`, { headers: await apiHeaders(pageB) })
  ).json()) as { items: unknown[] };
  expect(list.items).toHaveLength(0);

  // B cannot read or delete A's model: a plain 404, same as a missing id.
  expect(
    (
      await pageB.request.get(`${baseUrl}/api/models/${model.id}`, {
        headers: await apiHeaders(pageB),
      })
    ).status(),
  ).toBe(404);
  expect(
    (
      await pageB.request.delete(`${baseUrl}/api/models/${model.id}`, {
        headers: await apiHeaders(pageB),
      })
    ).status(),
  ).toBe(404);

  // A's model is intact.
  expect(
    (
      await page.request.get(`${baseUrl}/api/models/${model.id}`, {
        headers: await apiHeaders(page),
      })
    ).status(),
  ).toBe(200);

  await contextB.close();
});

test('deleting a model from the library removes the record and the S3 objects', async ({
  page,
}) => {
  const login = uniqueLogin('e2e_del');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadModel(page, 'to-delete.glb');
  await page.reload();
  await expect(page.getByText('to-delete.glb')).toBeVisible();

  // Grab the presigned URLs before the delete; they must stop resolving
  // afterwards (the S3 objects are really gone, not just the DB row).
  const detail = (await (
    await page.request.get(`${baseUrl}/api/models/${model.id}`, {
      headers: await apiHeaders(page),
    })
  ).json()) as { downloadUrl: string; thumbnailUrl: string | null };

  const card = page.locator('.library-card', { hasText: 'to-delete.glb' });
  await card.getByRole('button', { name: 'Удалить' }).click();
  await card.getByRole('button', { name: 'Да, удалить' }).click();
  await expect(page.getByText('to-delete.glb')).toBeHidden();
  await expect(page.getByText('У вас пока нет моделей.')).toBeVisible();

  // The record is gone: GET and a repeated DELETE are 404 (idempotency).
  expect(
    (
      await page.request.get(`${baseUrl}/api/models/${model.id}`, {
        headers: await apiHeaders(page),
      })
    ).status(),
  ).toBe(404);
  expect(
    (
      await page.request.delete(`${baseUrl}/api/models/${model.id}`, {
        headers: await apiHeaders(page),
      })
    ).status(),
  ).toBe(404);

  // The S3 objects are gone too (the old presigned URLs no longer resolve).
  expect((await page.request.get(detail.downloadUrl)).status()).toBeGreaterThanOrEqual(400);
  if (detail.thumbnailUrl) {
    expect((await page.request.get(detail.thumbnailUrl)).status()).toBeGreaterThanOrEqual(400);
  }
});

test('the library is paginated (page/pageSize) and walks pages', async ({ page }) => {
  test.setTimeout(180_000);

  const login = uniqueLogin('e2e_page');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);

  // 21 models: two pages of 20. The list is newest-first, so the last
  // page holds the single oldest model (model-01).
  for (let i = 1; i <= 21; i++) {
    await uploadModel(page, `model-${String(i).padStart(2, '0')}.glb`);
  }

  await page.reload();
  await expect(page.getByText('Стр. 1 из 2')).toBeVisible();
  expect(await page.locator('.library-card').count()).toBe(20);
  expect(page.getByRole('button', { name: '← Предыдущая' })).toBeDisabled();

  await page.getByRole('button', { name: 'Следующая →' }).click();
  await expect(page.getByText('Стр. 2 из 2')).toBeVisible();
  expect(await page.locator('.library-card').count()).toBe(1);
  await expect(page.getByText('model-01.glb')).toBeVisible();

  await page.getByRole('button', { name: '← Предыдущая' }).click();
  await expect(page.getByText('Стр. 1 из 2')).toBeVisible();
  expect(await page.locator('.library-card').count()).toBe(20);
});

test('pageSize beyond 100 is rejected with 400', async ({ page }) => {
  const login = uniqueLogin('e2e_page');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);

  const response = await page.request.get(`${baseUrl}/api/models?pageSize=1000`, {
    headers: await apiHeaders(page),
  });
  expect(response.status()).toBe(400);
});
