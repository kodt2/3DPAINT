import { useQuery } from '@tanstack/react-query';
import { api } from './client';
import type { components } from './schema';
import { ApiError, problemFrom } from './auth';

export type Model = components['schemas']['ModelDto'];
export type ModelDetail = components['schemas']['ModelDetailDto'];
export type ModelsPage = components['schemas']['ModelsPageDto'];

/**
 * Default page size for the library; must match
 * ModelsApiController.DefaultPageSize (the server rejects pageSize > 100).
 */
export const DEFAULT_PAGE_SIZE = 20;

/** Shared prefix of all model-list query keys (used for invalidation). */
export const modelsListKey = ['models'] as const;

/** GET /api/models — the current user's models, one page at a time. */
export function useModels(page: number, pageSize = DEFAULT_PAGE_SIZE) {
  return useQuery({
    queryKey: [...modelsListKey, { page, pageSize }],
    queryFn: async (): Promise<ModelsPage> => {
      const { data, error, response } = await api.GET('/api/models', {
        params: { query: { page, pageSize } },
      });
      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, problemFrom(error));
      }
      return data;
    },
  });
}

/**
 * GET /api/models/{id} — a single model (owner-scoped, ADR 0013):
 * a model that does not exist or belongs to another user is a 404.
 */
export async function getModel(id: string): Promise<ModelDetail> {
  const { data, error, response } = await api.GET('/api/models/{id}', {
    params: { path: { id } },
  });
  if (!response.ok || data === undefined) {
    throw new ApiError(response.status, problemFrom(error));
  }
  return data;
}

/**
 * DELETE /api/models/{id} — 204 on success. 404 means the model is gone
 * (or, by the owner-scoped contract, never belonged to this user).
 */
export async function deleteModel(id: string): Promise<void> {
  const { error, response } = await api.DELETE('/api/models/{id}', {
    params: { path: { id } },
  });
  if (!response.ok) {
    throw new ApiError(response.status, problemFrom(error));
  }
}
