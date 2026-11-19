import { expect, test } from "@playwright/test";

/**
 * The browser authentication flow, in a real browser.
 *
 * This spec exists because two rounds of review found the auth API working in
 * `TestServer` and failing for an actual browser following the project's setup
 * instructions. In-process tests cannot catch that class of bug: they do not
 * enforce `Secure`, they do not apply schemeful `SameSite`, and they do not have
 * a cookie jar that refuses to store things.
 *
 * Everything below goes through this origin only. `next.config.ts` proxies
 * `/api/*` to the API server-side, so the session is same-origin — which is what
 * makes a `SameSite=Strict` cookie usable at all (M01-W2-RR-003).
 */
test.describe("browser authentication flow", () => {
  const password = "Correct-Horse-9-Battery!";

  const uniqueEmail = () => `e2e-${Date.now()}-${Math.floor(Math.random() * 1e6)}@example.test`;

  /** Fetches an antiforgery token the way the studio will. */
  async function antiforgeryToken(request: import("@playwright/test").APIRequestContext) {
    const response = await request.get("/api/v1/auth/antiforgery-token");
    expect(response.status()).toBe(200);
    return (await response.json()).token as string;
  }

  test("issues a token, registers, signs in, uses the session, then signs out", async ({
    page,
  }) => {
    const email = uniqueEmail();

    // Load the app first so the context has this origin, then drive the API
    // through the page's own request context — i.e. with the browser's cookie
    // jar and origin, not a bare HTTP client.
    await page.goto("/");

    const api = page.request;

    const registerToken = await antiforgeryToken(api);
    const register = await api.post("/api/v1/auth/register", {
      headers: { "X-CSRF-TOKEN": registerToken },
      data: { email, password },
    });
    expect(register.status()).toBe(202);

    const loginToken = await antiforgeryToken(api);
    const login = await api.post("/api/v1/auth/login", {
      headers: { "X-CSRF-TOKEN": loginToken },
      data: { email, password },
    });
    expect(login.status()).toBe(200);
    expect((await login.json()).authenticated).toBe(true);

    // The session cookie must actually be in the browser's jar. This is the
    // assertion that failed before: the cookie was issued and the browser
    // declined to keep it.
    const cookies = await page.context().cookies();
    const session = cookies.find((c) => c.name === "__Host-aids.session");

    expect(session, "the browser must have stored the session cookie").toBeTruthy();
    expect(session!.httpOnly).toBe(true);
    expect(session!.secure).toBe(true);
    expect(session!.sameSite).toBe("Strict");

    // And the session must work for an authenticated call.
    const me = await api.get("/api/v1/auth/me");
    expect(me.status()).toBe(200);
    expect((await me.json()).email).toBe(email);

    const providers = await api.get("/api/v1/providers");
    expect(providers.status()).toBe(200);

    const logoutToken = await antiforgeryToken(api);
    const logout = await api.post("/api/v1/auth/logout", {
      headers: { "X-CSRF-TOKEN": logoutToken },
    });
    expect(logout.status()).toBe(204);

    expect((await api.get("/api/v1/auth/me")).status()).toBe(401);
  });

  test("rejects a state-changing request with no antiforgery token", async ({ page }) => {
    await page.goto("/");

    const response = await page.request.post("/api/v1/auth/register", {
      data: { email: uniqueEmail(), password },
    });

    expect(response.status()).toBe(400);
    expect((await response.json()).code).toBe("auth.antiforgery_failed");
  });

  test("protects provider status from an anonymous browser", async ({ page }) => {
    await page.goto("/");

    expect((await page.request.get("/api/v1/providers")).status()).toBe(401);
  });
});
