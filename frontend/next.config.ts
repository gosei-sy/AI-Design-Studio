import type { NextConfig } from "next";

/**
 * Where the ASP.NET Core API actually listens. Used only by the server-side
 * proxy below — never sent to the browser.
 */
const API_ORIGIN = process.env.API_ORIGIN ?? "http://localhost:5080";

const nextConfig: NextConfig = {
  cacheComponents: true,
  partialPrefetching: true,
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },

  /**
   * The BFF boundary (M01-W2-RR-003).
   *
   * The browser talks only to this origin; `/api/*` and `/health*` are proxied
   * server-side to the API. That makes the session genuinely **same-origin**,
   * which is what ADR-0008 claimed from the start and what the implementation
   * previously was not.
   *
   * Why it has to be same-origin rather than merely same-site: `SameSite`
   * comparison is *schemeful*, so an `http://localhost:3000` page and an
   * `https://localhost:7080` API are different sites and a `SameSite=Strict`
   * cookie is never sent. Reviewers reproduced exactly that in Chromium — a
   * request token was issued, the credentialed registration came back
   * `400 auth.antiforgery_failed`, and the cookie jar stayed empty. Proxying
   * removes the cross-origin hop instead of trying to configure around it, and
   * drops the need for CORS entirely.
   *
   * In production TLS terminates at a reverse proxy in front of both, which is
   * the same shape.
   */
  async rewrites() {
    return [
      { source: "/api/:path*", destination: `${API_ORIGIN}/api/:path*` },
      { source: "/health", destination: `${API_ORIGIN}/health` },
      { source: "/health/:path*", destination: `${API_ORIGIN}/health/:path*` },
    ];
  },
};

export default nextConfig;
