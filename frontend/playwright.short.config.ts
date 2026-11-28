import base from './playwright.config';

/**
 * Short-token stand config (Makefile `e2e-short`): the stand runs Auth with
 * 5-second access tokens (AUTH_JWT_EXPIRY_SECONDS=5) and the monolith with a
 * zero clock skew (JWT_CLOCK_SKEW_SECONDS=0), so only the short-token spec
 * is meaningful against it. Everything else (base URL, browser, timeouts)
 * is inherited from the main config; the base's testIgnore (which excludes
 * this very spec) is overridden.
 */
export default {
  ...base,
  testIgnore: [],
  testMatch: ['**/short-token.spec.ts'],
};
