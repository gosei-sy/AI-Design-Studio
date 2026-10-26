import { Suspense } from "react";

import { SystemStatusPanel, type SystemStatus } from "@/components/system-status";
import { getProviderReadiness, getReadiness, listProviders } from "@/lib/api";

/**
 * Collects status, degrading rather than throwing.
 *
 * A failure here is an expected state during setup (API not started, database
 * not migrated), so it is rendered as a message instead of a Next.js error page.
 */
async function loadStatus(): Promise<SystemStatus> {
  try {
    const [readiness, providers] = await Promise.all([getReadiness(), listProviders()]);

    const providerReadiness = await Promise.all(
      providers.map((p) => getProviderReadiness(p.providerId)),
    );

    return { readiness, providers, providerReadiness };
  } catch (error) {
    return { error: error instanceof Error ? error.message : String(error) };
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
