import { defineConfig, devices } from "@playwright/test";

const PORT = Number(process.env.E2E_PORT ?? 3100);
const BASE_URL = process.env.E2E_BASE_URL ?? `http://localhost:${PORT}`;

/**
 * End-to-end configuration.
 *
 * These tests need both services running: the ASP.NET Core API (default
 * http://localhost:5080) and this Next.js app. Playwright starts the frontend
 * itself via `webServer`, but deliberately does not start the API — a test run
 * that silently spun up its own backend would hide the fact that the two are
 * separately deployed processes.
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
  },

  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],

  webServer: {
    command: `npm run start -- --port ${PORT}`,
    url: BASE_URL,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    env: {
      NEXT_PUBLIC_API_BASE_URL: process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5080",
    },
  },
});
