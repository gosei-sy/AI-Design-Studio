// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";

import { getProviderReadiness, listProviders } from "./api";

/**
 * The server-side half of the API client: what a server component sends when it
 * calls the API on the visitor's behalf.
 *
 * Runs in the `node` environment because that is the only one where `window` is
 * undefined, which is how the client decides it is rendering on the server. The
 * rest of the suite runs under jsdom and so only ever exercises the browser path
 * — which is how a server render that silently dropped the session went
 * unnoticed: the status page told a signed-in visitor to sign in.
 */

const mockFetch = () => {
  const fn = vi.fn(async () => new Response("[]", { status: 200 }));
  vi.stubGlobal("fetch", fn);
  return fn;
};

const sentHeaders = (fn: ReturnType<typeof mockFetch>) =>
  new Headers((fn.mock.calls[0] as unknown as [string, RequestInit])[1].headers);

const sentInit = (fn: ReturnType<typeof mockFetch>) =>
  (fn.mock.calls[0] as unknown as [string, RequestInit])[1];

afterEach(() => vi.unstubAllGlobals());

describe("api client on the server", () => {
  it("forwards the visitor's Cookie header so the API sees their session", async () => {
    const fetchMock = mockFetch();

    await listProviders({ cookie: "__Host-aids.session=abc; other=1" });

    expect(sentHeaders(fetchMock).get("cookie")).toBe("__Host-aids.session=abc; other=1");
  });

  it("calls the API origin directly, not the browser's proxy path", async () => {
    const fetchMock = mockFetch();

    await getProviderReadiness("google-stitch", { cookie: "__Host-aids.session=abc" });

    const url = (fetchMock.mock.calls[0] as unknown as [string])[0];
    expect(url).toMatch(/^https?:\/\/[^/]+\/api\/v1\/providers\/google-stitch\/readiness$/);
  });

  /**
   * A response fetched with someone's session is that person's. Letting the
   * fetch cache keep it would hand it to the next visitor.
   */
  it("never caches a request that carries a session", async () => {
    const fetchMock = mockFetch();

    await listProviders({ cookie: "__Host-aids.session=abc", cache: "force-cache" });

    expect(sentInit(fetchMock).cache).toBe("no-store");
  });

  it("sends no Cookie header when the visitor has none", async () => {
    const fetchMock = mockFetch();

    await listProviders();

    expect(sentHeaders(fetchMock).has("cookie")).toBe(false);
  });
});
