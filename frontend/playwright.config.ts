import { defineConfig, devices } from "@playwright/test";

const PORT = Number(process.env.E2E_PORT ?? 3100);
const BASE_URL = process.env.E2E_BASE_URL ?? `https://localhost:${PORT}`;
// Loopback HTTP, matching next.config.ts and the CI job. TLS terminates at the
// frontend; the API trusts X-Forwarded-Proto from loopback. This defaulted to
// https://localhost:7080 while next.config.ts and CI used http://localhost:5080,
// so the CI e2e job would have failed on a mismatch nobody had run (M01-W2-R3).
const API_ORIGIN = process.env.API_ORIGIN ?? "http://localhost:5080";

/**
 * End-to-end configuration.
 *
 * These tests need both services running: the ASP.NET Core API (default
 * http://localhost:5080) and this Next.js app. Playwright starts the frontend
 * itself via `webServer`, but deliberately does not start the API — a test run
 * that silently spun up its own backend would hide the fact that the two are
 * separately deployed processes.
 *
 * **HTTPS, and the frontend proxies the API.** The session cookie is `Secure`
 * and `SameSite=Strict`, and SameSite is compared schemefully — so an HTTP page
 * cannot hold a session issued by an HTTPS API, and a cross-origin page cannot
 * send a Strict cookie at all. The browser therefore talks only to this origin,
 * which proxies `/api/*` (see next.config.ts). `ignoreHTTPSErrors` is set
 * because both local certificates are self-signed; that is a certificate-trust
 * concession, not a cookie-policy one (M01-W2-RR-003).
 *
 * Run `dotnet run --project backend/src/AIDesignStudio.Api` first, then
 * `npm run test:e2e --workspace frontend`.
 */
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? "github" : "list",

  use: {
    baseURL: BASE_URL,
    trace: "on-first-retry",
    ignoreHTTPSErrors: true,
  },

  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],

  webServer: {
    // `next dev --experimental-https` rather than `next start`: the session
    // cookie is Secure, so the page must be served over HTTPS, and Next has no
    // built-in TLS for `start`. In production TLS terminates at a reverse proxy
    // in front of both services — the same shape this reproduces locally.
    command: `npm run dev:https -- --port ${PORT}`,
    url: BASE_URL,
    ignoreHTTPSErrors: true,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
    env: {
      // Empty: the browser calls this origin and the proxy forwards. Setting an
      // absolute API URL here is what made the session unusable before.
      NEXT_PUBLIC_API_BASE_URL: "",
      API_ORIGIN,
      // Both local certificates are self-signed, so the server-side proxy must
      // accept them. Development only — never set in a deployed environment.
      NODE_TLS_REJECT_UNAUTHORIZED: "0",
    },
  },
});
