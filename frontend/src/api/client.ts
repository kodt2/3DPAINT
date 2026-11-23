import createClient from 'openapi-fetch';
import type { paths } from './schema';

/**
 * Typed API client generated from the monolith OpenAPI document
 * (regenerate with `npm run gen:api`). The client talks to the same origin:
 * in production the Gateway routes /api/** to the monolith, in dev the Vite
 * server proxies /api/** to the Gateway — no CORS, cookies work as-is.
 */
// Same origin: the Gateway (or the Vite dev proxy) serves /api/** here, so
// cookies flow without CORS. openapi-fetch resolves paths with `new URL()`,
// which requires an absolute base — hence the origin, not '/'.
export const api = createClient<paths>({ baseUrl: window.location.origin });
