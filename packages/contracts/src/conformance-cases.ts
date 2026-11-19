import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";

/**
 * The conformance corpus for `design-contract.schema.json`.
 *
 * The schema is enforced twice: by Ajv in the studio, and by a .NET validator in
 * the API. Two validators for one schema agree only while something compares
 * them, so this module produces a list of documents together with **the verdict
 * Ajv gives each one**, and that list is checked in as
 * `conformance/design-contract.cases.json`:
 *
 * - `conformance.test.ts` fails if the checked-in file is not exactly what this
 *   module produces today — a schema change, an Ajv upgrade, or a hand edit.
 * - `SchemaConformanceTests` (.NET) fails if the API's validator gives any case
 *   a different verdict.
 *
 * Each case is raw JSON **text**, not a parsed value, because spelling matters:
 * `1`, `1.0` and `1e0` are one number to a validator and three strings to a
 * parser, and a JavaScript round trip would erase the difference.
 *
 * A failure is identified as `"<instance pointer> <keyword>"`. Messages are not
 * compared; they are for people and differ between implementations.
 *
 * Regenerate after a deliberate change with:
 *
 *     UPDATE_CONFORMANCE=1 npm test --workspace @aids/contracts
 */

export interface ConformanceCase {
  name: string;
  json: string;
  valid: boolean;
  failures: string[];
}

export interface ConformanceFile {
  schema: string;
  regenerate: string;
  cases: ConformanceCase[];
}

export const DESIGN_CONTRACT_CASES_PATH = fileURLToPath(
  new URL("../conformance/design-contract.cases.json", import.meta.url),
);

const schema = JSON.parse(
  readFileSync(
    fileURLToPath(new URL("../schemas/design-contract.schema.json", import.meta.url)),
    "utf8",
  ),
);

// The same options the studio's suites use.
const validate = new Ajv2020({ allErrors: true, strict: false }).compile(schema);

// eslint-disable-next-line @typescript-eslint/no-explicit-any -- cases deliberately build ill-typed documents
type Loose = any;

const base = (): Loose => ({
  schemaVersion: 1,
  product: { name: "Acme", platforms: ["web"] },
  informationArchitecture: {
    flows: [{ key: "checkout", name: "Checkout", steps: [{ screenKey: "cart" }] }],
  },
  screens: [{ key: "cart", title: "Cart", purpose: "Review items." }],
});

const full = (): Loose => ({
  schemaVersion: 1,
  product: {
    name: "Acme Storefront",
    summary: "Sells things.\nOn two lines, with ünïcödé.",
    platforms: ["web", "mobile", "tablet"],
    primaryAudience: "Shoppers",
  },
  informationArchitecture: {
    flows: [
      {
        key: "checkout",
        name: "Checkout",
        steps: [{ screenKey: "cart", note: "Entry point" }, { screenKey: "checkout/payment" }],
      },
      { key: "browse", name: "Browse", steps: [{ screenKey: "home" }] },
    ],
  },
  screens: [
    {
      key: "cart",
      title: "Cart",
      purpose: "Review items before paying.",
      requiredElements: ["Line items", "Total"],
      states: ["default", "empty", "loading", "error", "success", "offline"],
      devices: ["mobile", "desktop", "tablet", "agnostic"],
    },
    { key: "checkout/payment", title: "Payment", purpose: "Pay." },
    { key: "home", title: "Home", purpose: "Start." },
  ],
  accessibility: { conformanceTarget: "WCAG22AA", notes: "Keyboard first." },
  contentRules: { tone: "Plain", readingLevel: "Grade 8", prohibited: ["Jargon"] },
  assumptions: [
    { statement: "Users pay by card.", confidence: "medium", validationPlan: "Ask five." },
    { statement: "No guest checkout." },
  ],
});

const inputs: { name: string; json: string }[] = [];

const raw = (name: string, json: string) => inputs.push({ name, json });

const doc = (name: string, change?: (document: Loose) => void, from: () => Loose = base) => {
  const document = from();
  change?.(document);
  raw(name, JSON.stringify(document));
};

const repeat = (text: string, count: number) => text.repeat(count);

// --- Valid ----------------------------------------------------------------

doc("valid: minimal");
doc("valid: every optional section", undefined, full);
raw(
  "valid: schemaVersion spelled 1.0",
  JSON.stringify(base()).replace('"schemaVersion":1', '"schemaVersion":1.0'),
);
raw(
  "valid: schemaVersion spelled 1e0",
  JSON.stringify(base()).replace('"schemaVersion":1', '"schemaVersion":1e0'),
);
raw(
  "valid: schemaVersion spelled 10e-1",
  JSON.stringify(base()).replace('"schemaVersion":1', '"schemaVersion":10e-1'),
);
raw("valid: insignificant whitespace", JSON.stringify(base(), null, 4));

for (const key of ["a", "0", "a-b", "a/b", "a-b/c-d", "a1/b2-c3", "settings/billing/invoices"]) {
  doc(`valid: stable key ${key}`, (d) => {
    d.screens[0].key = key;
    d.informationArchitecture.flows[0].steps[0].screenKey = key;
  });
}

doc("valid: stable key of 128 characters", (d) => (d.screens[0].key = repeat("a", 128)));
doc("valid: product name of 200 characters", (d) => (d.product.name = repeat("n", 200)));
// 200 characters, 400 UTF-16 units. A validator that counts units rejects this.
doc(
  "valid: product name of 200 astral characters",
  (d) => (d.product.name = repeat("\u{1F600}", 200)),
);
doc("valid: purpose of 1000 characters", (d) => (d.screens[0].purpose = repeat("p", 1000)));
doc("valid: summary of 2000 characters", (d) => (d.product.summary = repeat("s", 2000)));
doc("valid: empty optional arrays", (d) => {
  d.screens[0].requiredElements = [];
  d.screens[0].states = [];
  d.screens[0].devices = [];
  d.contentRules = { prohibited: [] };
  d.assumptions = [];
});
doc("valid: empty optional objects", (d) => {
  d.accessibility = {};
  d.contentRules = {};
});
doc("valid: prohibited entry may be empty", (d) => (d.contentRules = { prohibited: [""] }));
doc("valid: duplicate required elements are allowed", (d) => {
  d.screens[0].requiredElements = ["Total", "Total"];
});

// Schema-valid on purpose. These are the rules JSON Schema cannot express; the
// API enforces the first separately (AC-2.7), and pinning the verdict here keeps
// either validator from quietly starting to reject them.
doc("valid for the schema: a flow step names an unlisted screen", (d) => {
  d.informationArchitecture.flows[0].steps[0].screenKey = "nowhere";
});
doc("valid for the schema: two screens share a key", (d) => {
  d.screens.push({ key: "cart", title: "Cart again", purpose: "Duplicate." });
});

// --- Root -----------------------------------------------------------------

raw("root: an array", "[]");
raw("root: a string", '"contract"');
raw("root: a number", "1");
raw("root: null", "null");
raw("root: true", "true");
raw("root: an empty object", "{}");

for (const property of ["schemaVersion", "product", "informationArchitecture", "screens"]) {
  doc(`root: missing ${property}`, (d) => delete d[property]);
}

doc("root: unknown property", (d) => (d.extra = true));
doc("root: two unknown properties", (d) => {
  d.extra = true;
  d.another = 1;
});

// --- schemaVersion --------------------------------------------------------

for (const [label, value] of [
  ["2", 2],
  ["0", 0],
  ["1.5", 1.5],
  ["-1", -1],
  ['the string "1"', "1"],
  ["null", null],
  ["true", true],
  ["an array", [1]],
  ["an object", { version: 1 }],
] as const) {
  doc(`schemaVersion: ${label}`, (d) => (d.schemaVersion = value));
}

// --- product --------------------------------------------------------------

doc("product: an array", (d) => (d.product = []));
doc("product: a string", (d) => (d.product = "Acme"));
doc("product: missing name", (d) => delete d.product.name);
doc("product: missing platforms", (d) => delete d.product.platforms);
doc("product: unknown property", (d) => (d.product.owner = "me"));
doc("product: empty name", (d) => (d.product.name = ""));
doc("product: name of 201 characters", (d) => (d.product.name = repeat("n", 201)));
doc("product: name of 201 astral characters", (d) => (d.product.name = repeat("\u{1F600}", 201)));
doc("product: name is a number", (d) => (d.product.name = 7));
doc("product: name is null", (d) => (d.product.name = null));
doc("product: summary of 2001 characters", (d) => (d.product.summary = repeat("s", 2001)));
doc("product: summary is a number", (d) => (d.product.summary = 1));
doc(
  "product: primaryAudience of 501 characters",
  (d) => (d.product.primaryAudience = repeat("a", 501)),
);
doc("product: no platforms", (d) => (d.product.platforms = []));
doc("product: a platform listed twice", (d) => (d.product.platforms = ["web", "web"]));
doc("product: an unknown platform", (d) => (d.product.platforms = ["desktop"]));
doc("product: a platform in another case", (d) => (d.product.platforms = ["Web"]));
doc("product: a numeric platform", (d) => (d.product.platforms = [1]));
doc("product: platforms is a string", (d) => (d.product.platforms = "web"));
doc("product: platforms is an object", (d) => (d.product.platforms = { web: true }));

// --- informationArchitecture ----------------------------------------------

doc("architecture: a string", (d) => (d.informationArchitecture = "flows"));
doc("architecture: missing flows", (d) => delete d.informationArchitecture.flows);
doc("architecture: unknown property", (d) => (d.informationArchitecture.sitemap = []));
doc("architecture: no flows", (d) => (d.informationArchitecture.flows = []));
doc("architecture: flows is an object", (d) => (d.informationArchitecture.flows = {}));
doc("flow: a string", (d) => (d.informationArchitecture.flows = ["checkout"]));

for (const property of ["key", "name", "steps"]) {
  doc(`flow: missing ${property}`, (d) => delete d.informationArchitecture.flows[0][property]);
}

doc("flow: unknown property", (d) => (d.informationArchitecture.flows[0].owner = "x"));
doc("flow: empty name", (d) => (d.informationArchitecture.flows[0].name = ""));
doc(
  "flow: name of 201 characters",
  (d) => (d.informationArchitecture.flows[0].name = repeat("n", 201)),
);
doc("flow: no steps", (d) => (d.informationArchitecture.flows[0].steps = []));
doc("flow: steps is a string", (d) => (d.informationArchitecture.flows[0].steps = "cart"));
doc("step: a string", (d) => (d.informationArchitecture.flows[0].steps = ["cart"]));
doc("step: missing screenKey", (d) => delete d.informationArchitecture.flows[0].steps[0].screenKey);
doc("step: unknown property", (d) => (d.informationArchitecture.flows[0].steps[0].order = 1));
doc(
  "step: note of 501 characters",
  (d) => (d.informationArchitecture.flows[0].steps[0].note = repeat("n", 501)),
);
doc(
  "step: screenKey is a number",
  (d) => (d.informationArchitecture.flows[0].steps[0].screenKey = 1),
);
doc(
  "second flow: second step is wrong",
  (d) =>
    (d.informationArchitecture.flows[1].steps = [{ screenKey: "home" }, { screenKey: "Bad Key" }]),
  full,
);

// --- stable keys ----------------------------------------------------------
// The pattern is where two regular-expression dialects are most likely to part.

for (const [label, key] of [
  ["empty", ""],
  ["uppercase", "Cart"],
  ["a space", "my cart"],
  ["an underscore", "my_cart"],
  ["a leading hyphen", "-cart"],
  ["a trailing hyphen", "cart-"],
  ["a doubled hyphen", "my--cart"],
  ["a leading slash", "/cart"],
  ["a trailing slash", "cart/"],
  ["a doubled slash", "shop//cart"],
  ["a hyphen beside a slash", "shop-/cart"],
  ["a dot", "cart.v2"],
  // .NET's `$` also matches before a final newline; ECMA-262's does not.
  ["a trailing newline", "cart\n"],
  ["a leading newline", "\ncart"],
  ["an embedded newline", "my\ncart"],
  ["a trailing carriage return", "cart\r"],
  ["a trailing space", "cart "],
  // Digits and letters outside ASCII, which \d and \w would admit in .NET.
  ["an Arabic-Indic digit", "cart٣"],
  ["a full-width letter", "ｃart"],
  ["an accented letter", "café"],
  ["a Cyrillic look-alike", "сart"],
  ["an emoji", "cart\u{1F600}"],
  ["129 characters", repeat("a", 129)],
] as const) {
  doc(`screen key: ${label}`, (d) => (d.screens[0].key = key));
}

doc("flow key: uppercase", (d) => (d.informationArchitecture.flows[0].key = "Checkout"));
doc("flow key: a trailing newline", (d) => (d.informationArchitecture.flows[0].key = "checkout\n"));
doc(
  "step screenKey: uppercase",
  (d) => (d.informationArchitecture.flows[0].steps[0].screenKey = "Cart"),
);
doc("screen key: a number", (d) => (d.screens[0].key = 5));
doc("screen key: null", (d) => (d.screens[0].key = null));

// --- screens --------------------------------------------------------------

doc("screens: none", (d) => (d.screens = []));
doc("screens: an object", (d) => (d.screens = { cart: {} }));
doc("screens: a string", (d) => (d.screens = "cart"));
doc("screen: a string", (d) => (d.screens = ["cart"]));
doc("screen: null", (d) => (d.screens = [null]));

for (const property of ["key", "title", "purpose"]) {
  doc(`screen: missing ${property}`, (d) => delete d.screens[0][property]);
}

doc("screen: unknown property", (d) => (d.screens[0].route = "/cart"));
doc("screen: empty title", (d) => (d.screens[0].title = ""));
doc("screen: title of 201 characters", (d) => (d.screens[0].title = repeat("t", 201)));
doc("screen: empty purpose", (d) => (d.screens[0].purpose = ""));
doc("screen: purpose of 1001 characters", (d) => (d.screens[0].purpose = repeat("p", 1001)));
doc("screen: an empty required element", (d) => (d.screens[0].requiredElements = [""]));
doc(
  "screen: a required element of 201 characters",
  (d) => (d.screens[0].requiredElements = [repeat("e", 201)]),
);
doc("screen: a numeric required element", (d) => (d.screens[0].requiredElements = [1]));
doc("screen: requiredElements is a string", (d) => (d.screens[0].requiredElements = "Total"));
doc("screen: a state listed twice", (d) => (d.screens[0].states = ["empty", "empty"]));
doc("screen: an unknown state", (d) => (d.screens[0].states = ["hover"]));
doc("screen: states is a string", (d) => (d.screens[0].states = "default"));
doc("screen: a device listed twice", (d) => (d.screens[0].devices = ["mobile", "mobile"]));
doc("screen: an unknown device", (d) => (d.screens[0].devices = ["phone"]));
doc("third screen: missing title", (d) => delete d.screens[2].title, full);

// --- accessibility, contentRules, assumptions -----------------------------

doc("accessibility: a string", (d) => (d.accessibility = "AA"));
doc("accessibility: unknown property", (d) => (d.accessibility = { level: "AA" }));
doc(
  "accessibility: an unknown conformance target",
  (d) => (d.accessibility = { conformanceTarget: "WCAG20AA" }),
);
doc(
  "accessibility: notes of 2001 characters",
  (d) => (d.accessibility = { notes: repeat("n", 2001) }),
);
doc("contentRules: an array", (d) => (d.contentRules = []));
doc("contentRules: unknown property", (d) => (d.contentRules = { voice: "x" }));
doc("contentRules: tone of 501 characters", (d) => (d.contentRules = { tone: repeat("t", 501) }));
doc(
  "contentRules: readingLevel of 101 characters",
  (d) => (d.contentRules = { readingLevel: repeat("r", 101) }),
);
doc(
  "contentRules: a prohibited entry of 201 characters",
  (d) => (d.contentRules = { prohibited: [repeat("p", 201)] }),
);
doc("contentRules: a numeric prohibited entry", (d) => (d.contentRules = { prohibited: [1] }));
doc("contentRules: prohibited is a string", (d) => (d.contentRules = { prohibited: "x" }));
doc("assumptions: an object", (d) => (d.assumptions = {}));
doc("assumption: a string", (d) => (d.assumptions = ["Users pay by card."]));
doc("assumption: missing statement", (d) => (d.assumptions = [{ confidence: "low" }]));
doc("assumption: empty statement", (d) => (d.assumptions = [{ statement: "" }]));
doc(
  "assumption: statement of 1001 characters",
  (d) => (d.assumptions = [{ statement: repeat("s", 1001) }]),
);
doc(
  "assumption: an unknown confidence",
  (d) => (d.assumptions = [{ statement: "x", confidence: "certain" }]),
);
doc(
  "assumption: validationPlan of 1001 characters",
  (d) => (d.assumptions = [{ statement: "x", validationPlan: repeat("v", 1001) }]),
);
doc("assumption: unknown property", (d) => (d.assumptions = [{ statement: "x", owner: "me" }]));

// --- Several at once ------------------------------------------------------

doc("several: one failure in each section", (d) => {
  d.schemaVersion = 2;
  d.product.name = "";
  d.product.platforms = ["web", "web", "desktop"];
  d.informationArchitecture.flows[0].key = "Bad Key";
  d.screens[0].states = ["hover", "hover"];
  d.accessibility = { conformanceTarget: "none", extra: 1 };
});
doc("several: a type error hides nothing beside it", (d) => {
  d.product = 5;
  d.screens = "none";
  d.extra = null;
});

/** Ajv's verdict on one raw JSON text. */
export const ajvVerdict = (json: string): { valid: boolean; failures: string[] } => {
  const valid = validate(JSON.parse(json));
  const failures = [
    ...new Set((validate.errors ?? []).map((error) => `${error.instancePath} ${error.keyword}`)),
  ].sort();

  return { valid, failures };
};

export const buildDesignContractCases = (): ConformanceFile => ({
  schema: "design-contract.schema.json",
  regenerate: "UPDATE_CONFORMANCE=1 npm test --workspace @aids/contracts",
  cases: inputs.map(({ name, json }) => ({ name, json, ...ajvVerdict(json) })),
});

/** The keywords in the schema that can reject a value. */
export const validationKeywordsIn = (node: unknown, found = new Set<string>()): Set<string> => {
  const annotations = new Set([
    "$schema",
    "$id",
    "$defs",
    "$ref",
    "title",
    "description",
    "default",
  ]);
  const holdsSchemas = new Set(["properties", "$defs"]);
  const holdsValues = new Set(["enum", "const", "default", "required"]);

  if (Array.isArray(node)) {
    node.forEach((item) => validationKeywordsIn(item, found));
  } else if (node !== null && typeof node === "object") {
    for (const [keyword, value] of Object.entries(node)) {
      if (!annotations.has(keyword) && keyword !== "properties" && keyword !== "items") {
        found.add(keyword);
      }

      if (holdsSchemas.has(keyword)) {
        Object.values(value as object).forEach((child) => validationKeywordsIn(child, found));
      } else if (!holdsValues.has(keyword)) {
        validationKeywordsIn(value, found);
      }
    }
  }

  return found;
};

export const designContractSchema: unknown = schema;
