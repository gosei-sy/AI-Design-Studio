import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { SystemStatusPanel } from "./system-status";

describe("SystemStatusPanel", () => {
  it("renders the overall status and each individual check", () => {
    render(
      <SystemStatusPanel
        readiness={{
          status: "Healthy",
          totalDurationMs: 3,
          checks: [
            { name: "database", status: "Healthy", durationMs: 3 },
            { name: "blob-storage", status: "Degraded", durationMs: 1 },
          ],
        }}
        providers={[]}
      />,
    );

    // Once for the overall status, once per healthy check.
    expect(screen.getAllByText("Healthy")).toHaveLength(2);
    expect(screen.getByText("database")).toBeInTheDocument();
    expect(screen.getByText("blob-storage")).toBeInTheDocument();
    expect(screen.getByText("Degraded")).toBeInTheDocument();
  });

  /**
   * The state a developer hits most often during first-time setup. It must be a
   * readable message, not a crash.
   */
  it("renders an alert instead of failing when the API is unreachable", () => {
    render(<SystemStatusPanel error="Could not reach the API at http://localhost:5080." />);

    expect(screen.getByRole("alert")).toHaveTextContent("Could not reach the API");
  });

  it("lists a provider's declared capabilities", () => {
    render(
      <SystemStatusPanel
        providers={[
          {
            providerId: "google-stitch",
            capabilities: ["IScreenGenerationCapability", "IScreenImportCapability"],
          },
        ]}
      />,
    );

    expect(screen.getByText("google-stitch")).toBeInTheDocument();
    expect(
      screen.getByText(/IScreenGenerationCapability, IScreenImportCapability/),
    ).toBeInTheDocument();
  });

  it("names missing credentials so setup is actionable", () => {
    render(
      <SystemStatusPanel
        providers={[{ providerId: "google-stitch", capabilities: [] }]}
        providerReadiness={[
          {
            providerId: "google-stitch",
            isInstalled: true,
            isAuthenticated: false,
            isUsable: false,
            detail: "Signed in, but no canvas credential.",
            missingCredentialNames: ["STITCH_API_KEY"],
          },
        ]}
      />,
    );

    expect(screen.getByText("Signed in, but no canvas credential.")).toBeInTheDocument();
    expect(screen.getByText("STITCH_API_KEY")).toBeInTheDocument();
  });

  it("states plainly when a provider declares no capabilities", () => {
    render(<SystemStatusPanel providers={[{ providerId: "google-stitch", capabilities: [] }]} />);

    expect(screen.getByText("none declared")).toBeInTheDocument();
  });

  it("reports when no adapters are registered at all", () => {
    render(<SystemStatusPanel providers={[]} />);

    expect(screen.getByText("No provider adapters reported.")).toBeInTheDocument();
  });
});
