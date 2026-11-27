import { useQuery } from '@tanstack/react-query';
import { api } from './client';
import type { components } from './schema';

export type AuthUser = components['schemas']['AuthUserDto'];

type ProblemBody = components['schemas']['ProblemDetails'];

/** Query key for the current user (shared by useMe and login/logout updates). */
export const meQueryKey = ['me'] as const;

/**
 * Error for a non-2xx API response. Carries the status code and, when the
 * body is a ProblemDetails document (RFC 7807), its fields — the UI renders
 * `detail` and, for 400s, the `errors` map.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemBody | undefined,
  ) {
    super(problem?.detail ?? problem?.title ?? `HTTP ${status}`);
    this.name = 'ApiError';
  }

  /**
   * Per-field validation errors of a 400 ValidationProblemDetails, if any.
   * Keys are camelCased: .NET reports property names ("Login", "Password")
   * while the SPA fields use the DTO casing ("login", "password").
   */
  get fieldErrors(): Record<string, string[]> {
    const raw = this.problem?.errors ?? {};
    return Object.fromEntries(
      Object.entries(raw).map(([key, messages]) => [
        key.charAt(0).toLowerCase() + key.slice(1),
        messages,
      ]),
    );
  }
}

/** Extract the ProblemDetails body (RFC 7807) from an openapi-fetch error. */
export function problemFrom(value: unknown): ProblemBody | undefined {
  return value && typeof value === 'object' ? (value as ProblemBody) : undefined;
}

/**
 * POST /api/auth/login. The service sets the "jwt" cookie on success, so
 * the SPA does not store any token itself (ADR 0011). From step 4.3b the
 * Gateway routes this to the Auth service (before 4.3b — the monolith);
 * the generated schema describes the Auth shape in both cases.
 */
export async function login(login: string, password: string): Promise<AuthUser> {
  const { data, error, response } = await api.POST('/api/auth/login', {
    body: { login, password },
  });
  if (response.ok && data) {
    return data;
  }
  throw new ApiError(response.status, problemFrom(error));
}

/** POST /api/auth/register — 201 with the user; the cookie is set too. */
export async function register(input: {
  fullName: string;
  birthDate: string;
  login: string;
  password: string;
}): Promise<AuthUser> {
  const { data, error, response } = await api.POST('/api/auth/register', {
    body: input,
  });
  if (response.ok && data) {
    return data;
  }
  throw new ApiError(response.status, problemFrom(error));
}

/** POST /api/auth/logout — removes the cookie (idempotent, anonymous-safe). */
export async function logout(): Promise<void> {
  const { response } = await api.POST('/api/auth/logout');
  if (!response.ok) {
    throw new ApiError(response.status, undefined);
  }
}

/**
 * Current user from /api/me. Resolves to null when the browser is not
 * authenticated (401) — that is the SPA's "is logged in" signal.
 */
export function useMe() {
  return useQuery({
    queryKey: meQueryKey,
    retry: false,
    queryFn: async (): Promise<AuthUser | null> => {
      const { data, response } = await api.GET('/api/me');
      if (response.status === 401) {
        return null;
      }
      if (!response.ok) {
        throw new Error(`Unexpected status ${response.status} from /api/me`);
      }
      return data ?? null;
    },
  });
}
