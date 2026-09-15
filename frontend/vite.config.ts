import react from '@vitejs/plugin-react'
// vitest/config re-exports Vite's defineConfig with the `test` key typed.
import { defineConfig } from 'vitest/config'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  // One .env at the repo root serves backend and frontend (PLAN.md, Configuration).
  // Vite only exposes keys prefixed VITE_, so Jwt__Secret and friends in the same
  // file never reach the bundle.
  envDir: '..',
  test: {
    // client.ts throws at import if VITE_API_URL is unset, and CI has no .env — so
    // the test run supplies a value itself rather than depending on the developer's
    // file. Nothing in the tests makes a request; the URL only has to exist.
    env: { VITE_API_URL: 'http://localhost:8080' },
  },
})
