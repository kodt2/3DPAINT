import { api } from './client';
import type { components } from './schema';

/**
 * Client configuration served by GET /api/config (step 3c.4): the upload
 * limit (the same MAX_UPLOAD_BYTES the server enforces, ADR 0015) and the
 * extensions the SPA upload accepts (.glb only).
 */
export type AppConfig = components['schemas']['ConfigDto'];

export const appConfigQueryKey = ['appConfig'] as const;

/**
 * Fetches the app config once and caches the promise: the values do not
 * change while the page is open.
 */
let configPromise: Promise<AppConfig> | null = null;

export function getAppConfig(): Promise<AppConfig> {
  configPromise ??= (async () => {
    const { data, error, response } = await api.GET('/api/config');
    if (error || response.status >= 400 || !data) {
      throw new Error('Не удалось получить настройки загрузки.');
    }
    if (typeof data.maxUploadBytes !== 'number' || data.maxUploadBytes <= 0) {
      throw new Error('Не удалось получить настройки загрузки.');
    }
    return data;
  })();
  return configPromise;
}

/** Test helper: forget the cached config so the next call refetches. */
export function resetAppConfigCache(): void {
  configPromise = null;
}
