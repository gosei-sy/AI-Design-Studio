# ADR-0010: Payload schemas are validated in-process and pinned to the studio's validator

- **Status:** **Proposed (M01-05)** — implemented under delegated authorization
  on 2026-11-19; **not accepted**. It can be overturned without touching any
  endpoint: the validator sits behind a port.
- **Date:** 2026-11-19
- **Deciders:** to be accepted by the Solution Architect

## Context

M01-05 is where the API first enforces `design-contract.schema.json`
(AC-2.6). The plan's instruction is to use **the same JSON Schema files the
frontend uses**, because two copies of a contract definition diverge. The studio
validates with Ajv. The API is .NET, so the same _file_ still means a second
_implementation_ reading it.

Three facts shaped the choice.

**1. The usual .NET library is no longer free to use here.** `JsonSchema.Net`
is the de facto draft 2020-12 validator. Its NuGet licence was `MIT` through
8.0.0; the current release, 9.7.0, declares `<license type="file">OSMFEULA.txt`
— an end-user licence that charges commercial users a maintenance fee. Checked
against the NuGet registry on 2026-11-19. Rule 10 forbids spending money with an
external provider unasked, and pinning to the last MIT release would mean
running an unmaintained line of a library that parses untrusted input.

**2. The alternatives are larger than the problem.** `NJsonSchema` (MIT) does
not implement draft 2020-12 and brings Newtonsoft.Json. `Corvus.JsonSchema`
(Apache-2.0) does, through a build-time source generator and a runtime with a
substantial dependency tree. `LateApexEarlySpeed.Json.Schema` (BSD-3-Clause) is
a credible fit but a single-maintainer package, and item 1 is a reminder of how
that can change.

**3. The schemas are small, and a library would not have removed the real
risk.** Both files in `packages/contracts/schemas` use the same nineteen
keywords — no `oneOf`, no `format`, no numeric bounds. And any second
implementation, bought or written, can disagree with Ajv in ways a specification
test suite does not cover for _our_ schema: a .NET `$` also matches before a
final newline, `\d` admits non-ASCII digits, `string.Length` counts UTF-16 units
where the specification counts characters. Whatever validated the payload,
something had to compare it with the studio's validator.

## Decision

**1. The API validates with its own small validator,
`AIDesignStudio.Infrastructure.Schemas.JsonSchemaValidator`**, reading the
schema file embedded from `packages/contracts/schemas` at build time. One file,
no copy.

**2. It fails closed.** Loading a schema that uses any keyword, keyword form or
regular-expression construct it does not implement throws. A schema change that
needs something new breaks a test instead of being silently ignored — which, for
a validator, would mean accepting what the studio rejects.

**3. Patterns are accepted only where the two regex dialects are identical.**
ECMA-262 patterns are rewritten for .NET by translating `$` to `\z` and refusing
everything whose meaning differs: class escapes, `.`, look-around, named groups,
subtraction. Matching uses the non-backtracking engine, so no input can make a
pattern expensive.

**4. It is pinned to Ajv by a shared corpus, not by a claim.**
`packages/contracts/conformance/design-contract.cases.json` holds 157 documents
— as raw JSON text, so number spelling survives — each with Ajv's verdict and
the set of `pointer keyword` failures. Two tests guard it from opposite sides:

- `conformance.test.ts` (contracts package) fails unless the checked-in file is
  exactly what Ajv produces for the current schema, and unless every validation
  keyword in the schema fails in at least one case.
- `SchemaConformanceTests` (.NET) fails unless the API's validator gives every
  case the same verdict and the same failures.

**5. The API is deliberately stricter than the schema where the store demands
it.** PostgreSQL `jsonb` keeps one member per name, refuses U+0000 and unpaired
surrogates, and holds numbers as `numeric`. A body relying on any of those is
refused with a pointer before the schema is consulted, because it would
otherwise be a 500 at `SaveChanges` or be stored as a different value from the
one validated. What is stored is the validator's re-serialization of the single
parse it checked.

**6. The mechanism sits behind `IDesignContractPayloadValidator`** in the
Application layer. Replacing the implementation with a library is a change to
one class and one registration, and the corpus would then judge the library.

## Consequences

**Good**

- No dependency, no licence exposure, nothing to audit or upgrade.
- "The API and the studio enforce the same contract" is a tested statement in
  both directions rather than an expectation.
- A schema change that outruns either validator is caught where it is made.

**Costs, accepted**

- We own a validator. It is about 650 lines, comments included, and deliberately incomplete; the
  temptation to grow it into a general one should be resisted. A schema that
  needs `oneOf`, `format` or numeric ranges is the moment to revisit this ADR,
  not to extend the class quietly.
- The corpus must be regenerated when the schema changes
  (`UPDATE_CONFORMANCE=1 npm test --workspace @aids/contracts`). Forgetting
  fails `npm test`, so it cannot be forgotten silently.
- Conformance is proven for the corpus, not for all inputs. The corpus is built
  to cover every keyword at every level and the known dialect traps, but it is a
  sample.
- The validator differs from Ajv on purpose in one direction (decision 5).
  Those cases are outside the corpus and covered by
  `DesignContractPayloadValidatorTests`; the studio can accept, client-side, a
  document the API then refuses with a pointer.

**Not decided here**

- The design system schema (M01-06) uses the same keywords and loads in the same
  validator, but has no conformance corpus yet. It needs one before M01-06 can
  make the same claim.
- Duplicate screen keys, and duplicate flow keys, are schema-valid and are not
  rejected. Whether they should be is a product rule, not a validation mechanism,
  and is recorded in the plan under M01-05.

## Alternatives considered

| Option                                 | Why not                                                                        |
| :------------------------------------- | :----------------------------------------------------------------------------- |
| `JsonSchema.Net` 9.x                   | Fee-bearing licence for commercial use; needs a spending decision              |
| `JsonSchema.Net` pinned to 8.0.0 (MIT) | Unmaintained line parsing untrusted input; `--vulnerable` gate would go stale  |
| `Corvus.JsonSchema`                    | Source generator and dependency tree out of proportion to nineteen keywords    |
| `LateApexEarlySpeed.Json.Schema`       | Plausible; single maintainer, and would still have needed the corpus           |
| `NJsonSchema`                          | Not draft 2020-12                                                              |
| Validate only in the studio            | The API is the trust boundary; a client is never the only check (S-10)         |
| Generate C# types from the schema      | Solves shape, not the value rules (`pattern`, lengths) that are the whole risk |
