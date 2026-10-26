import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

// Testing Library registers its own cleanup only when Vitest globals are
// enabled. This project keeps `globals: false` so that test APIs are imported
// explicitly, which means unmounting has to be wired up here — otherwise each
// render accumulates in the document and queries match the previous test's DOM.
afterEach(cleanup);
