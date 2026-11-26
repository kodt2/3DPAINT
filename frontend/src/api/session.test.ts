import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  _resetSessionStateForTests,
  sessionFetch,
  sessionRedirect,
} from './session';

const ORIGIN = 'http://localhost';

/** A minimal JSON Response helper for the fetch mock. */
function json(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('session single-flight 401 handler (step 4.2)', () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let refreshCalls: number;

  beforeEach(() => {
    _resetSessionStateForTests();
    refreshCalls = 0;
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('10 concurrent 401s trigger exactly one refresh and one retry each', async () => {
    // Requests before the refresh resolves are 401 (stale access token);
    // requests after it (the retries) succeed.
    let refreshDone = false;
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      const url = new URL(input instanceof Request ? input.url : String(input), ORIGIN);
      if (url.pathname === '/api/auth/refresh') {
        refreshCalls += 1;
        // A realistic delay so that all 10 original 401s land in the SAME
        // flight and share this single refresh call.
        await new Promise((r) => setTimeout(r, 50));
        refreshDone = true;
        return json(200, { login: 'user' });
      }
      return refreshDone ? json(200, { ok: true }) : json(401);
    });

    const results = await Promise.all(
      Array.from({ length: 10 }, () =>
        sessionFetch(new Request(`${ORIGIN}/api/me`)),
      ),
    );

    expect(refreshCalls).toBe(1);
    for (const res of results) {
      expect(res.status).toBe(200);
    }
    // 10 originals + 1 refresh + 10 retries.
    expect(fetchMock).toHaveBeenCalledTimes(21);
  });

  it('a failed refresh redirects to /spa/login exactly once, no loop', async () => {
    const toLoginSpy = vi
      .spyOn(sessionRedirect, 'toLogin')
      .mockImplementation(() => {});
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      const url = new URL(input instanceof Request ? input.url : String(input), ORIGIN);
      if (url.pathname === '/api/auth/refresh') {
        refreshCalls += 1;
        // The monolith (until step 4.3) does not know /api/auth/refresh.
        return json(404, {});
      }
      return json(401);
    });

    const results = await Promise.all(
      Array.from({ length: 5 }, () =>
        sessionFetch(new Request(`${ORIGIN}/api/models`)),
      ),
    );

    // One shared refresh (all 401s landed in the same flight)...
    expect(refreshCalls).toBe(1);
    // ...and exactly one redirect, even though five requests failed.
    expect(toLoginSpy).toHaveBeenCalledTimes(1);
    for (const res of results) {
      expect(res.status).toBe(401);
    }
  });

  it('a retry that is 401 again is final: no second refresh round', async () => {
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      const url = new URL(input instanceof Request ? input.url : String(input), ORIGIN);
      if (url.pathname === '/api/auth/refresh') {
        refreshCalls += 1;
        return json(200);
      }
      return json(401);
    });

    const res = await sessionFetch(new Request(`${ORIGIN}/api/me`));

    expect(res.status).toBe(401);
    expect(refreshCalls).toBe(1);
    // original + refresh + one retry
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('non-401 responses pass through without a refresh', async () => {
    fetchMock.mockResolvedValue(json(500, { title: 'boom' }));

    const res = await sessionFetch(new Request(`${ORIGIN}/api/models`));

    expect(res.status).toBe(500);
    expect(refreshCalls).toBe(0);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('401s under /api/auth/** are never intercepted', async () => {
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      const url = new URL(input instanceof Request ? input.url : String(input), ORIGIN);
      expect(url.pathname.startsWith('/api/auth/')).toBe(true);
      return json(401);
    });

    const login = await sessionFetch(new Request(`${ORIGIN}/api/auth/login`, {
      method: 'POST',
    }));
    const refresh = await sessionFetch(new Request(`${ORIGIN}/api/auth/refresh`, {
      method: 'POST',
    }));

    expect(login.status).toBe(401);
    expect(refresh.status).toBe(401);
    expect(refreshCalls).toBe(0);
    // login + refresh, no interception in between
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
