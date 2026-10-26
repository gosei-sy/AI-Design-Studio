import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import { describe, expect, it } from "vitest";

import {
  DESIGN_CONTRACT_SCHEMA_VERSION,
  DESIGN_SYSTEM_SCHEMA_VERSION,
  type DesignContract,
  type DesignSystem,
} from "./index.js";

const load = (name: string) =>
  JSON.parse(readFileSync(fileURLToPath(new URL(`../schemas/${name}`, import.meta.url)), "utf8"));

const contractSchema = load("design-contract.schema.json");
const designSystemSchema = load("design-system.schema.json");

const ajv = new Ajv2020({ allErrors: true, strict: false });
const validateContract = ajv.compile(contractSchema);
const validateDesignSystem = ajv.compile(designSystemSchema);

/**
 * A minimal contract that satisfies every required field. Typed as
 * DesignContract so a divergence between the TypeScript types and the schema
 * fails either typecheck or this test, rather than drifting unnoticed.
 */
const minimalContract: DesignContract = {
  schemaVersion: DESIGN_CONTRACT_SCHEMA_VERSION,
  product: { name: "Acme Storefront", platforms: ["web"] },
  informationArchitecture: {
    flows: [{ key: "checkout", name: "Checkout", steps: [{ screenKey: "cart" }] }],
  },
  screens: [{ key: "cart", title: "Cart", purpose: "Review items before paying." }],
};

const minimalDesignSystem: DesignSystem = {
  schemaVersion: DESIGN_SYSTEM_SCHEMA_VERSION,
  name: "Acme",
  typography: { bodyFont: "Inter", headlineFont: "Manrope" },
  color: { roles: { primary: "#3366ff" } },
  shape: { radiusBase: "8px" },
};

describe("design contract schema", () => {
  it("accepts a minimal valid contract", () => {
    expect(validateContract(minimalContract), JSON.stringify(validateContract.errors)).toBe(true);
  });

  it("accepts a fully populated contract", () => {
    const full: DesignContract = {
      ...minimalContract,
      product: {
        name: "Acme Storefront",
        summary: "Buy things.",
        platforms: ["web", "mobile"],
        primaryAudience: "Returning customers",
      },
      screens: [
        {
          key: "cart",
          title: "Cart",
          purpose: "Review items before paying.",
          requiredElements: ["line items", "order total"],
          states: ["default", "empty", "error"],
          devices: ["mobile", "desktop"],
        },
      ],
      accessibility: { conformanceTarget: "WCAG22AA", notes: "Keyboard-first." },
      contentRules: { tone: "Plain", prohibited: ["synergy"] },
      assumptions: [
        {
          statement: "Users arrive from email campaigns.",
          confidence: "low",
          validationPlan: "Check referrer analytics before M02.",
        },
      ],
    };

    expect(validateContract(full), JSON.stringify(validateContract.errors)).toBe(true);
  });

  it.each([
    ["schemaVersion", { ...minimalContract, schemaVersion: 2 }],
    ["product", { ...minimalContract, product: undefined }],
    ["screens", { ...minimalContract, screens: [] }],
    ["flows", { ...minimalContract, informationArchitecture: { flows: [] } }],
  ])("rejects an invalid %s", (_field, payload) => {
    expect(validateContract(payload)).toBe(false);
  });

  it("rejects unknown top-level properties so typos are caught, not ignored", () => {
    expect(validateContract({ ...minimalContract, screenz: [] })).toBe(false);
  });

  it.each(["Checkout", "check out", "/cart", "cart_page", "cart-"])(
    "rejects %s as a stable key",
    (key) => {
      expect(
        validateContract({
          ...minimalContract,
          screens: [{ key, title: "T", purpose: "P" }],
        }),
      ).toBe(false);
    },
  );

  it.each(["cart", "checkout/payment", "step-1"])("accepts %s as a stable key", (key) => {
    expect(
      validateContract({
        ...minimalContract,
        informationArchitecture: { flows: [{ key: "f", name: "F", steps: [{ screenKey: key }] }] },
        screens: [{ key, title: "T", purpose: "P" }],
      }),
    ).toBe(true);
  });

  it("rejects an unknown screen state", () => {
    expect(
      validateContract({
        ...minimalContract,
        screens: [{ key: "cart", title: "T", purpose: "P", states: ["sideways"] }],
      }),
    ).toBe(false);
  });
});

describe("design system schema", () => {
  it("accepts a minimal valid design system", () => {
    expect(
      validateDesignSystem(minimalDesignSystem),
      JSON.stringify(validateDesignSystem.errors),
    ).toBe(true);
  });

  it.each(["#fff", "#3366ff", "#3366ffcc"])("accepts %s as a colour", (primary) => {
    expect(validateDesignSystem({ ...minimalDesignSystem, color: { roles: { primary } } })).toBe(
      true,
    );
  });

  it.each(["3366ff", "rgb(1,2,3)", "#12345", "blue"])("rejects %s as a colour", (primary) => {
    expect(validateDesignSystem({ ...minimalDesignSystem, color: { roles: { primary } } })).toBe(
      false,
    );
  });

  it.each(["8px", "0.5rem", "0", "100%"])("accepts %s as a length", (radiusBase) => {
    expect(validateDesignSystem({ ...minimalDesignSystem, shape: { radiusBase } })).toBe(true);
  });

  it.each(["8", "8 px", "big"])("rejects %s as a length", (radiusBase) => {
    expect(validateDesignSystem({ ...minimalDesignSystem, shape: { radiusBase } })).toBe(false);
  });

  /**
   * These sections have no provider-theme equivalent. They must still be valid
   * here: the design system is the source of truth, and the projection reports
   * them as unrepresentable rather than the schema forbidding them.
   */
  it("accepts sections that no provider theme can express", () => {
    const rich: DesignSystem = {
      ...minimalDesignSystem,
      elevation: { card: "0 1px 2px rgba(0,0,0,.08)" },
      motion: { standard: "200ms ease-out" },
      breakpoints: { md: "768px" },
      borders: { width: "1px" },
      components: { button: { variants: ["primary", "ghost"], rules: ["min 44px target"] } },
    };

    expect(validateDesignSystem(rich), JSON.stringify(validateDesignSystem.errors)).toBe(true);
  });

  it("accepts arbitrary semantic colour roles beyond the four provider slots", () => {
    expect(
      validateDesignSystem({
        ...minimalDesignSystem,
        color: { roles: { primary: "#000000", danger: "#dc2626", success: "#16a34a" } },
      }),
    ).toBe(true);
  });

  it("allows declaring both colour modes even though only one projects", () => {
    expect(
      validateDesignSystem({
        ...minimalDesignSystem,
        color: { roles: { primary: "#000000" }, modes: ["light", "dark"] },
      }),
    ).toBe(true);
  });

  /**
   * Pins the documented limit of the TypeScript mirror rather than leaving it as
   * a comment someone has to believe.
   *
   * `"red"` is a valid `HexColor` — the alias is `string` — so the annotation
   * below compiles. The schema rejects it. That is the whole claim in
   * `index.ts`: structural alignment, with format left to runtime validation.
   * If the types were ever generated and made exact, this file would stop
   * compiling, which is the correct way to find out.
   */
  it("accepts at compile time what the schema rejects at runtime, for lexical constraints", () => {
    const typeChecksButIsInvalid: DesignSystem = {
      ...minimalDesignSystem,
      color: { roles: { primary: "red" } },
    };

    expect(validateDesignSystem(typeChecksButIsInvalid)).toBe(false);
  });

  it("requires typography and colour", () => {
    expect(validateDesignSystem({ ...minimalDesignSystem, typography: undefined })).toBe(false);
    expect(validateDesignSystem({ ...minimalDesignSystem, color: undefined })).toBe(false);
  });
});

describe("schema identity", () => {
  it("pins each schema id and version so a breaking change is deliberate", () => {
    expect(contractSchema.$id).toBe("https://aidesignstudio.dev/schemas/design-contract-v1.json");
    expect(designSystemSchema.$id).toBe("https://aidesignstudio.dev/schemas/design-system-v1.json");
    expect(contractSchema.properties.schemaVersion.const).toBe(DESIGN_CONTRACT_SCHEMA_VERSION);
    expect(designSystemSchema.properties.schemaVersion.const).toBe(DESIGN_SYSTEM_SCHEMA_VERSION);
  });
});
