import { Suspense } from "react";

import { SystemStatusPanel, type SystemStatus } from "@/components/system-status";
import { ApiError, getProviderReadiness, getReadiness, listProviders } from "@/lib/api";

/**
 * Collects status, degrading rather than throwing.
 *
 * A failure here is an expected state during setup (API not started, database
 * not migrated), so it is rendered as a message instead of a Next.js error page.
 */
async function loadStatus(): Promise<SystemStatus> {
  // Health is anonymous; the provider list is not (S-19, since M01-02). These
  // are fetched independently so one cannot mask the other.
  //
  // M01-W2-RR-004: they used to share a Promise.all, so once providers became
  // authenticated the 401 took the health result down with it and the page
  // rendered raw problem JSON. An unauthenticated visitor still deserves to see
  // whether the API and database are up.
  let readiness: SystemStatus["readiness"];
  let error: string | undefined;

  try {
    readiness = await getReadiness();
  } catch (cause) {
    error = cause instanceof Error ? cause.message : String(cause);
  }

  try {
    const providers = await listProviders();

    const providerReadiness = await Promise.all(
      providers.map((p) => getProviderReadiness(p.providerId)),
    );

    return { readiness, providers, providerReadiness, error };
  } catch (cause) {
    // A 401 is the expected state for an anonymous visitor, not a fault. It is
    // reported as "sign in to view" rather than as an error, because calling it
    // an error would be untrue and would hide a real outage behind noise.
    if (cause instanceof ApiError && cause.status === 401) {
      return { readiness, providersRequireAuth: true, error };
    }

    return {
      readiness,
      error: error ?? (cause instanceof Error ? cause.message : String(cause)),
    };
  }
}

/**
 * The request-time part of the page.
 *
 * Isolated into its own component so the surrounding shell can still be
 * prerendered: under Next's Cache Components model, uncached data access must
 * sit inside a Suspense boundary. Keeping the boundary tight also means a slow
 * or unreachable API degrades to a spinner rather than blocking the whole page.
 */
async function LiveSystemStatus() {
  const status = await loadStatus();
  return <SystemStatusPanel {...status} />;
}

function StatusSkeleton() {
  return (
    <p className="text-muted-foreground text-sm" role="status">
      Checking environment…
    </p>
  );
}

export default function Home() {
  return (
    <main className="mx-auto max-w-2xl px-6 py-16">
      <header className="mb-10 space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">AI Design Studio</h1>
        <p className="text-muted-foreground text-sm">
          Milestone M00 — foundation. This page reports environment status only; product workflows
          arrive in M01.
        </p>
      </header>

      <Suspense fallback={<StatusSkeleton />}>
        <LiveSystemStatus />
      </Suspense>
    </main>
  );
}
