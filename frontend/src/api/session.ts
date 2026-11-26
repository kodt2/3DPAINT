/**
 * Single-flight session refresh on 401 (step 4.2).
 *
 * The API client (openapi-fetch) is wired with `customFetch: sessionFetch`,
 * so every request made through `api` passes this module. When a protected
 * request comes back 401:
 *
 *   1. All concurrent 401s share ONE `POST /api/auth/refresh` call
 *      (single-flight: the in-flight promise is reused, then cleared).
 *   2. On success the original request is retried EXACTLY ONCE using a
 *      pre-cloned copy (the first send may have consumed its body). The
 *      retry goes through the raw fetch, not this module, so a second 401
 *      can never start another refresh round.
 *   3. On any refresh failure (non-2xx or network error) the app redirects
 *      to /spa/login exactly once (a latch, not a loop). The redirect is
 *      skipped when the app is already on /spa/login or /spa/register:
 *      those pages call /api/me too ("already signed in?" probe), and a
 *      failed refresh there must not bounce the page into a reload cycle.
 *      While the monolith does not implement /api/auth/refresh yet (that is
 *      step 4.3), the refresh answers non-2xx and the observable behaviour
 *      stays equivalent to today's "401 -> login screen".
 *
 * Requests under /api/auth/** (login, register, refresh, logout) are never
 * intercepted: a 401 there is a failed login or an expired session token,
 * not a reason to refresh again.
 */

const REFRESH_PATH = '/api/auth/refresh';
const AUTH_PATH_PREFIX = '/api/auth/';

/** Pages that probe /api/me themselves: never redirect to them from them. */
const AUTH_PAGE_PREFIXES = ['/spa/login', '/spa/register'];

let refreshInFlight: Promise<boolean> | null = null;
let redirectStarted = false;

/**
 * The login redirect, isolated so tests can observe it without touching
 * window.location (jsdom does not implement navigation).
 */
export const sessionRedirect = {
  toLogin(): void {
    window.location.assign('/spa/login');
  },
};

/** Test hook: resets the single-flight and the redirect latch. */
export function _resetSessionStateForTests(): void {
  refreshInFlight = null;
  redirectStarted = false;
}

function isProtected(url: string): boolean {
  let path: string;
  try {
    path = new URL(url, window.location.origin).pathname;
  } catch {
    // Unparseable URL: treat as protected (refresh, then it will fail).
    return true;
  }
  return !path.startsWith(AUTH_PATH_PREFIX);
}

function performRefresh(): Promise<boolean> {
  // Raw fetch: the browser sends the `rt` cookie (Path=/api/auth) and adds
  // the Origin header. The refresh call never goes through this module, so
  // it cannot trigger itself.
  return fetch(REFRESH_PATH, {
    method: 'POST',
    credentials: 'same-origin',
  })
    .then((res) => res.ok)
    .catch(() => false);
}

function refreshOnce(): Promise<boolean> {
  refreshInFlight ??= performRefresh().finally(() => {
    refreshInFlight = null;
  });
  return refreshInFlight;
}

function redirectToLogin(): void {
  if (redirectStarted) {
    return;
  }
  redirectStarted = true;
  // Already on the login/register page (they probe /api/me too): showing
  // the form IS the answer. Redirecting would reload the same page forever.
  const path = window.location.pathname;
  if (AUTH_PAGE_PREFIXES.some((prefix) => path.startsWith(prefix))) {
    return;
  }
  sessionRedirect.toLogin();
}

/**
 * The fetch implementation behind the API client.
 *
 * On a 401 from a protected route: one shared refresh, then exactly one
 * retry of the original request. On refresh failure: one redirect to
 * /spa/login and the original 401 is returned to the caller.
 */
export async function sessionFetch(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<Response> {
  const request =
    input instanceof Request ? input : new Request(input.toString(), init);
  // Clone BEFORE the first send: a request with a body can be disturbed
  // once it is sent, and the retry needs the body intact.
  const retryCopy = request.clone();

  let response = await fetch(request, init);

  if (response.status === 401 && isProtected(request.url)) {
    const refreshed = await refreshOnce();
    if (refreshed) {
      // Exactly one retry; whatever it returns is final.
      response = await fetch(retryCopy);
    } else {
      redirectToLogin();
      // Keep the 401: the caller sees the failure, the redirect takes over.
    }
  }

  return response;
}
