// Step 4.3a: dual-source OpenAPI generation for the SPA.
//
// The committed schema (src/api/schema.ts) is generated from TWO documents:
//   1. the monolith document (SWAGGER_URL, default
//      http://127.0.0.1:18097/swagger/v1/swagger.json);
//   2. the Auth document (AUTH_SWAGGER_URL, default
//      http://127.0.0.1:18096/swagger/v1/swagger.json).
// Both defaults are the dev-only loopback ports from infra/compose.override.yml
// (the Gateway has no /swagger route by design — 3d.2 removed the catch-all).
//
// Merge rules (which mirror what the Gateway actually serves):
//   - /api/auth/** and /api/me come from the Auth document (step 4.3b routes
//     them to the Auth service; the rest of /api/** stays on the monolith);
//   - all other paths come from the monolith document;
//   - components.schemas: the monolith's first, then the Auth's — the Auth
//     shape wins on a name collision (its only collision, AuthUserDto, is
//     referenced exclusively by the paths replaced above).
//
// Usage (from the frontend/ directory):
//   node scripts/gen-api.mjs           # (re)generate src/api/schema.ts
//   node scripts/gen-api.mjs --check   # generate to a temp file and fail
//                                      # (exit 1) if it differs from the
//                                      # committed src/api/schema.ts
//
// The main stack must be running (`make up` — it picks up the dev override
// with the two loopback ports) with ASPNETCORE_ENVIRONMENT=Development
// (the monolith only serves /swagger in Development).

import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const check = process.argv.includes('--check');
const monolithUrl = process.env.SWAGGER_URL ?? 'http://127.0.0.1:18097/swagger/v1/swagger.json';
const authUrl = process.env.AUTH_SWAGGER_URL ?? 'http://127.0.0.1:18096/swagger/v1/swagger.json';

async function fetchJson(url) {
  const res = await fetch(url);
  if (!res.ok) {
    throw new Error(
      `Cannot fetch the OpenAPI document from ${url} (HTTP ${res.status}). ` +
        'Is the stack up (`make up`) and ASPNETCORE_ENVIRONMENT=Development?',
    );
  }
  return res.json();
}

// Paths the Auth service serves from step 4.3a on: /api/auth/** and /api/me.
const fromAuth = (p) => p === '/api/me' || p.startsWith('/api/auth');

const [monolith, auth] = await Promise.all([fetchJson(monolithUrl), fetchJson(authUrl)]);

const merged = {
  ...monolith,
  info: {
    ...monolith.info,
    description:
      'Merged OpenAPI document (frontend/scripts/gen-api.mjs): /api/auth/** and /api/me from the Auth service, the rest from the monolith.',
  },
  paths: { ...monolith.paths },
  components: {
    ...monolith.components,
    schemas: {
      ...(monolith.components?.schemas ?? {}),
      ...(auth.components?.schemas ?? {}),
    },
  },
};

for (const [path, methods] of Object.entries(auth.paths)) {
  if (fromAuth(path)) {
    merged.paths[path] = methods;
  }
}

const outFile = check ? path.join(mkdtempSync(path.join(tmpdir(), 'gen-api-')), 'schema.ts') : 'src/api/schema.ts';
const jsonFile = check ? path.join(path.dirname(outFile), 'merged.json') : path.join(tmpdir(), `gen-api-merged-${Date.now()}.json`);
try {
  writeFileSync(jsonFile, JSON.stringify(merged, null, 2));

  const generated = spawnSync(
    'npx',
    ['--no-install', 'openapi-typescript', jsonFile, '-o', outFile],
    { stdio: 'inherit' },
  );
  if (generated.status !== 0) {
    process.exit(generated.status ?? 1);
  }

  if (check) {
    const expected = readFileSync('src/api/schema.ts');
    const actual = readFileSync(outFile);
    if (!expected.equals(actual)) {
      console.error(
        '\nERROR: the generated schema differs from the committed src/api/schema.ts.\n' +
          'Regenerate it with: npm run gen:api   (then review and commit the diff)',
      );
      process.exit(1);
    }
    console.log('gen:api:check — src/api/schema.ts is up to date.');
  }
} finally {
  if (check) {
    rmSync(path.dirname(outFile), { recursive: true, force: true });
  } else {
    rmSync(jsonFile, { force: true });
  }
}
