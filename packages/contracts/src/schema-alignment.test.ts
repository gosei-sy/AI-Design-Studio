import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import { describe, expect, it } from "vitest";

import type {
  DesignContract,
  DesignSystem,
  Flow,
  FlowStep,
  ProductAssumption,
  ScreenObligation,
  TypeStyle,
} from "./index.js";

/**
 * Structural alignment between the JSON Schemas and the TypeScript mirrors.
 *
 * `contracts.test.ts` validates hand-written fixtures, which proves those
 * examples are compatible but not that the two representations have the same
 * shape: a property could be added, removed or renamed on one side without any
 * fixture noticing. That gap matters because M01 validates with the schema at
 * the API boundary while the UI is typed from `index.ts` — drift would let the
 * frontend accept data the API rejects.
 *
 * The check is bidirectional, and each direction fails differently:
 *
 *   - **Schema gains or loses a property** → the runtime assertions below fail,
 *     because the declared key list no longer matches the schema.
 *   - **TypeScript gains or loses a property** → `npm run typecheck` fails,
 *     because `AssertSameKeys` resolves to `never`.
 *
 * So the key lists are not documentation; they are the pivot both sides are
 * compared against.
 */

const load = (name: string) =>
  JSON.parse(readFileSync(fileURLToPath(new URL(`../schemas/${name}`, import.meta.url)), "utf8"));

const contractSchema = load("design-contract.schema.json");
const designSystemSchema = load("design-system.schema.json");

const ajv = new Ajv2020({ allErrors: true, strict: false });
const validateContract = ajv.compile(contractSchema);
const validateDesignSystem = ajv.compile(designSystemSchema);

/**
 * Resolves to `true` only when A and B are the same union of string literals.
 * Any difference resolves to `never`, which fails to typecheck at the use site.
 */
type AssertSameKeys<A extends string, B extends string> = [A] extends [B]
  ? [B] extends [A]
    ? true
    : never
  : never;

// ---------------------------------------------------------------------------
// Declared key lists, pinned to the TypeScript types at compile time
// ---------------------------------------------------------------------------

const CONTRACT_KEYS = [
  "schemaVersion",
  "product",
  "informationArchitecture",
  "screens",
  "accessibility",
  "contentRules",
  "assumptions",
] as const;
const _contractKeys: AssertSameKeys<(typeof CONTRACT_KEYS)[number], keyof DesignContract> = true;

const PRODUCT_KEYS = ["name", "summary", "platforms", "primaryAudience"] as const;
const _productKeys: AssertSameKeys<(typeof PRODUCT_KEYS)[number], keyof DesignContract["product"]> =
  true;

const SCREEN_KEYS = ["key", "title", "purpose", "requiredElements", "states", "devices"] as const;
const _screenKeys: AssertSameKeys<(typeof SCREEN_KEYS)[number], keyof ScreenObligation> = true;

const FLOW_KEYS = ["key", "name", "steps"] as const;
const _flowKeys: AssertSameKeys<(typeof FLOW_KEYS)[number], keyof Flow> = true;

const FLOW_STEP_KEYS = ["screenKey", "note"] as const;
const _flowStepKeys: AssertSameKeys<(typeof FLOW_STEP_KEYS)[number], keyof FlowStep> = true;

const ASSUMPTION_KEYS = ["statement", "confidence", "validationPlan"] as const;
const _assumptionKeys: AssertSameKeys<(typeof ASSUMPTION_KEYS)[number], keyof ProductAssumption> =
  true;

const DESIGN_SYSTEM_KEYS = [
  "schemaVersion",
  "name",
  "typography",
  "color",
  "shape",
  "spacing",
  "elevation",
  "motion",
  "breakpoints",
  "borders",
  "components",
] as const;
const _designSystemKeys: AssertSameKeys<(typeof DESIGN_SYSTEM_KEYS)[number], keyof DesignSystem> =
  true;

const TYPOGRAPHY_KEYS = ["bodyFont", "headlineFont", "labelFont", "scale"] as const;
const _typographyKeys: AssertSameKeys<
  (typeof TYPOGRAPHY_KEYS)[number],
  keyof DesignSystem["typography"]
> = true;

const TYPE_STYLE_KEYS = [
  "fontFamily",
  "fontSize",
  "fontWeight",
  "lineHeight",
  "letterSpacing",
] as const;
const _typeStyleKeys: AssertSameKeys<(typeof TYPE_STYLE_KEYS)[number], keyof TypeStyle> = true;

// Reference the witnesses so `noUnusedLocals` does not strip them.
void [
  _contractKeys,
  _productKeys,
  _screenKeys,
  _flowKeys,
  _flowStepKeys,
  _assumptionKeys,
  _designSystemKeys,
  _typographyKeys,
  _typeStyleKeys,
];

// ---------------------------------------------------------------------------
// Runtime side: the same lists must match the schemas exactly
// ---------------------------------------------------------------------------

interface SchemaNode {
  properties?: Record<string, unknown>;
  required?: string[];
  items?: SchemaNode;
  $defs?: Record<string, SchemaNode>;
}

const keysOf = (node: SchemaNode) => Object.keys(node.properties ?? {}).sort();
const expected = (keys: readonly string[]) => [...keys].sort();

describe("design contract: schema and TypeScript declare the same properties", () => {
  const schema = contractSchema as SchemaNode;

  it.each([
    ["top level", schema, CONTRACT_KEYS],
    ["product", schema.properties!.product as SchemaNode, PRODUCT_KEYS],
    ["screen obligation", (schema.properties!.screens as SchemaNode).items!, SCREEN_KEYS],
    [
      "flow",
      ((schema.properties!.informationArchitecture as SchemaNode).properties!.flows as SchemaNode)
        .items!,
      FLOW_KEYS,
    ],
    [
      "flow step",
      (
        ((schema.properties!.informationArchitecture as SchemaNode).properties!.flows as SchemaNode)
          .items!.properties!.steps as SchemaNode
      ).items!,
      FLOW_STEP_KEYS,
    ],
    ["assumption", (schema.properties!.assumptions as SchemaNode).items!, ASSUMPTION_KEYS],
  ])("%s", (_label, node, keys) => {
    expect(keysOf(node)).toEqual(expected(keys));
  });
});

describe("design system: schema and TypeScript declare the same properties", () => {
  const schema = designSystemSchema as SchemaNode;

  it.each([
    ["top level", schema, DESIGN_SYSTEM_KEYS],
    ["typography", schema.properties!.typography as SchemaNode, TYPOGRAPHY_KEYS],
  ])("%s", (_label, node, keys) => {
    expect(keysOf(node)).toEqual(expected(keys));
  });

  it("type style", () => {
    const scale = (schema.properties!.typography as SchemaNode).properties!.scale as {
      additionalProperties: SchemaNode;
    };

    expect(keysOf(scale.additionalProperties)).toEqual(expected(TYPE_STYLE_KEYS));
  });
});

// ---------------------------------------------------------------------------
// Required vs optional, property by property
// ---------------------------------------------------------------------------

const fullContract: DesignContract = {
  schemaVersion: 1,
  product: {
    name: "Acme Storefront",
    summary: "Buy things.",
    platforms: ["web", "mobile"],
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
      requiredElements: ["line items"],
      states: ["default", "empty"],
      devices: ["mobile"],
    },
  ],
  accessibility: { conformanceTarget: "WCAG22AA", notes: "Keyboard-first." },
  contentRules: { tone: "Plain", readingLevel: "B2", prohibited: ["synergy"] },
  assumptions: [{ statement: "Users arrive from email.", confidence: "low", validationPlan: "x" }],
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
  color: { roles: { primary: "#3366ff", secondary: "#22c55e" }, modes: ["light"] },
  shape: { radiusBase: "8px" },
  spacing: { md: "16px" },
  elevation: { card: "0 1px 2px rgba(0,0,0,.08)" },
  motion: { standard: "200ms ease-out" },
  breakpoints: { md: "768px" },
  borders: { width: "1px" },
  components: { button: { variants: ["primary"], rules: ["44px target"] } },
};

const without = <T extends object>(source: T, key: keyof T) => {
  const copy = { ...source };
  delete copy[key];
  return copy;
};

describe("every declared property is exercised against its required/optional rule", () => {
  it("a fully populated contract is valid, so no optional field is secretly rejected", () => {
    expect(validateContract(fullContract), JSON.stringify(validateContract.errors)).toBe(true);
  });

  it("a fully populated design system is valid", () => {
    expect(
      validateDesignSystem(fullDesignSystem),
      JSON.stringify(validateDesignSystem.errors),
    ).toBe(true);
  });

  it.each(contractSchema.required as string[])(
    "contract rejects a payload missing the required '%s'",
    (key) => {
      expect(validateContract(without(fullContract, key as keyof DesignContract))).toBe(false);
    },
  );

  it.each(CONTRACT_KEYS.filter((k) => !(contractSchema.required as string[]).includes(k)))(
    "contract accepts a payload omitting the optional '%s'",
    (key) => {
      expect(
        validateContract(without(fullContract, key)),
        JSON.stringify(validateContract.errors),
      ).toBe(true);
    },
  );

  it.each(designSystemSchema.required as string[])(
    "design system rejects a payload missing the required '%s'",
    (key) => {
      expect(validateDesignSystem(without(fullDesignSystem, key as keyof DesignSystem))).toBe(
        false,
      );
    },
  );

  it.each(DESIGN_SYSTEM_KEYS.filter((k) => !(designSystemSchema.required as string[]).includes(k)))(
    "design system accepts a payload omitting the optional '%s'",
    (key) => {
      expect(
        validateDesignSystem(without(fullDesignSystem, key)),
        JSON.stringify(validateDesignSystem.errors),
      ).toBe(true);
    },
  );
});
