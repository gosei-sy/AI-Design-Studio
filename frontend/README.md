# frontend — AI Design Studio

Next.js studio application. Part of the AI Design Studio monorepo; run commands
from the repository root so npm workspaces resolve `@aids/contracts`.

See the [root README](../README.md) for local setup, and
[docs/03-design/SCREEN_INVENTORY.md](../docs/03-design/SCREEN_INVENTORY.md) for
the screens this app will contain.

## Commands

```bash
npm run dev:https --workspace frontend  # dev server → https://localhost:3000
npm run build --workspace frontend      # production build
npm test --workspace frontend           # Vitest: component + API client
npm run typecheck --workspace frontend
npm run lint --workspace frontend
npm run test:e2e --workspace frontend   # Playwright; needs the API running
```

The API base URL comes from `NEXT_PUBLIC_API_BASE_URL`, defaulting to
the studio's own origin, proxied server-side to the API. Anything with a `NEXT_PUBLIC_` prefix is visible in the
browser, so never put a secret there.

## Current state (M00)

One page: a read-only environment status panel reporting API health, provider
capabilities and missing credential names. No product UI exists yet — that
begins in M01 (see [IMPLEMENTATION_PLAN.md](../IMPLEMENTATION_PLAN.md)).

## Layout

```
src/app/            App Router pages
src/components/     UI components (shadcn/ui primitives under ui/)
src/lib/api.ts      typed API client
e2e/                Playwright specs — 7/7 passing locally; CI run outstanding
```
