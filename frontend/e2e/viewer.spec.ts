import { expect, test, type Page } from '@playwright/test';
import { buildCorruptGlb, buildCubeGlb } from '../src/test/glbfactory';
import { apiHeaders, baseUrl, registerViaUi, uniqueLogin } from './helpers';

/**
 * Step 3c.3 E2E: the 3D viewer screen /spa/models/:id, exercised through
 * the Gateway with a real Chromium WebGL context.
 *
 * Test GLB files are built in memory by src/test/glbfactory.ts (no binary
 * fixtures in the repo). The cube carries a red PBR material so the
 * viewer's pixel probe (data-viewer-drawn) has something that differs from
 * the dark background to detect.
 */

const DRAWN_TIMEOUT = 30_000;

/** Uploads an arbitrary GLB payload under the given file name. */
async function uploadGlbBuffer(
  page: Page,
  fileName: string,
  glb: ArrayBuffer,
): Promise<{ id: string }> {
  const response = await page.request.post(`${baseUrl}/api/files/upload`, {
    headers: await apiHeaders(page),
    multipart: {
      file: {
        name: fileName,
        mimeType: 'model/gltf-binary',
        buffer: Buffer.from(glb),
      },
    },
  });
  if (response.status() !== 200) {
    throw new Error(`upload failed: ${response.status()} ${await response.text()}`);
  }
  const body = (await response.json()) as { id: string };
  return { id: body.id };
}

async function openViewer(page: Page, id: string) {
  await page.goto(`/spa/models/${id}`);
}

async function waitForDrawn(page: Page) {
  await expect
    .poll(async () => page.locator('.viewer-canvas-wrap').getAttribute('data-viewer-drawn'), {
      timeout: DRAWN_TIMEOUT,
    })
    .toBe('true');
}

test('the viewer renders a GLB and the info panel reports the real geometry', async ({ page }) => {
  const login = uniqueLogin('e2e_view');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadGlbBuffer(page, 'cube.glb', buildCubeGlb({ withMaterial: true }));

  await openViewer(page, model.id);
  await waitForDrawn(page);

  const panel = page.getByRole('complementary', { name: 'Информация о модели' });
  await expect(panel).toBeVisible();
  const row = (label: string) =>
    panel.locator('.viewer-info-list > div').filter({ hasText: label });
  await expect(row('Вершины')).toHaveText('Вершины24');
  await expect(row('Треугольники')).toHaveText('Треугольники12');
  await expect(row('Мешей')).toHaveText('Мешей1');
  await expect(row('Материалов')).toHaveText('Материалов1');
  await expect(row('Текстур')).toHaveText('Текстур0');
  await expect(row('UV')).toHaveText('UVда');
});

test('GPU resources are not leaked when the scene is remounted', async ({ page }) => {
  const login = uniqueLogin('e2e_leak');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadGlbBuffer(page, 'leak-cube.glb', buildCubeGlb({ withMaterial: true }));

  await openViewer(page, model.id);
  await waitForDrawn(page);
  const first = await page.locator('.viewer-canvas-wrap').getAttribute('data-viewer-geometries');

  await page.goto('/spa/library');
  await expect(page).toHaveURL(/\/spa\/library/);
  await openViewer(page, model.id);
  await waitForDrawn(page);
  const second = await page.locator('.viewer-canvas-wrap').getAttribute('data-viewer-geometries');

  // The renderer.info counter must not grow across visits: the previous
  // scene's buffers were released on unmount.
  expect(Number(second)).toBeLessThanOrEqual(Number(first));
});

test('a corrupt file shows the friendly parse error', async ({ page }) => {
  const login = uniqueLogin('e2e_badglb');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadGlbBuffer(page, 'corrupt.glb', buildCorruptGlb());

  await openViewer(page, model.id);
  await expect(
    page.getByRole('alert').getByText('Файл повреждён или не является корректным GLB.'),
  ).toBeVisible();
  // No rendered scene and no info panel for a broken file.
  await expect(page.locator('.viewer-canvas-wrap canvas')).toHaveCount(0);
  await expect(page.getByRole('complementary')).toHaveCount(0);
});

test('another user gets "Модель не найдена" for a foreign model', async ({ page, browser }) => {
  const loginA = uniqueLogin('e2e_view_a');
  await registerViaUi(page, loginA);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadGlbBuffer(page, 'foreign.glb', buildCubeGlb());

  const contextB = await browser.newContext();
  const pageB = await contextB.newPage();
  const loginB = uniqueLogin('e2e_view_b');
  await registerViaUi(pageB, loginB);
  await expect(pageB).toHaveURL(/\/spa\/library/);

  await pageB.goto(`/spa/models/${model.id}`);
  await expect(pageB.getByRole('heading', { name: 'Модель не найдена' })).toBeVisible();

  await contextB.close();
});

test('a non-GLB model shows the notice and the download link', async ({ page }) => {
  const login = uniqueLogin('e2e_stl');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);

  // The monolith whitelist includes .stl; the smallest valid ASCII STL.
  const stl = Buffer.from(
    [
      'solid e2e',
      '  facet normal 0 0 1',
      '    outer loop',
      '      vertex 0 0 0',
      '      vertex 1 0 0',
      '      vertex 0 1 0',
      '    endloop',
      '  endfacet',
      'endsolid e2e',
      '',
    ].join('\n'),
    'utf8',
  );
  const response = await page.request.post(`${baseUrl}/api/files/upload`, {
    headers: await apiHeaders(page),
    multipart: {
      file: { name: 'tri.stl', mimeType: 'model/stl', buffer: stl },
    },
  });
  expect(response.status()).toBe(200);
  const model = (await response.json()) as { id: string };

  await openViewer(page, model.id);
  await expect(
    page.getByRole('status').getByText('Просмотр 3D недоступен для формата .stl.'),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: 'Скачать' })).toBeVisible();
  await expect(page.locator('.viewer-canvas-wrap')).toHaveCount(0);
});
