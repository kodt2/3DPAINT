import type { Page } from '@playwright/test';
import { E2E_BASE_URL } from '../playwright.config';

/**
 * E2E helpers shared by auth.spec.ts and library.spec.ts.
 *
 * Every API call goes through the Gateway (the make-e2e stand's Gateway;
 * see the make target). Mutations to /api/** must carry an Origin header —
 * the Gateway's CSRF check (ADR 0011) rejects state-changing requests
 * without a matching Origin, exactly like a real browser page.
 */
export const baseUrl = E2E_BASE_URL;
/** The gateway's public origin (same as the page's own origin). */
export const origin = E2E_BASE_URL;

export function uniqueLogin(prefix = 'e2e'): string {
  return `${prefix}_${Date.now()}_${Math.floor(Math.random() * 100000)}`;
}

/**
 * Headers for API calls that need the session: the public Origin (CSRF
 * check) and the session cookie copied explicitly from the browser
 * context. The explicit Cookie header (instead of relying on the page's
 * API context cookie sync) makes the calls deterministic.
 */
export async function apiHeaders(page: Page): Promise<Record<string, string>> {
  const cookies = await page.context().cookies();
  return {
    origin,
    cookie: cookies.map((c) => `${c.name}=${c.value}`).join('; '),
  };
}

/**
 * Registers a new account through the SPA. Deliberately does NOT wait for
 * the result: callers assert the URL (success lands in /spa/library,
 * failures stay on /spa/register with the ProblemDetails message).
 */
export async function registerViaUi(page: Page, login: string) {
  await page.goto('/spa/register');
  await page.getByLabel('Полное имя').fill('E2E Tester');
  await page.getByLabel('Дата рождения').fill('1995-01-15');
  await page.getByLabel('Логин').fill(login);
  await page.getByLabel('Пароль').fill('e2e-secret-1');
  await page.getByRole('button', { name: 'Зарегистрироваться' }).click();
}

/** Logs in through the SPA (no waiting; callers assert the outcome). */
export async function loginViaUi(page: Page, login: string, password = 'e2e-secret-1') {
  await page.goto('/spa/login');
  await page.getByLabel('Логин').fill(login);
  await page.getByLabel('Пароль').fill(password);
  await page.getByRole('button', { name: 'Войти' }).click();
}

/**
 * Uploads a model through the legacy /api/files/upload endpoint (the only
 * upload path until the Models Service exists, Phase 5).
 */
export async function uploadModel(
  page: Page,
  fileName: string,
): Promise<{ id: string; name: string }> {
  const response = await page.request.post(`${baseUrl}/api/files/upload`, {
    headers: await apiHeaders(page),
    multipart: {
      file: { name: fileName, mimeType: 'model/gltf-binary', buffer: minimalGlb() },
    },
  });
  if (response.status() !== 200) {
    throw new Error(`upload failed: ${response.status()} ${await response.text()}`);
  }
  const body = (await response.json()) as { id: string; fileName: string };
  return { id: body.id, name: body.fileName };
}

/**
 * The smallest GLB the backend accepts: 12-byte header + one JSON chunk
 * (an empty asset), no BIN chunk. The backend only checks extension and
 * size (thumbnails are generated on the server side).
 */
function minimalGlb(): Buffer {
  const json = Buffer.from(JSON.stringify({ asset: { version: '2.0' } }), 'utf8');
  const jsonPadded = Math.ceil(json.length / 4) * 4;
  const total = 12 + 8 + jsonPadded;
  const buf = Buffer.alloc(total);
  buf.writeUInt32LE(0x46546c67, 0); // magic 'glTF'
  buf.writeUInt32LE(2, 4); // version
  buf.writeUInt32LE(total, 8); // total length
  buf.writeUInt32LE(jsonPadded, 12); // JSON chunk length
  buf.writeUInt32LE(0x4e4f534a, 16); // 'JSON'
  json.copy(buf, 20);
  if (jsonPadded > json.length) {
    buf.fill(0x20, 20 + json.length, 20 + jsonPadded); // space padding
  }
  return buf;
}
