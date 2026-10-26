<!-- BEGIN:nextjs-agent-rules -->

## This is NOT the Next.js you know

This version has breaking changes — APIs, conventions, and file structure may all differ from your training data. Read the relevant guide in `node_modules/next/dist/docs/` (resolved from this file's directory; in monorepos the `next` package may not be visible from the repo root) before writing any code. Heed deprecation notices.

This block is written and re-added by `next dev` — verify at `node_modules/next/dist/server/lib/generate-agent-files.js`. Removing it from a diff only re-creates the uncommitted change; committing it with your work keeps the tree clean.

<!-- END:nextjs-agent-rules -->

---

## Project rules

The block above is generated and rewritten by `next dev`. The binding rules for
this repository live at the root:

- [../AGENTS.md](../AGENTS.md) — the ten development rules
- [../CLAUDE.md](../CLAUDE.md) — the same rules with examples
- [../docs/04-engineering/CODING_STANDARDS.md](../docs/04-engineering/CODING_STANDARDS.md)

Frontend-specific notes:

- Vitest runs with `globals: false`, so test APIs are imported explicitly and
  Testing Library's cleanup is registered by hand in `vitest.setup.ts`.
- Dynamic data belongs inside `<Suspense>`. Cache Components rejects
  `export const dynamic`.
- Shared contract types come from `@aids/contracts`; the JSON Schemas there are
  normative.
