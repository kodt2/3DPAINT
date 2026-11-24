import { createElement, type ReactElement } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { render } from '@testing-library/react';

/**
 * Shared Vitest helpers for the auth screens (no components in this file:
 * react-refresh requires component files to export only components).
 *
 * The router is a MemoryRouter (unit tests must not depend on
 * window.location); the QueryClient never retries so a mocked 401 stays a
 * 401.
 */
export function renderWithProviders(ui: ReactElement, initialEntries: string[] = ['/']) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  // createElement instead of JSX: this file is .ts (no JSX syntax).
  return render(
    createElement(
      QueryClientProvider,
      { client: queryClient },
      createElement(MemoryRouter, { initialEntries }, ui),
    ),
  );
}

/** openapi-fetch shaped 2xx result. */
export function okResult(data: unknown, status = 200) {
  return {
    response: { ok: true, status, statusText: 'OK', headers: new Headers() },
    data,
  };
}

/** openapi-fetch shaped error result with a ProblemDetails body. */
export function errorResult(status: number, problem: Record<string, unknown>) {
  return {
    response: { ok: false, status, statusText: '', headers: new Headers() },
    error: problem,
  };
}
