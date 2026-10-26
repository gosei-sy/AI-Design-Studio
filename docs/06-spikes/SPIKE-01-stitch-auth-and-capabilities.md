# SPIKE-01: Google Stitch SDK authentication and capabilities

**Status:** ✅ **Verified by direct execution**
**Date:** 2026-10-20 · **Subject:** `@google/stitch` 0.11.1

---

## Question

Does Google Stitch expose a programmatic interface we can build on, how does
authentication work, and what can it actually do?

## Method

The package was downloaded and executed directly. Nothing below is taken from
documentation alone; where the README and the machine-readable schema disagreed,
**the schema won**.

```bash
npm view @google/stitch --json
npm pack @google/stitch && tar xzf google-stitch-0.11.1.tgz
node package/dist/stitch.js --help
node package/dist/stitch.js status --json
node package/dist/stitch.js generate screen --schema
node package/dist/stitch.js edit design-system --schema
node package/dist/stitch.js generate screen --new-project "Probe" \
  --prompt "Login screen" --device MOBILE --dry-run --json
node package/dist/stitch.js get screen 12345 --project 99999 --json
```

## Package identity — verified

| Field               | Value                                              |
| :------------------ | :------------------------------------------------- |
| Name / version      | `@google/stitch` 0.11.1                            |
| Published           | 2026-10-01; 0.11.1 on 2026-10-08                   |
| Author / licence    | Google LLC · Apache-2.0                            |
| Repository          | `github.com/google-labs-code/stitch-sdk`           |
| Description         | "Agent-first CLI and MCP server for Google Stitch" |
| Binaries            | `stitch`, `stitch-mcp`                             |
| Engine              | Node ≥ 20                                          |
| Runtime dependency  | `zod` only                                         |
| **Library exports** | **`./ingest` only** (`defineIngest`)               |

### Finding 1 — There is no client library for canvas operations

The package's only library export is `defineIngest`, for configuring codebase
ingestion. There is no published client for projects, screens or design systems.

**Consequence:** the adapter drives the CLI as a subprocess. Every command
supports `--json` for output and `--schema` for its input contract, which makes
the CLI a genuinely programmable surface rather than a fallback. Recorded as
ADR-0003, _Not chosen → HTTP client_.

---

## Authentication — verified

```
$ stitch status --json
{
  "authenticated": false,
  "credentialsPath": "/home/claude/.stitch/credentials.json",
  "globalConfigPath": "/home/claude/.stitch.json",
  "experiments": { "loop": false },
  "canvas": {
    "credential": "STITCH_API_KEY",
    "present": false,
    "authMethod": "none",
    "requiredFor": "projects, screens, captures and design systems"
  }
}
```

Exit code 0.

### Finding 2 — Two independent credentials, and OAuth alone is not enough

| Credential       | Obtained by                                                                      | Covers                                          |
| :--------------- | :------------------------------------------------------------------------------- | :---------------------------------------------- |
| Google OAuth     | `stitch login` — **interactive**: opens a browser, listens on loopback port 8085 | workspaces, priorities, insights, solutions     |
| `STITCH_API_KEY` | Out of band                                                                      | **projects, screens, captures, design systems** |

Everything this product needs sits behind the API key, not behind OAuth.

The CLI's own remediation text confirms the split:

> Set `STITCH_API_KEY` (environment or .env) to work with projects, screens,
> captures and design systems. It is separate from your Google sign-in, which
> already covers workspaces, priorities, insights and solutions.

**Consequence:** `ProviderReadiness` reports `IsInstalled` and
`IsAuthenticated` separately, and lists missing credentials by **name**
(`google-oauth`, `STITCH_API_KEY`). A system that only checked "signed in?"
would report itself ready and then fail on every canvas call.

### Finding 3 — The OAuth flow is unusable from a server

`stitch login` requires a browser and a loopback listener. A background worker
has neither.

This is the origin of **assumption A-08**, the one open item that can change the
architecture: we have not confirmed that `STITCH_API_KEY` can be provisioned for
unattended server-side use. If canvas operations need OAuth _in addition_ for
some endpoints, generation would require per-user delegated tokens with consent
and refresh machinery — an architecture change, not a workaround.

---

## Error handling — verified

```
$ stitch get screen 12345 --project 99999 --json
{
  "success": false,
  "error": {
    "code": "MISSING_CANVAS_CREDENTIALS",
    "message": "MISSING_CANVAS_CREDENTIALS: Stitch Canvas requires authentication. Set STITCH_API_KEY or run 'stitch login'.",
    "recoverable": true,
    "remediation": "Set STITCH_API_KEY (environment or .env) to work with projects, screens, captures and design systems...",
    "isRecoverable": true,
    "suggestion": "..."
  }
}
$ echo $?
0
```

### Finding 4 — Exit code 0 on failure

**This is the single most important integration detail in the spike.** The
process exited 0 while reporting a failure. An adapter that branched on exit
code would treat every authentication failure as a success and then fail on a
null field somewhere downstream.

`StitchProcessCommandRunner.Parse` therefore reads success from the envelope's
`success` field. Held in place by
`StitchEnvelopeParsingTests.A_failure_envelope_is_detected_even_when_the_process_exits_zero`,
which uses this exact recorded output.

### Finding 5 — Two response shapes

| Shape                                     | Returned by                    | Handling         |
| :---------------------------------------- | :----------------------------- | :--------------- |
| `{ "success": bool, "data"\|"error": … }` | Mutating and resource commands | Read `success`   |
| Bare object                               | `status --json`, `--schema`    | Treat as success |

Both are parsed; `A_bare_object_response_is_treated_as_success` covers the
second. Non-JSON and empty stdout are mapped to `CLI_UNPARSEABLE_OUTPUT` and
`CLI_NO_OUTPUT` rather than throwing.

### Finding 6 — Error codes are a usable contract

`error.code` is stable and machine-readable, so retry classification keys off it
instead of message text. `MISSING_CANVAS_CREDENTIALS` and `PERMISSION_DENIED`
are non-retryable; `RESOURCE_EXHAUSTED` and `UNAVAILABLE` are retryable. A
credential error retried three times against a billable endpoint is wasted
attempts and wasted money.

---

## Dry-run — verified, and more useful than expected

```
$ stitch generate screen --new-project "Probe" --prompt "Login screen" \
    --device MOBILE --dry-run --json
{
  "success": true,
  "data": {
    "resource": "screen",
    "id": "screen-preview-dry-run",
    "project": "[will-create-new-project]",
    "device": "MOBILE",
    "canvasUrl": "https://stitch.google.com/projects/[will-create-new-project]",
    "dryRun": true,
    "requests": [
      { "kind": "tool", "tool": "create_project",
        "args": { "title": "Probe" } },
      { "kind": "tool", "tool": "generate_screen_from_text",
        "args": { "projectId": "[will-create-new-project]",
                  "prompt": "Login screen", "deviceType": "MOBILE" } }
    ]
  }
}
```

### Finding 7 — Request shaping is testable for free

`--dry-run` worked with **no credentials and no network**, and returned the
ordered provider operations. This means:

- `PlanScreenAsync` can validate and preview a request at zero cost, and is
  exposed as a user-facing step (S-11) rather than an internal detail.
- Request-shaping logic is covered in CI without an account and without spend.
- `Planning_is_allowed_when_billable_operations_are_disabled` asserts planning
  stays available while the cost guard is on.

---

## Theme constraints — verified from the provider's own schema

`stitch edit design-system --schema` returns JSON Schema Draft 2020-12.
Extracted:

**Required:** `bodyFont`, `colorMode`, `customColor`, `headlineFont`,
`roundness`.

| Field                                           | Type                            | Count                                           |
| :---------------------------------------------- | :------------------------------ | :---------------------------------------------- |
| `bodyFont`, `headlineFont`, `labelFont`         | enum                            | **67** named families                           |
| `colorMode`                                     | enum                            | 2 — `LIGHT`, `DARK` (**one value, not a pair**) |
| `colorVariant`                                  | enum                            | 9 tonal algorithms                              |
| `customColor`                                   | hex string                      | 1 seed                                          |
| `overridePrimaryColor` … `overrideNeutralColor` | hex                             | 4 role slots                                    |
| `roundness`                                     | enum                            | 5 — `ROUND_TWO/FOUR/EIGHT/TWELVE/FULL`          |
| `spacing`                                       | free-form string map            | unbounded                                       |
| `typography`                                    | free-form map of CSS properties | unbounded                                       |
| `designMd`                                      | markdown string                 | advisory only                                   |

### Finding 8 — The provider theme is strictly poorer than a design system

No elevation. No motion. No breakpoints. No component variants. No semantic
colour roles beyond four slots. One colour mode. Fonts restricted to a fixed
list.

**This finding is the mechanical justification for ADR-0002 and ADR-0004.** It
is not a philosophical preference that the Design Contract is the source of
truth — the provider's model simply cannot hold a real design system, so
something else must.

`StitchThemeProjector` implements the projection with one
`ProjectionWarning` per loss. The font count is asserted
(`The_verified_font_enum_matches_the_provider_schema_size`) so a provider change
fails the build instead of silently degrading output.

### Finding 9 — The schema is richer than the README

The README documents `--device` as `DESKTOP` or `MOBILE`. The schema's enum is
`MOBILE`, `DESKTOP`, `TABLET`, `AGNOSTIC`.

All four of our `TargetDevice` values map losslessly, verified by a theory test.
This is why the spike treats `--schema` output as authoritative over prose.

---

## Full command surface — verified present

| Group          | Commands                                                   |
| :------------- | :--------------------------------------------------------- |
| Resources      | `create`, `delete`, `edit`, `find`, `generate`, `get`      |
| Canvas actions | `capture`, `open`, `serve`, `upload`, `url`                |
| Auth / config  | `config`, `enable`, `disable`, `login`, `logout`, `status` |
| Advanced       | `agent-skills`, `mcp`, `api`                               |

Resource types: `project`, `screen`, `design-system` (`find`/`get`);
`screen`/`screens`/`variant`/`variants` (`generate`); `screen`/`design`
(`upload`).

Models: `GEMINI_3_8_FLASH`, `GEMINI_3_5_FLASH_LITE`.
Variants: count 1–5, `creative-range` ∈ {`REFINE`, `EXPLORE`, `REIMAGINE`},
aspects ∈ {`LAYOUT`, `COLOR_SCHEME`, `IMAGES`, `TEXT_FONT`, `TEXT_CONTENT`}.

### Finding 10 — Google's own guidance: capture, never generate, when importing

The package ships an agent skill (`skills/design/stitch/SKILL.md`) whose
operational rules include:

> **Capture rather than generate when importing code**: When turning an existing
> app into Stitch designs, capture real views with `stitch capture` and upload
> them with `stitch upload screen`. **Never call `stitch generate screen` during
> code import.**

This independently corroborates the redesign workflow's design: generation
invents UI the existing application does not have, which is exactly the
functional-loss failure mode ADR-0006 and ADR-0007 exist to prevent.

### Finding 11 — Loop is experimental and excluded

The Loop workspace API (priorities, insights, solutions) is off by default and
documented as liable to change "without a major version bump". Not adapted, not
in scope.

---

## End-to-end verification through our API

With the real CLI on PATH:

```
$ curl -s localhost:5080/api/v1/providers/google-stitch/readiness
{
  "providerId": "google-stitch",
  "isInstalled": true,
  "isAuthenticated": false,
  "isUsable": false,
  "detail": "Not signed in and no canvas credential.",
  "missingCredentialNames": ["google-oauth", "STITCH_API_KEY"]
}
```

Without it:

```
{ "isInstalled": false,
  "detail": "The stitch CLI is not installed or not on PATH. Install @google/stitch.",
  "missingCredentialNames": [] }
```

Both states are reported as **data with `200 OK`**, never as a server error: an
unconfigured provider is an expected condition.

---

## Security notes applied to the adapter

1. **Credentials are never process arguments.** `/proc/<pid>/cmdline` is
   world-readable. The runner passes credentials only via the inherited
   environment, and redacts `--api-key` / `--access-token` from logs.
2. **`STITCH_NO_BROWSER=1`** is forced, so no invocation can block waiting for a
   browser that does not exist.
3. **Timeout with process-tree kill**, so a hung CLI cannot hold a worker lease
   indefinitely.
4. **Output is untrusted.** It is parsed as JSON; the raw text is never
   evaluated.

---

## What this spike did not establish

- That a server-usable `STITCH_API_KEY` can be obtained (**A-08, M01-01**).
- Any live response shape. Everything beyond `--dry-run` and `status` is
  unverified — see [SPIKE-02](SPIKE-02-stitch-screen-retrieval.md).
- Cost or rate limits (A-09).

## Tests holding these findings

`backend/tests/AIDesignStudio.UnitTests/StitchAdapterTests.cs` (11 tests) and
`StitchThemeProjectionTests.cs` (15 tests). The CLI recordings quoted above are
embedded verbatim as test fixtures, so the adapter is tested against the
provider's real behaviour without needing it installed or authenticated.
