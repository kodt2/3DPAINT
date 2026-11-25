import { expect, test } from '@playwright/test';
import {
  buildDracoCubeGlb,
  buildMeshoptCubeGlb,
  buildMeshoptQuantCubeGlb,
} from '../src/test/gltf-fixture';
import {
  baseUrl,
  expectOnlyLocalRequests,
  infoRow,
  openViewer,
  registerViaUi,
  trackRequests,
  uniqueLogin,
  uploadGlbBuffer,
  waitForDrawn,
} from './helpers';

/**
 * Step 3c.5 E2E: compressed GLB files (Draco, Meshopt, Meshopt+quantization)
 * upload through the legacy endpoint and render in the viewer, with all
 * decoders served from the local /assets/decoders/** path (no CDN, ADR 0014).
 *
 * Fixtures are built in memory by src/test/gltf-fixture.ts (no binary
 * fixtures in the repo). The cube topology is shared with glbfactory.ts:
 * 24 corner vertices, 12 triangles. Draco additionally welds the 24 corner
 * copies into 8 unique points — that is the expected info-panel row.
 */

test('a Draco GLB renders with the locally served draco decoder', async ({ page }) => {
  const login = uniqueLogin('e2e_draco');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadGlbBuffer(page, 'draco-cube.glb', await buildDracoCubeGlb());

  const requests = trackRequests(page);
  await openViewer(page, model.id);
  await waitForDrawn(page);

  // The decoded cube: draco welds the 24 corner copies into 8 unique
  // vertices; the triangle count is unchanged.
  await expect(infoRow(page, 'Вершины')).toHaveText('Вершины8');
  await expect(infoRow(page, 'Треугольники')).toHaveText('Треугольники12');
  await expect(infoRow(page, 'Мешей')).toHaveText('Мешей1');

  // The decoder assets came from the local /assets/decoders/** path, from
  // the same origin as the page (no CDN).
  const decoderRequests = requests.filter((url) => url.includes('/assets/decoders/'));
  expect(
    decoderRequests.some((url) => url.endsWith('draco/draco_wasm_wrapper.js')),
  ).toBe(true);
  expect(decoderRequests.some((url) => url.endsWith('draco/draco_decoder.wasm'))).toBe(true);
  for (const url of decoderRequests) {
    expect(new URL(url).origin).toBe(new URL(baseUrl).origin);
  }

  expectOnlyLocalRequests(requests);
});

test('a Meshopt GLB renders the original 24-vertex topology', async ({ page }) => {
  const login = uniqueLogin('e2e_meshopt');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadGlbBuffer(page, 'meshopt-cube.glb', await buildMeshoptCubeGlb());

  const requests = trackRequests(page);
  await openViewer(page, model.id);
  await waitForDrawn(page);

  // Meshopt decompression is lossless and does not weld vertices.
  await expect(infoRow(page, 'Вершины')).toHaveText('Вершины24');
  await expect(infoRow(page, 'Треугольники')).toHaveText('Треугольники12');
  await expect(infoRow(page, 'Мешей')).toHaveText('Мешей1');

  // The meshopt decoder ships inside the app bundle (inline wasm, Blob-URL
  // worker): no separate decoder asset requests are expected.
  expect(requests.filter((url) => url.includes('/assets/decoders/'))).toEqual([]);

  expectOnlyLocalRequests(requests);
});

test('a Meshopt+quantized GLB renders the quantized geometry', async ({ page }) => {
  const login = uniqueLogin('e2e_meshq');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadGlbBuffer(
    page,
    'meshopt-quant-cube.glb',
    await buildMeshoptQuantCubeGlb(),
  );

  const requests = trackRequests(page);
  await openViewer(page, model.id);
  await waitForDrawn(page);

  await expect(infoRow(page, 'Вершины')).toHaveText('Вершины24');
  await expect(infoRow(page, 'Треугольники')).toHaveText('Треугольники12');
  await expect(infoRow(page, 'Мешей')).toHaveText('Мешей1');

  expectOnlyLocalRequests(requests);
});

test('a Draco scene does not leak GPU resources on remount', async ({ page }) => {
  const login = uniqueLogin('e2e_draco_leak');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadGlbBuffer(page, 'draco-leak.glb', await buildDracoCubeGlb());

  await openViewer(page, model.id);
  await waitForDrawn(page);
  const first = await page.locator('.viewer-canvas-wrap').getAttribute('data-viewer-geometries');

  // Unmount (leave the page) and come back: the decoded geometry must be
  // released by disposeSceneObject exactly like a plain GLB's, and the
  // decoder worker pools are disposed by the loader on every parse settle.
  await page.goto('/spa/library');
  await expect(page).toHaveURL(/\/spa\/library/);
  await openViewer(page, model.id);
  await waitForDrawn(page);
  const second = await page.locator('.viewer-canvas-wrap').getAttribute('data-viewer-geometries');

  expect(Number(second)).toBeLessThanOrEqual(Number(first));
});

/**
 * A JSON-only GLB (no BIN chunk) whose required extension the viewer does
 * not know: the loader must reject it before any network decoder work and
 * name the extension in the friendly message.
 */
function buildUnsupportedExtensionGlb(extension: string): ArrayBuffer {
  const json = JSON.stringify({
    asset: { version: '2.0', generator: '3dpaint-e2e-unsupported' },
    extensionsRequired: [extension],
    extensionsUsed: [extension],
  });
  const jsonBytes = new TextEncoder().encode(json);
  const jsonPadded = Math.ceil(jsonBytes.length / 4) * 4;
  const total = 12 + 8 + jsonPadded;
  const bytes = new Uint8Array(total);
  const view = new DataView(bytes.buffer);
  view.setUint32(0, 0x46546c67, true); // magic 'glTF'
  view.setUint32(4, 2, true); // version
  view.setUint32(8, total, true); // total length
  view.setUint32(12, jsonPadded, true); // JSON chunk length
  view.setUint32(16, 0x4e4f534a, true); // 'JSON'
  bytes.set(jsonBytes, 20);
  for (let i = 20 + jsonBytes.length; i < total; i += 1) {
    bytes[i] = 0x20; // space padding per the glTF binary container spec
  }
  return bytes.buffer;
}

test('an unsupported required extension is rejected and named', async ({ page }) => {
  const login = uniqueLogin('e2e_unsup');
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  const model = await uploadGlbBuffer(
    page,
    'unsupported.glb',
    buildUnsupportedExtensionGlb('FOO_not_supported'),
  );

  const requests = trackRequests(page);
  await openViewer(page, model.id);
  await expect(
    page
      .getByRole('alert')
      .getByText('Файл использует glTF-расширение, которое вьюер не поддерживает: FOO_not_supported.'),
  ).toBeVisible();
  // No canvas for a rejected file.
  await expect(page.locator('.viewer-canvas-wrap canvas')).toHaveCount(0);

  // No decoder was even attempted for an unsupported extension.
  expect(requests.filter((url) => url.includes('/assets/decoders/'))).toEqual([]);
});
