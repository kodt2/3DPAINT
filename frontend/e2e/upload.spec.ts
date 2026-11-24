import { expect, test, type Page } from '@playwright/test';
import { buildCubeGlb, buildCorruptGlb, buildLargeGlb } from '../src/test/glbfactory';
import { registerViaUi, uniqueLogin } from './helpers';

// Uploading a ~22 MB file under CDP throttling is the slow part.
test.describe.configure({ timeout: 90_000 });

const INPUT_LABEL = 'Файлы для загрузки (GLB)';
const LIBRARY_EMPTY = 'У вас пока нет моделей.';

async function pickFile(page: Page, name: string, buffer: Buffer) {
  await page.getByLabel(INPUT_LABEL).setInputFiles({
    name,
    mimeType: 'model/gltf-binary',
    buffer,
  });
}

/** Upload requests issued by the browser (XHR shows up as 'xhr' resource type). */
function trackUploadRequests(page: Page): string[] {
  const urls: string[] = [];
  page.on('request', (req) => {
    if (req.url().includes('/api/files/upload')) {
      urls.push(req.url());
    }
  });
  return urls;
}

test.describe('SPA upload panel (step 3c.4)', () => {
  test('uploads a valid GLB and the library shows the new model', async ({ page }) => {
    await registerViaUi(page, uniqueLogin('e2e_up_ok'));
    await expect(page).toHaveURL(/\/spa\/library/);

    await pickFile(page, 'upload-ok.glb', Buffer.from(buildCubeGlb()));

    await expect(page.getByText('Готово').first()).toBeVisible();
    // The panel invalidates the models query: the card appears without a reload.
    await expect(page.getByText('upload-ok.glb').first()).toBeVisible();
    await expect(page.getByRole('link', { name: 'Открыть 3D' })).toBeVisible();
  });

  test('uploads two files and shows both in the library', async ({ page }) => {
    await registerViaUi(page, uniqueLogin('e2e_up_two'));
    await expect(page).toHaveURL(/\/spa\/library/);

    await pickFile(page, 'two-a.glb', Buffer.from(buildCubeGlb()));
    await pickFile(page, 'two-b.glb', Buffer.from(buildCubeGlb()));

    await expect(page.getByText('two-a.glb').first()).toBeVisible();
    await expect(page.getByText('two-b.glb').first()).toBeVisible();
    expect(await page.getByText('Готово').count()).toBe(2);
  });

  test('rejects an oversized file locally and never sends it', async ({ page }) => {
    await registerViaUi(page, uniqueLogin('e2e_up_big'));
    await expect(page).toHaveURL(/\/spa\/library/);

    const requests = trackUploadRequests(page);
    // The e2e stand limit is 32 MiB; 40 MiB must be stopped by the client.
    const oversized = Buffer.alloc(40 * 1024 * 1024, 1);
    await pickFile(page, 'too-big.glb', oversized);

    await expect(page.getByText(/лимит/).first()).toBeVisible();
    expect(requests).toHaveLength(0);
    await expect(page.getByText(LIBRARY_EMPTY)).toBeVisible();
  });

  test('rejects a non-GLB file locally', async ({ page }) => {
    await registerViaUi(page, uniqueLogin('e2e_up_stl'));
    await expect(page).toHaveURL(/\/spa\/library/);

    const requests = trackUploadRequests(page);
    await pickFile(page, 'model.stl', Buffer.from('solid e2e\nendsolid e2e\n'));

    await expect(page.getByText('Допускается только формат .glb.').first()).toBeVisible();
    expect(requests).toHaveLength(0);
  });

  test('a corrupt GLB gets the server 400 message and no retry offer', async ({ page }) => {
    await registerViaUi(page, uniqueLogin('e2e_up_corrupt'));
    await expect(page).toHaveURL(/\/spa\/library/);

    await pickFile(page, 'corrupt-upload.glb', Buffer.from(buildCorruptGlb()));

    // The server's ProblemDetails detail, mapped by the panel.
    await expect(
      page.getByText('Файл не является GLB: заголовок не соответствует контейнеру glTF.').first(),
    ).toBeVisible();
    // 400 is a content error: no retry button, the library stays empty.
    expect(await page.getByRole('button', { name: 'Повторить' }).count()).toBe(0);
    await expect(page.getByText(LIBRARY_EMPTY)).toBeVisible();
  });

  test('cancels a mid-upload and the model never appears', async ({ page }) => {
    await registerViaUi(page, uniqueLogin('e2e_up_cancel'));
    await expect(page).toHaveURL(/\/spa\/library/);

    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Network.enable');
    // ~1.5 MiB/s: a 22 MiB file takes ~15 s to send — plenty of time to cancel.
    await cdp.send('Network.emulateNetworkConditions', {
      offline: false,
      latency: 0,
      downloadThroughput: -1,
      uploadThroughput: 1.5 * 1024 * 1024,
    });

    try {
      // 22 MiB < the 32 MiB stand limit, so the client lets it through.
      const big = buildLargeGlb({ vertexCount: 1_000_000, triangleCount: 800_000 });
      await pickFile(page, 'cancel-me.glb', Buffer.from(big));

      await expect(page.getByText(/Загрузка…/)).toBeVisible();
      await page.getByRole('button', { name: 'Отменить' }).first().click();
      await expect(page.getByText('Отменено').first()).toBeVisible();
    } finally {
      await cdp.send('Network.emulateNetworkConditions', {
        offline: false,
        latency: 0,
        downloadThroughput: -1,
        uploadThroughput: -1,
      });
    }

    // No trace of the cancelled upload: the library stays empty (the server
    // never finished receiving the body, so no S3 object and no DB record).
    await page.reload();
    await expect(page.getByText(LIBRARY_EMPTY)).toBeVisible();
    expect(await page.getByText('cancel-me.glb').count()).toBe(0);
  });

  test('an expired session sends the user to the login screen', async ({ page }) => {
    await registerViaUi(page, uniqueLogin('e2e_up_expired'));
    await expect(page).toHaveURL(/\/spa\/library/);

    await page.context().clearCookies();
    await pickFile(page, 'after-expiry.glb', Buffer.from(buildCubeGlb()));

    // The 401 on the XHR redirects the whole app to /spa/login.
    await expect(page).toHaveURL(/\/spa\/login/, { timeout: 15_000 });
  });
});
