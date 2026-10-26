import { afterEach, describe, expect, it, vi } from "vitest";

import {
  API_BASE_URL,
  ApiError,
  ApiUnreachableError,
  getProviderReadiness,
  getReadiness,
  listProviders,
} from "./api";

const mockFetch = (impl: typeof fetch) => {
  vi.stubGlobal("fetch", vi.fn(impl));
  return fetch as unknown as ReturnType<typeof vi.fn>;
};

const jsonResponse = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });

afterEach(() => vi.unstubAllGlobals());

describe("api client", () => {
  it("requests the readiness endpoint and returns the parsed report", async () => {
    const fetchMock = mockFetch(async () =>
      jsonResponse({
        status: "Healthy",
        totalDurationMs: 4.2,
        checks: [{ name: "database", status: "Healthy", durationMs: 4 }],
      }),
    );

    const report = await getReadiness();

    expect(report.status).toBe("Healthy");
    expect(report.checks[0]?.name).toBe("database");
    expect(fetchMock).toHaveBeenCalledWith(
      `${API_BASE_URL}/health/ready`,
      expect.objectContaining({ cache: "no-store" }),
    );
  });

  it("lists providers", async () => {
    mockFetch(async () =>
      jsonResponse([
        { providerId: "google-stitch", capabilities: ["IScreenGenerationCapability"] },
      ]),
    );

    const providers = await listProviders();

    expect(providers).toHaveLength(1);
    expect(providers[0]?.providerId).toBe("google-stitch");
  });

  /**
   * Readiness reports credential *names* only. If a value ever appeared here it
   * would end up in the browser, so the shape is asserted explicitly.
   */
  it("surfaces missing credential names without values", async () => {
    mockFetch(async () =>
      jsonResponse({
        providerId: "google-stitch",
        isInstalled: true,
        isAuthenticated: false,
        isUsable: false,
        detail: "Not signed in and no canvas credential.",
        missingCredentialNames: ["google-oauth", "STITCH_API_KEY"],
      }),
    );

    const readiness = await getProviderReadiness("google-stitch");

    expect(readiness.isUsable).toBe(false);
    expect(readiness.missingCredentialNames).toEqual(["google-oauth", "STITCH_API_KEY"]);
  });

  it("url-encodes the provider id", async () => {
    const fetchMock = mockFetch(async () => jsonResponse({}));

    await getProviderReadiness("weird/id");

    expect(fetchMock).toHaveBeenCalledWith(
      `${API_BASE_URL}/api/v1/providers/weird%2Fid/readiness`,
      expect.anything(),
    );
  });

  it("raises ApiError with the status for a non-ok response", async () => {
    mockFetch(async () => jsonResponse({ error: "No provider 'figma' is registered." }, 404));

    const error = await getProviderReadiness("figma").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(404);
    expect((error as ApiError).message).toContain("figma");
  });

  it("falls back to status text when the error body is empty", async () => {
    mockFetch(async () => new Response("", { status: 503, statusText: "Service Unavailable" }));

    const error = (await getReadiness().catch((e: unknown) => e)) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.message).toContain("503");
  });

  /**
   * The most common local failure: the API is not running. It must produce an
   * actionable message, not an opaque TypeError from fetch.
   */
  it("raises ApiUnreachableError when the API cannot be reached", async () => {
    mockFetch(async () => {
      throw new TypeError("fetch failed");
    });

    const error = (await listProviders().catch((e: unknown) => e)) as ApiUnreachableError;

    expect(error).toBeInstanceOf(ApiUnreachableError);
    expect(error.message).toContain("Is it running?");
    expect(error.cause).toBeInstanceOf(TypeError);
  });

  it("normalises a base url with a trailing slash", () => {
    expect(API_BASE_URL.endsWith("/")).toBe(false);
  });
});
