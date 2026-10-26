import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

export default defineConfig({
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
      "@aids/contracts": fileURLToPath(
        new URL("../packages/contracts/src/index.ts", import.meta.url),
      ),
    },
  },
  test: {
    // jsdom for component tests; the API client tests run fine under it too.
    environment: "jsdom",
    include: ["src/**/*.test.{ts,tsx}"],
    // Playwright owns end-to-end coverage; keep it out of the unit run.
    exclude: ["e2e/**"],
    globals: false,
    setupFiles: ["./vitest.setup.ts"],
  },
});
