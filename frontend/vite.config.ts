/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// base:
//  - production build (nginx in Docker): "/", the Gateway serves the SPA under
//    /spa/** and the hashed assets under /assets/** (ADR 0011);
//  - dev server: "/spa/" so the page works both directly (http://host:5173/spa/)
//    and through the Gateway route /spa/** (prefix stripped).
const base = process.env.VITE_BASE ?? '/';

export default defineConfig({
  base,
  plugins: [react()],
  server: {
    host: true,
    port: 5173,
    strictPort: true,
    // In the compose `dev` profile the Vite dev server proxies /api to the
    // Gateway so the SPA and the API share the origin (no CORS).
    proxy: {
      '/api': {
        target: process.env.GATEWAY_URL ?? 'http://gateway:8080',
        changeOrigin: false,
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
});
