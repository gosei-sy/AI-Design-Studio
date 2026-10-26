/**
 * Shared contract types.
 *
 * These mirror the JSON Schema files in `../schemas`, which are the normative
 * definition. The API validates payloads against those schemas; these types
 * exist so the studio UI gets compile-time checking against the same shape.
 *
 * **What the alignment covers.** `schema-alignment.test.ts` and
 * `schema-coverage.test.ts` hold these types and the schemas to the same
 * *structure*: which properties exist, which are required, what shape their
 * values have, and what an `additionalProperties` map admits. A schema change not
 * reflected here fails the build rather than drifting silently.
 *
 * **What it does not cover.** Lexical constraints — `pattern`, `minLength`,
 * `maxLength`, `uniqueItems`, numeric bounds — are not expressible in TypeScript
 * and are not enforced here. `HexColor` is `string`, so `"red"` compiles and the
 * schema rejects it; the same holds for CSS lengths and font-family bounds. These
 * are runtime-schema constraints, and the schema is the only authority for them.
 *
 * So: structurally aligned and tested, not semantically equivalent. Generating
 * these types from the schemas would narrow the gap and is deliberately out of
 * scope for M00 — nothing here is generated.
 */

export const DESIGN_CONTRACT_SCHEMA_VERSION = 1 as const;
export const DESIGN_SYSTEM_SCHEMA_VERSION = 1 as const;

export type Platform = "web" | "mobile" | "tablet";
export type TargetDevice = "mobile" | "desktop" | "tablet" | "agnostic";
export type ScreenState = "default" | "empty" | "loading" | "error" | "success" | "offline";
export type ColorMode = "light" | "dark";
export type ConformanceTarget = "WCAG21A" | "WCAG21AA" | "WCAG22AA";
export type Confidence = "low" | "medium" | "high";

/**
 * A lowercase slug or route-like path, stable across versions. Stability is what
 * lets a redesign match proposed screens to the existing application.
 */
export type StableKey = string;

/**
 * A CSS hex colour: `#rgb`, `#rrggbb` or `#rrggbbaa`.
 *
 * An alias for `string`, and therefore **not enforced by the compiler**:
 * `"red"` and `""` satisfy this type and are rejected by the schema's `pattern`.
 * The name documents the intended constraint and keeps `unknown` out of the
 * colour map. The schema is the only authority on the format.
 */
export type HexColor = string;

export interface ScreenObligation {
  key: StableKey;
  title: string;
  /** What a user accomplishes here. Review judges generated output against this. */
  purpose: string;
  requiredElements?: string[];
  states?: ScreenState[];
  devices?: TargetDevice[];
}

export interface FlowStep {
  screenKey: StableKey;
  note?: string;
}

export interface Flow {
  key: StableKey;
  name: string;
  steps: FlowStep[];
}

/**
 * An unvalidated product assumption. Recorded explicitly so it can be
 * challenged, rather than being absorbed into the requirements as if it were
 * a finding.
 */
export interface ProductAssumption {
  statement: string;
  confidence?: Confidence;
  validationPlan?: string;
}

export interface DesignContract {
  schemaVersion: typeof DESIGN_CONTRACT_SCHEMA_VERSION;
  product: {
    name: string;
    summary?: string;
    platforms: Platform[];
    primaryAudience?: string;
  };
  informationArchitecture: { flows: Flow[] };
  screens: ScreenObligation[];
  accessibility?: { conformanceTarget?: ConformanceTarget; notes?: string };
  contentRules?: { tone?: string; readingLevel?: string; prohibited?: string[] };
  assumptions?: ProductAssumption[];
}

export interface TypeStyle {
  fontFamily?: string;
  fontSize?: string;
  fontWeight?: string;
  lineHeight?: string;
  letterSpacing?: string;
}

export interface DesignSystem {
  schemaVersion: typeof DESIGN_SYSTEM_SCHEMA_VERSION;
  name: string;
  typography: {
    bodyFont: string;
    headlineFont: string;
    labelFont?: string;
    scale?: Record<string, TypeStyle>;
  };
  /**
   * Semantic colour roles and the colour modes the design system defines.
   *
   * Split into two properties so the *structure* lines up. Every value under
   * `roles` is a colour, so the index signature is homogeneous — `HexColor`
   * rather than `HexColor | ColorMode[]`. While `modes` lived among the roles the
   * signature had to admit arrays too, and `{ primary: "#fff", danger: ["light"] }`
   * type-checked while failing validation. That is the structural half of
   * M00-R3-002, and the split fixes it: `danger: ["light"]` is now a type error.
   *
   * It does **not** make the type exact. `HexColor` is `string`, so
   * `{ primary: "red" }` type-checks and the schema rejects it. Hex format is a
   * lexical constraint, enforced at runtime by the schema only — see `HexColor`.
   */
  color: {
    /** Only primary/secondary/tertiary/neutral project onto a provider theme slot. */
    roles: {
      primary: HexColor;
      secondary?: HexColor;
      tertiary?: HexColor;
      neutral?: HexColor;
    } & Record<string, HexColor | undefined>;

    /** Stitch carries one mode, so a second needs its own provider design system. */
    modes?: ColorMode[];
  };
  shape: { radiusBase: string };
  spacing?: Record<string, string>;
  /** Sections below have no provider-theme equivalent and are studio-enforced only. */
  elevation?: Record<string, string>;
  motion?: Record<string, string>;
  breakpoints?: Record<string, string>;
  borders?: Record<string, string>;
  components?: Record<string, { variants?: string[]; rules?: string[] }>;
}

// ---------------------------------------------------------------------------
// Provider capability model (mirrors the backend's Application layer)
// ---------------------------------------------------------------------------

/**
 * Capability names the API reports per provider. The UI must gate on these
 * rather than assuming a provider can do something: a capability absent from
 * this list is one the adapter does not implement.
 */
export type ProviderCapabilityName =
  | "IScreenGenerationCapability"
  | "IScreenVariantCapability"
  | "IScreenImportCapability"
  | "IScreenRetrievalCapability"
  | "IDesignSystemProjectionCapability"
  | "IRouteCaptureCapability";

export interface ProviderSummary {
  providerId: string;
  capabilities: ProviderCapabilityName[];
}

export interface ProviderReadiness {
  providerId: string;
  isInstalled: boolean;
  isAuthenticated: boolean;
  isUsable: boolean;
  detail: string;
  /** Credential *names* only, e.g. "STITCH_API_KEY". Never values. */
  missingCredentialNames: string[];
}

/**
 * One token or rule the provider could not represent. A non-empty warning list
 * is the normal case, not an error: the design system is intentionally richer
 * than any provider's theme model.
 */
export interface ProjectionWarning {
  tokenPath: string;
  requestedValue: string;
  appliedValue: string | null;
  reason: string;
}

export interface HealthReport {
  status: "Healthy" | "Degraded" | "Unhealthy";
  totalDurationMs: number;
  checks: Array<{
    name: string;
    status: string;
    durationMs: number;
    description?: string | null;
  }>;
}
