import { defineConfig } from "vitest/config";

// Logic only (uploads, queue, receipts, roles, translations) runs in Node; the screens are checked by tsc and the Metro bundle.
export default defineConfig({ test: { environment: "node", include: ["src/**/*.test.ts"] } });
