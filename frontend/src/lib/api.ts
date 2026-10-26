import type { HealthReport, ProviderReadiness, ProviderSummary } from "@aids/contracts";

/**
 * Base URL of the ASP.NET Core API.
 *
 * Read from the environment rather than hardcoded so the same build runs
 * against local, preview and production APIs. NEXT_PUBLIC_ is required because
 * client components read it.
 */
export const API_BASE_URL = (
  process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5080"
).replace(/\/+$/, "");

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
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  let response: Response;

  try {
    response = await fetch(`${API_BASE_URL}${path}`, {
      headers: { Accept: "application/json" },
      cache: options.cache ?? "no-store",
      signal: options.signal,
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
