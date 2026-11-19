import type { HealthReport, ProviderReadiness, ProviderSummary } from "@aids/contracts";

/**
 * Base URL of the API, as the browser sees it.
 *
 * **Empty by default, and that is the point.** Requests go to this origin and
 * `next.config.ts` proxies `/api/*` to the real API server-side, so the session
 * cookie is same-origin. Pointing the browser at the API's own origin is what
 * broke the documented browser path twice: `SameSite` is compared *schemefully*,
 * so an HTTP page and an HTTPS API are different sites and a `SameSite=Strict`
 * cookie is never sent (M01-W2-006, M01-W2-RR-003).
 *
 * Override only to talk to a genuinely remote API, which then needs its own CORS
 * allow-list and `credentials` handling — see ADR-0008.
 */
export const API_BASE_URL = (process.env.NEXT_PUBLIC_API_BASE_URL ?? "").replace(/\/+$/, "");

/**
 * Where **server-side** rendering reaches the API.
 *
 * A server component cannot fetch a relative URL — there is no document origin
 * to resolve it against — so the browser's proxied path and the server's path
 * cannot be the same string. The server talks to the API directly over loopback;
 * only the browser goes through the BFF proxy, which is the hop that needs to be
 * same-origin for the session cookie.
 *
 * Mixing these up is what made the status page report "no provider adapters"
 * instead of "sign in": the server-side fetch failed outright rather than
 * receiving the 401 it was supposed to interpret.
 */
const SERVER_API_ORIGIN = (process.env.API_ORIGIN ?? "http://localhost:5080").replace(/\/+$/, "");

/** True while rendering on the server, where there is no `window`. */
const isServer = () => typeof window === "undefined";

/** The base for this execution context: proxy path in the browser, origin on the server. */
const baseFor = (): string => (isServer() ? SERVER_API_ORIGIN : API_BASE_URL);

/**
 * Raised when the API responds but not successfully. Carries the status so
 * callers can distinguish "not configured yet" (404) from a real fault.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly path: string,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/**
 * Raised when the API could not be reached at all. Separate from ApiError
 * because during development "the API is not running" is the overwhelmingly
 * common case and deserves its own message rather than a generic failure.
 */
export class ApiUnreachableError extends Error {
  constructor(
    readonly path: string,
    cause: unknown,
  ) {
    super(
      `Could not reach the API at ${API_BASE_URL}${path}. Is it running? ` +
        `See README.md for local setup.`,
    );
    this.name = "ApiUnreachableError";
    this.cause = cause;
  }
}

interface RequestOptions {
  /** Forwarded to fetch's cache option. Defaults to no-store: these are diagnostics. */
  cache?: RequestCache;
  signal?: AbortSignal;

  /**
   * **Server-side only.** The incoming request's `Cookie` header, so a server
   * component calls the API as the visitor rather than anonymously.
   *
   * The browser's own requests need nothing here: they go through the
   * same-origin proxy and the session travels with them. A server render has no
   * cookie jar, so without this every server-side call was anonymous and the
   * status page told signed-in visitors to sign in. Ignored in the browser,
   * where fetch forbids setting the header anyway.
   *
   * It is only ever sent to `SERVER_API_ORIGIN`, which comes from server
   * configuration rather than from the request.
   */
  cookie?: string;
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  let response: Response;

  const headers: Record<string, string> = { Accept: "application/json" };
  const forwardsSession = isServer() && !!options.cookie;

  if (forwardsSession) {
    headers.Cookie = options.cookie!;
  }

  try {
    response = await fetch(`${baseFor()}${path}`, {
      headers,
      // A response fetched with a visitor's session belongs to that visitor;
      // letting the fetch cache keep it would serve it to the next one.
      cache: forwardsSession ? "no-store" : (options.cache ?? "no-store"),
      signal: options.signal,

      // Same-origin by default (the proxy in next.config.ts), so "same-origin"
      // is the accurate value. Kept explicit rather than relying on the default
      // because the whole class of bug here was the session cookie silently not
      // being attached.
      credentials: "same-origin",
    });
  } catch (cause) {
    throw new ApiUnreachableError(path, cause);
  }

  if (!response.ok) {
    // Health endpoints return a JSON body on failure too, so prefer it over the
    // bare status text when present.
    const detail = await response.text().catch(() => "");
    throw new ApiError(
      response.status,
      path,
      detail.trim() || `${response.status} ${response.statusText}`,
    );
  }

  return (await response.json()) as T;
}

/** Liveness: is the process up? Does not touch the database. */
export const getLiveness = (options?: RequestOptions) =>
  request<HealthReport>("/health/live", options);

/** Readiness: can the process serve traffic? Includes the database. */
export const getReadiness = (options?: RequestOptions) =>
  request<HealthReport>("/health/ready", options);

/** Registered provider adapters and the capabilities each declares. */
export const listProviders = (options?: RequestOptions) =>
  request<ProviderSummary[]>("/api/v1/providers/", options);

/**
 * Probes whether a provider is installed and authenticated. Never billable, so
 * it is safe to call on page load.
 */
export const getProviderReadiness = (providerId: string, options?: RequestOptions) =>
  request<ProviderReadiness>(
    `/api/v1/providers/${encodeURIComponent(providerId)}/readiness`,
    options,
  );
