import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { describe, expect, it } from "vitest";

import {
  DESIGN_CONTRACT_CASES_PATH,
  buildDesignContractCases,
  designContractSchema,
  validationKeywordsIn,
  type ConformanceFile,
} from "./conformance-cases.js";

/**
 * Keeps `conformance/design-contract.cases.json` honest.
 *
 * That file is what the API's .NET validator is tested against, so it has to be
 * exactly what Ajv says about the current schema — not what Ajv said when
 * someone last remembered to regenerate it. See `conformance-cases.ts`.
 */

const built = buildDesignContractCases();
const serialized = `${JSON.stringify(built, null, 2)}\n`;

if (process.env.UPDATE_CONFORMANCE === "1") {
  mkdirSync(dirname(DESIGN_CONTRACT_CASES_PATH), { recursive: true });
  writeFileSync(DESIGN_CONTRACT_CASES_PATH, serialized);
}

describe("design contract conformance corpus", () => {
  it("is checked in exactly as Ajv evaluates the current schema", () => {
    expect(
      existsSync(DESIGN_CONTRACT_CASES_PATH),
      "run UPDATE_CONFORMANCE=1 npm test --workspace @aids/contracts",
    ).toBe(true);

    const checkedIn = JSON.parse(
      readFileSync(DESIGN_CONTRACT_CASES_PATH, "utf8"),
    ) as ConformanceFile;

    // Compared as data, so a formatter may re-wrap the file without failing this.
    expect(checkedIn).toEqual(built);
  });

  it("names every case uniquely", () => {
    const names = built.cases.map((c) => c.name);

    expect(new Set(names).size).toBe(names.length);
  });

  /**
   * A corpus that never trips a keyword says nothing about how either validator
   * implements it. If the schema gains a keyword, this fails until a case
   * exercises it — which is also when the .NET validator has to learn it.
   */
  it("makes every validation keyword in the schema fail at least once", () => {
    const tripped = new Set(
      built.cases.flatMap((c) => c.failures.map((failure) => failure.split(" ").pop() as string)),
    );

    expect([...validationKeywordsIn(designContractSchema)].sort()).toEqual([...tripped].sort());
  });

  it("holds both verdicts in quantity", () => {
    const valid = built.cases.filter((c) => c.valid).length;

    expect(valid).toBeGreaterThanOrEqual(20);
    expect(built.cases.length - valid).toBeGreaterThanOrEqual(100);
  });

  it("marks a case invalid exactly when it lists a failure", () => {
    for (const c of built.cases) {
      expect(c.failures.length === 0, c.name).toBe(c.valid);
    }
  });
});
