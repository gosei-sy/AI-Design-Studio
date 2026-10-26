import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import { describe, expect, it } from "vitest";

import type { DesignContract, DesignSystem } from "./index.js";

/**
 * Recursive, exhaustive coverage of both schemas.
 *
 * `schema-alignment.test.ts` pins a hand-written key list per object to the
 * TypeScript types. That is a strong check where it applies, but it applied only
 * to the objects someone remembered to list — M00-RR-002 found
 * `informationArchitecture`, `accessibility`, `contentRules`, `color`, `shape`
 * and the component definitions uncovered, and required/optional testing limited
 * to the two top levels.
 *
 * This file closes that by discovery rather than enumeration:
 *
 *   1. Walk every object node in each schema and record its path.
 *   2. Assert every discovered path is declared below. A new nested object fails
 *      here, so coverage cannot silently shrink.
 *   3. For every `required` field at every depth, omit it from a full fixture and
 *      assert the payload becomes invalid.
 *   4. For every optional field at every depth, omit it and assert it stays valid.
 *   5. For every `additionalProperties` contract, assert a conforming extra value
 *      is accepted and a non-conforming one is rejected.
 *
 * Steps 3–5 are generated from the schema, so they grow automatically.
 */

const load = (name: string) =>
  JSON.parse(readFileSync(fileURLToPath(new URL(`../schemas/${name}`, import.meta.url)), "utf8"));

const contractSchema = load("design-contract.schema.json");
const designSystemSchema = load("design-system.schema.json");

const ajv = new Ajv2020({ allErrors: true, strict: false });
const validateContract = ajv.compile(contractSchema);
const validateDesignSystem = ajv.compile(designSystemSchema);

// ---------------------------------------------------------------------------
// Schema walking
// ---------------------------------------------------------------------------

interface Node {
  type?: string | string[];
  properties?: Record<string, Node>;
  required?: string[];
  items?: Node;
  additionalProperties?: Node | boolean;
  $ref?: string;
  anyOf?: Node[];
  enum?: unknown[];
  const?: unknown;
  pattern?: string;
}

interface ObjectNode {
  /** Fixture path, e.g. `screens.0` or `typography.scale.*`. */
  path: string;
  node: Node;
}

const isObjectNode = (node: Node) =>
  node.properties !== undefined ||
  node.type === "object" ||
  typeof node.additionalProperties === "object";

/**
 * Enumerates every object node reachable from the root, keyed by the path a
 * caller would use to reach it in a payload. `0` steps into an array's first
 * element; `*` steps into an `additionalProperties` value.
 */
function walkObjects(root: Node, rootPath = ""): ObjectNode[] {
  const found: ObjectNode[] = [];

  const visit = (node: Node, path: string) => {
    if (!node || typeof node !== "object") return;

    // $ref targets are shared leaf definitions (stableKey, hexColor, cssLength);
    // they carry no properties of their own.
    if (node.$ref) return;

    if (isObjectNode(node)) {
      found.push({ path, node });

      for (const [key, child] of Object.entries(node.properties ?? {})) {
        visit(child, path ? `${path}.${key}` : key);
      }

      if (typeof node.additionalProperties === "object") {
        visit(node.additionalProperties, path ? `${path}.*` : "*");
      }
    }

    if (node.items) visit(node.items, path ? `${path}.0` : "0");
    for (const variant of node.anyOf ?? []) visit(variant, path);
  };

  visit(root, rootPath);
  return found;
}

const contractObjects = walkObjects(contractSchema as Node);
const designSystemObjects = walkObjects(designSystemSchema as Node);

// ---------------------------------------------------------------------------
// Declared coverage — every discovered object path must appear here
// ---------------------------------------------------------------------------

const DECLARED_CONTRACT_PATHS = [
  "",
  "product",
  "informationArchitecture",
  "informationArchitecture.flows.0",
  "informationArchitecture.flows.0.steps.0",
  "screens.0",
  "accessibility",
  "contentRules",
  "assumptions.0",
].sort();

const DECLARED_DESIGN_SYSTEM_PATHS = [
  "",
  "typography",
  "typography.scale",
  "typography.scale.*",
  "color",
  "color.roles",
  "shape",
  "spacing",
  "elevation",
  "motion",
  "breakpoints",
  "borders",
  "components",
  "components.*",
].sort();

describe("every object in the schema is covered", () => {
  it("design contract", () => {
    expect(contractObjects.map((o) => o.path).sort()).toEqual(DECLARED_CONTRACT_PATHS);
  });

  it("design system", () => {
    expect(designSystemObjects.map((o) => o.path).sort()).toEqual(DECLARED_DESIGN_SYSTEM_PATHS);
  });
});

// ---------------------------------------------------------------------------
// Full fixtures — every property at every depth populated
// ---------------------------------------------------------------------------

const fullContract: DesignContract = {
  schemaVersion: 1,
  product: {
    name: "Acme Storefront",
    summary: "Buy things.",
    platforms: ["web", "mobile", "tablet"],
    primaryAudience: "Returning customers",
  },
  informationArchitecture: {
    flows: [{ key: "checkout", name: "Checkout", steps: [{ screenKey: "cart", note: "Entry" }] }],
  },
  screens: [
    {
      key: "cart",
      title: "Cart",
      purpose: "Review items before paying.",
      requiredElements: ["line items", "order total"],
      states: ["default", "empty", "loading", "error", "success", "offline"],
      devices: ["mobile", "desktop", "tablet", "agnostic"],
    },
  ],
  accessibility: { conformanceTarget: "WCAG22AA", notes: "Keyboard-first." },
  contentRules: { tone: "Plain", readingLevel: "B2", prohibited: ["synergy"] },
  assumptions: [
    {
      statement: "Users arrive from email.",
      confidence: "low",
      validationPlan: "Check analytics.",
    },
  ],
};

const fullDesignSystem: DesignSystem = {
  schemaVersion: 1,
  name: "Acme",
  typography: {
    bodyFont: "Inter",
    headlineFont: "Manrope",
    labelFont: "Inter",
    scale: {
      body: {
        fontFamily: "Inter",
        fontSize: "16px",
        fontWeight: "400",
        lineHeight: "24px",
        letterSpacing: "0px",
      },
    },
  },
  color: {
    roles: {
      primary: "#3366ff",
      secondary: "#22c55e",
      tertiary: "#f59e0b",
      neutral: "#6b7280",
    },
    modes: ["light", "dark"],
  },
  shape: { radiusBase: "8px" },
  spacing: { md: "16px" },
  elevation: { card: "0 1px 2px rgba(0,0,0,.08)" },
  motion: { standard: "200ms ease-out" },
  breakpoints: { md: "768px" },
  borders: { width: "1px" },
  components: { button: { variants: ["primary", "ghost"], rules: ["44px target"] } },
};

// ---------------------------------------------------------------------------
// Fixture navigation
// ---------------------------------------------------------------------------

type Mutable = Record<string, unknown>;

const clone = <T>(value: T): T => structuredClone(value);

/**
 * Resolves a walker path against a fixture, returning the container holding the
 * leaf. `0` indexes an array; `*` takes the first key of a map.
 */
function resolve(fixture: unknown, path: string): Mutable | undefined {
  if (path === "") return fixture as Mutable;

  let current: unknown = fixture;

  for (const segment of path.split(".")) {
    if (current === undefined || current === null) return undefined;

    if (segment === "0") {
      current = (current as unknown[])[0];
    } else if (segment === "*") {
      const keys = Object.keys(current as Mutable);
      if (keys.length === 0) return undefined;
      current = (current as Mutable)[keys[0]!];
    } else {
      current = (current as Mutable)[segment];
    }
  }

  return current as Mutable | undefined;
}

/** Every (path, property) pair the schema marks required, at any depth. */
const requiredCases = (objects: ObjectNode[]) =>
  objects.flatMap(({ path, node }) =>
    (node.required ?? []).map((property) => ({ path, property })),
  );

/** Every (path, property) pair the schema leaves optional, at any depth. */
const optionalCases = (objects: ObjectNode[]) =>
  objects.flatMap(({ path, node }) =>
    Object.keys(node.properties ?? {})
      .filter((property) => !(node.required ?? []).includes(property))
      .map((property) => ({ path, property })),
  );

const label = ({ path, property }: { path: string; property: string }) =>
  `${path === "" ? "(root)" : path}.${property}`;

// ---------------------------------------------------------------------------
// Required and optional rules, at every depth
// ---------------------------------------------------------------------------

describe("design contract: required and optional rules at every depth", () => {
  it("the full fixture is valid, so no populated property is secretly rejected", () => {
    expect(validateContract(fullContract), JSON.stringify(validateContract.errors)).toBe(true);
  });

  it.each(requiredCases(contractObjects).map((c) => [label(c), c] as const))(
    "rejects a payload missing the required %s",
    (_name, { path, property }) => {
      const payload = clone(fullContract);
      const container = resolve(payload, path);
      expect(container, `fixture does not reach ${path}`).toBeDefined();
      delete container![property];

      expect(validateContract(payload)).toBe(false);
    },
  );

  it.each(optionalCases(contractObjects).map((c) => [label(c), c] as const))(
    "accepts a payload omitting the optional %s",
    (_name, { path, property }) => {
      const payload = clone(fullContract);
      const container = resolve(payload, path);
      expect(container, `fixture does not reach ${path}`).toBeDefined();
      delete container![property];

      expect(validateContract(payload), JSON.stringify(validateContract.errors)).toBe(true);
    },
  );
});

describe("design system: required and optional rules at every depth", () => {
  it("the full fixture is valid", () => {
    expect(
      validateDesignSystem(fullDesignSystem),
      JSON.stringify(validateDesignSystem.errors),
    ).toBe(true);
  });

  it.each(requiredCases(designSystemObjects).map((c) => [label(c), c] as const))(
    "rejects a payload missing the required %s",
    (_name, { path, property }) => {
      const payload = clone(fullDesignSystem);
      const container = resolve(payload, path);
      expect(container, `fixture does not reach ${path}`).toBeDefined();
      delete container![property];

      expect(validateDesignSystem(payload)).toBe(false);
    },
  );

  it.each(optionalCases(designSystemObjects).map((c) => [label(c), c] as const))(
    "accepts a payload omitting the optional %s",
    (_name, { path, property }) => {
      const payload = clone(fullDesignSystem);
      const container = resolve(payload, path);
      expect(container, `fixture does not reach ${path}`).toBeDefined();
      delete container![property];

      expect(validateDesignSystem(payload), JSON.stringify(validateDesignSystem.errors)).toBe(true);
    },
  );
});

// ---------------------------------------------------------------------------
// additionalProperties contracts — generated from schema discovery
//
// M00-R3-002 found these cases hand-enumerated while the file claimed they were
// generated. They are now derived from each discovered node's own schema, and a
// guard asserts that every discovered node is either covered by the generator or
// explicitly classified with a reason. Coverage can no longer drift from the
// claim, and a new map in either schema fails the guard rather than being
// silently untested.
// ---------------------------------------------------------------------------

/** A value the node's schema should accept, and one it should reject. */
interface Samples {
  good: unknown;
  bad: unknown;
  derivedFrom: string;
}

/** Nodes the generator cannot derive samples for, each with a stated reason. */
const UNSUPPORTED_ADDITIONAL_PROPERTIES: Record<string, string> = {
  // Intentionally empty. An entry here is a documented coverage hole, not a
  // default: the guard below prints it, so adding one is a visible decision.
};

const resolveRef = (schema: Node, ref: string): Node | undefined => {
  const name = ref.replace("#/$defs/", "");
  return (schema as { $defs?: Record<string, Node> }).$defs?.[name];
};

/**
 * Derives accept/reject samples from a node's own schema.
 *
 * Handles the shapes these schemas actually use: `$ref` to a shared leaf, a
 * pattern-constrained string, a plain string, and a closed object. Anything else
 * returns undefined, which the guard turns into a failure unless classified.
 */
function deriveSamples(root: Node, node: Node): Samples | undefined {
  if (node.$ref) {
    const target = resolveRef(root, node.$ref);
    if (!target) return undefined;

    const derived = deriveSamples(root, target);
    return derived ? { ...derived, derivedFrom: node.$ref } : undefined;
  }

  if (node.type === "string" && node.pattern) {
    // Build an accepted value per known pattern, and reject a string that
    // cannot match any of them.
    const good = node.pattern.includes("#") ? "#abcdef" : "16px";
    return { good, bad: "definitely-not-matching", derivedFrom: `string/${node.pattern}` };
  }

  if (node.type === "string") {
    // Unconstrained string: the only thing it must reject is a non-string.
    return { good: "a string", bad: 42, derivedFrom: "string" };
  }

  if (node.type === "object" && node.additionalProperties === false) {
    const known = Object.keys(node.properties ?? {});
    if (known.length === 0) return undefined;

    return {
      good: {},
      bad: { __notAKnownProperty: "x" },
      derivedFrom: `closed object (${known.join(", ")})`,
    };
  }

  return undefined;
}

/**
 * `walkObjects` records the *value* schema of a map under `<parent>.*`, but a
 * value that is not itself an object never gets a node — so maps of strings are
 * found by inspecting the parent instead.
 */
function stringValuedMaps(root: Node, objects: ObjectNode[]) {
  return objects
    .filter((o) => typeof o.node.additionalProperties === "object")
    .map((o) => ({
      path: `${o.path}.*`,
      parent: o.path,
      node: o.node.additionalProperties as Node,
    }))
    .filter((m) => !m.path.startsWith("."))
    .map((m) => ({ ...m, samples: deriveSamples(root, m.node) }));
}

const contractMapCases = stringValuedMaps(contractSchema as Node, contractObjects);
const designSystemMapCases = stringValuedMaps(designSystemSchema as Node, designSystemObjects);

describe("every additionalProperties contract is covered", () => {
  it("the generator derives samples for every discovered map, or it is classified", () => {
    const uncovered = [...contractMapCases, ...designSystemMapCases]
      .filter((m) => m.samples === undefined)
      .filter((m) => !(m.path in UNSUPPORTED_ADDITIONAL_PROPERTIES))
      .map((m) => m.path);

    expect(
      uncovered,
      "add a generator case, or classify the path in UNSUPPORTED_ADDITIONAL_PROPERTIES with a reason",
    ).toEqual([]);
  });

  it("at least one map was discovered in each schema, so the generator is not vacuous", () => {
    expect(contractMapCases.length + designSystemMapCases.length).toBeGreaterThan(0);
    expect(designSystemMapCases.length).toBeGreaterThan(0);
  });

  it("every classified exclusion states a reason", () => {
    for (const [path, reason] of Object.entries(UNSUPPORTED_ADDITIONAL_PROPERTIES)) {
      expect(reason.length, `${path} needs a reason`).toBeGreaterThan(10);
    }
  });
});

/** Sets a map entry at the parent path, creating nothing — the fixture is full. */
function withMapEntry<T>(fixture: T, parentPath: string, key: string, value: unknown): T {
  const copy = clone(fixture);
  const container = resolve(copy, parentPath);
  if (container === undefined) {
    throw new Error(`fixture does not reach ${parentPath}`);
  }

  container[key] = value;
  return copy;
}

const generatedCases = (
  cases: ReturnType<typeof stringValuedMaps>,
): Array<[string, string, Samples]> =>
  cases
    .filter((m) => m.samples !== undefined)
    .map((m) => [m.path, m.parent, m.samples!] as [string, string, Samples]);

describe("design system: generated additionalProperties cases", () => {
  const cases = generatedCases(designSystemMapCases);

  it.each(cases)("%s accepts a value derived from its schema", (_path, parent, samples) => {
    const payload = withMapEntry(fullDesignSystem, parent, "generatedKey", samples.good);

    expect(
      validateDesignSystem(payload),
      `${parent} rejected ${JSON.stringify(samples.good)} (from ${samples.derivedFrom}): ` +
        JSON.stringify(validateDesignSystem.errors),
    ).toBe(true);
  });

  it.each(cases)("%s rejects a value its schema forbids", (_path, parent, samples) => {
    const payload = withMapEntry(fullDesignSystem, parent, "generatedKey", samples.bad);

    expect(
      validateDesignSystem(payload),
      `${parent} accepted ${JSON.stringify(samples.bad)} (from ${samples.derivedFrom})`,
    ).toBe(false);
  });
});

describe("design contract: generated additionalProperties cases", () => {
  const cases = generatedCases(contractMapCases);

  it.each(cases.length > 0 ? cases : [["(none)", "", { good: 0, bad: 0, derivedFrom: "" }]])(
    "%s behaves as its schema specifies",
    (path, parent, samples) => {
      if (path === "(none)") {
        // The contract schema has no additionalProperties maps today. Asserted
        // rather than skipped, so adding one is noticed.
        expect(contractMapCases).toEqual([]);
        return;
      }

      const accepted = withMapEntry(
        fullContract,
        parent,
        "generatedKey",
        (samples as Samples).good,
      );
      expect(validateContract(accepted)).toBe(true);

      const rejected = withMapEntry(fullContract, parent, "generatedKey", (samples as Samples).bad);
      expect(validateContract(rejected)).toBe(false);
    },
  );
});

// ---------------------------------------------------------------------------
// Closed objects reject unknown properties
// ---------------------------------------------------------------------------

describe("closed objects reject unknown properties", () => {
  const closed = (objects: ObjectNode[]) =>
    objects.filter((o) => o.node.additionalProperties === false && o.path !== "");

  it.each(closed(designSystemObjects).map((o) => [o.path, o] as const))(
    "design system %s",
    (path) => {
      const payload = clone(fullDesignSystem);
      const container = resolve(payload, path);
      expect(container, `fixture does not reach ${path}`).toBeDefined();
      (container as Mutable).__notAKnownProperty = "x";

      expect(validateDesignSystem(payload), `${path} accepted an unknown property`).toBe(false);
    },
  );

  it.each(closed(contractObjects).map((o) => [o.path, o] as const))(
    "design contract %s",
    (path) => {
      const payload = clone(fullContract);
      const container = resolve(payload, path);
      expect(container, `fixture does not reach ${path}`).toBeDefined();
      (container as Mutable).__notAKnownProperty = "x";

      expect(validateContract(payload), `${path} accepted an unknown property`).toBe(false);
    },
  );

  it("the root of each schema rejects unknown properties", () => {
    expect(validateContract({ ...fullContract, __unknown: 1 })).toBe(false);
    expect(validateDesignSystem({ ...fullDesignSystem, __unknown: 1 })).toBe(false);
  });
});
