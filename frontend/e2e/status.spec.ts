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
      // Health is anonymous, so a reachable API must produce a real verdict
      // here even though the provider section needs a session. Those two
      // requests are deliberately independent (M01-W2-RR-004).
      await expect(apiSection).toContainText(/Healthy|Degraded|Unhealthy/);
    }
  });

  /**
   * Provider status needs a session since M01-02 (S-19). An anonymous visitor
   * must see a deliberate "sign in" state — not raw 401 problem JSON, and not an
   * error that would mask a genuine outage.
   *
   * M01-W2-RR-004: this spec used to skip itself whenever an alert appeared,
   * which meant it stopped asserting anything the moment the endpoint became
   * protected.
   */
  test("asks an anonymous visitor to sign in before showing provider status", async ({ page }) => {
    await page.goto("/");

    const providers = page.getByRole("region", { name: "AI providers" });
    await expect(providers).toBeVisible();
    await expect(providers).toContainText(/Sign in to view provider status/i);

    // Whatever it shows, it must not be a raw problem document.
    await expect(providers).not.toContainText("application/problem");
    await expect(providers).not.toContainText('"status": 401');
  });

  /**
   * The other half of the test above. The page is rendered on the server, which
   * calls the API on the visitor's behalf — and until 2026-11-19 it did so
   * without their session, so a signed-in visitor was told to sign in. The
   * anonymous test could not notice: it passes whether or not the session is
   * forwarded.
   */
  test("shows provider status to a signed-in visitor", async ({ page }) => {
    await page.goto("/");

    const api = page.request;
    const token = async () =>
      (await (await api.get("/api/v1/auth/antiforgery-token")).json()).token as string;
    const email = `status-${Date.now()}-${Math.floor(Math.random() * 1e6)}@example.test`;
    const password = "Correct-Horse-9-Battery!";

    const register = await api.post("/api/v1/auth/register", {
      headers: { "X-CSRF-TOKEN": await token() },
      data: { email, password },
    });
    expect(register.status()).toBe(202);

    const login = await api.post("/api/v1/auth/login", {
      headers: { "X-CSRF-TOKEN": await token() },
      data: { email, password },
    });
    expect(login.status()).toBe(200);

    await page.reload();

    const providers = page.getByRole("region", { name: "AI providers" });
    await expect(providers).toContainText("google-stitch", { timeout: 30_000 });
    await expect(providers).not.toContainText(/Sign in to view provider status/i);
  });

  /**
   * `--font-sans` once resolved to itself (`var(--font-sans)`), which is invalid,
   * so every page silently fell back to the browser's default serif. Nothing
   * failed; it only looked wrong in a screenshot.
   */
  test("renders in the studio typeface, not the browser default", async ({ page }) => {
    await page.goto("/");

    const family = await page.evaluate(() => getComputedStyle(document.body).fontFamily);

    expect(family).toMatch(/Geist/);
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
