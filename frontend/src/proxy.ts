import { type NextRequest, NextResponse } from "next/server";

/**
 * Tells the API what scheme the browser actually used.
 *
 * `next.config.ts` proxies `/api/*` and `/health*` to the API over loopback
 * HTTP. The API's cookies are `Secure`, so it must know the original request was
 * HTTPS or it refuses to emit them — which is exactly the 500 the reviewers
 * reproduced (`AntiforgeryOptions.Cookie.SecurePolicy = Always, but the current
 * request is not an SSL request`).
 *
 * Next's rewrite proxy does **not** forward `X-Forwarded-Proto` on its own.
 * Measured: the same request sent directly to the API with the header returns
 * 200, and through the rewrite without it returns 500. Middleware is the
 * supported place to add a request header that reaches the proxied destination.
 *
 * This is the standard reverse-proxy contract, and the API only trusts it from
 * loopback — so a remote client cannot forge it (M01-W2-RR-003).
 *
 * Named `proxy.ts`, not `middleware.ts`: Next 16 deprecates the latter and warns
 * on every start (M01-W2-R3).
 */
export function proxy(request: NextRequest) {
  const headers = new Headers(request.headers);

  headers.set("x-forwarded-proto", request.nextUrl.protocol.replace(":", ""));
  headers.set("x-forwarded-host", request.nextUrl.host);

  return NextResponse.next({ request: { headers } });
}

export const config = {
  // Only the proxied paths. Page routes do not need it and matching them would
  // put this in front of every prerendered asset.
  matcher: ["/api/:path*", "/health", "/health/:path*"],
};
