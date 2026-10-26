import { expect, test } from "@playwright/test";

/**
 * Smoke coverage for the M00 skeleton: the app renders, and it reports the
 * environment it is actually pointed at.
 *
 * These assertions hold whether or not the API is reachable. That is
 * deliberate — the panel's job is to tell the truth about the environment, so
 * both outcomes are valid results and neither should fail the smoke test. What
 * would be a real failure is the page not rendering, or rendering neither a
 * status nor an error.
 */
/**
 * Next.js renders its own `<div role="alert" id="__next-route-announcer__">` as a
 * sibling of `<main>`, so a bare `getByRole("alert")` matches two elements and
 * every call on it fails Playwright's strict mode. Scoping to `main` keeps the
 * locator pointed at the status panel's own alert.
 *
 * This surfaced only once the specs could actually execute (M01-11): until the
 * Chromium build was installed they failed at `browserType.launch`, so the
 * ambiguity was never reached. The assertions below are unchanged.
 */
const statusAlert = (page: import("@playwright/test").Page) =>
  page.getByRole("main").getByRole("alert");

test.describe("system status page", () => {
  test("renders the application shell", async ({ page }) => {
    await page.goto("/");

    await expect(page.getByRole("heading", { name: "AI Design Studio", level: 1 })).toBeVisible();
    await expect(page.getByText("Milestone M00")).toBeVisible();
  });

  test("reports either a health result or a clear connection error", async ({ page }) => {
    await page.goto("/");

    const apiSection = page.getByRole("region", { name: "API" });
    const alert = statusAlert(page);

    // Exactly one of the two outcomes must appear once the boundary resolves.
    await expect(apiSection.or(alert).first()).toBeVisible({ timeout: 30_000 });

    if (await alert.isVisible()) {
      await expect(alert).toContainText(/Could not reach the API|API/i);
    } else {
      await expect(apiSection).toContainText(/Healthy|Degraded|Unhealthy/);
    }
  });

  test("lists provider capabilities when the API is reachable", async ({ page }) => {
    await page.goto("/");

    const alert = statusAlert(page);

    if (await alert.isVisible().catch(() => false)) {
      test.skip(true, "API not reachable; provider listing cannot be asserted.");
    }

    const providers = page.getByRole("region", { name: "AI providers" });
    await expect(providers).toBeVisible();
    await expect(providers).toContainText("google-stitch");

    // The UI must present capabilities as discovered data, never assume them.
    await expect(providers).toContainText("Capabilities:");
  });

  test("never renders a credential value", async ({ page }) => {
    await page.goto("/");

    const body = await page.locator("body").innerText();

    // Credential names are expected; values must never reach the browser.
    expect(body).not.toMatch(/AIza[0-9A-Za-z_-]{10,}/);
    expect(body).not.toMatch(/Password\s*=/i);
    expect(body).not.toContain("aids_dev_only");
  });
});
