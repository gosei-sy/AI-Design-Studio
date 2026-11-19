import type { HealthReport, ProviderReadiness, ProviderSummary } from "@aids/contracts";

/**
 * What the status panel needs to render. Every field is optional-by-failure:
 * the panel must render something useful when the API is down, because that is
 * the state a developer sees most often during setup.
 */
export interface SystemStatus {
  readiness?: HealthReport;
  providers?: ProviderSummary[];
  providerReadiness?: ProviderReadiness[];

  /**
   * The provider list needs a session (S-19, since M01-02) and this visitor has
   * none. A distinct state rather than an error, because "sign in to see this"
   * is a correct answer and calling it a failure would hide real outages in
   * noise (M01-W2-RR-004).
   */
  providersRequireAuth?: boolean;

  error?: string;
}

function StatusDot({ ok }: { ok: boolean }) {
  return (
    <span
      aria-hidden="true"
      className={`inline-block size-2 rounded-full ${ok ? "bg-emerald-500" : "bg-amber-500"}`}
    />
  );
}

/**
 * Read-only diagnostics panel.
 *
 * M00 deliberately ships no product UI. This panel exists because "is the API
 * reachable, is the database migrated, and which provider capabilities are
 * actually available here?" is the question every later milestone depends on,
 * and answering it from the browser is faster than reading logs.
 */
export function SystemStatusPanel({
  readiness,
  providers,
  providerReadiness,
  error,
  providersRequireAuth,
}: SystemStatus) {
  return (
    <div className="space-y-8">
      <section aria-labelledby="api-heading" className="space-y-3">
        <h2 id="api-heading" className="text-foreground text-sm font-medium">
          API
        </h2>

        {error ? (
          <p role="alert" className="text-sm text-amber-600 dark:text-amber-400">
            {error}
          </p>
        ) : readiness ? (
          <dl className="space-y-1 text-sm">
            <div className="flex items-center gap-2">
              <StatusDot ok={readiness.status === "Healthy"} />
              <dt className="sr-only">Overall status</dt>
              <dd className="font-mono">{readiness.status}</dd>
            </div>

            {readiness.checks.map((check) => (
              <div key={check.name} className="text-muted-foreground flex items-center gap-2 pl-4">
                <StatusDot ok={check.status === "Healthy"} />
                <dt className="font-mono">{check.name}</dt>
                <dd className="font-mono">{check.status}</dd>
              </div>
            ))}
          </dl>
        ) : null}
      </section>

      <section aria-labelledby="providers-heading" className="space-y-3">
        <h2 id="providers-heading" className="text-foreground text-sm font-medium">
          AI providers
        </h2>

        {providers?.length ? (
          <ul className="space-y-4 text-sm">
            {providers.map((provider) => {
              const status = providerReadiness?.find((r) => r.providerId === provider.providerId);

              return (
                <li key={provider.providerId} className="space-y-1">
                  <div className="flex items-center gap-2">
                    <StatusDot ok={status?.isUsable ?? false} />
                    <span className="font-mono">{provider.providerId}</span>
                  </div>

                  {status ? <p className="text-muted-foreground pl-4">{status.detail}</p> : null}

                  {status?.missingCredentialNames.length ? (
                    <p className="text-muted-foreground pl-4">
                      Missing:{" "}
                      <span className="font-mono">{status.missingCredentialNames.join(", ")}</span>
                    </p>
                  ) : null}

                  <p className="text-muted-foreground pl-4">
                    Capabilities:{" "}
                    <span className="font-mono">
                      {provider.capabilities.length
                        ? provider.capabilities.join(", ")
                        : "none declared"}
                    </span>
                  </p>
                </li>
              );
            })}
          </ul>
        ) : providersRequireAuth ? (
          <p role="status" className="text-muted-foreground text-sm">
            Sign in to view provider status.
          </p>
        ) : (
          <p className="text-muted-foreground text-sm">No provider adapters reported.</p>
        )}
      </section>
    </div>
  );
}
