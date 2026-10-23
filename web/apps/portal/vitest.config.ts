import { defineConfig } from "vitest/config";

// Kept apart from vite.config.ts: vitest bundles its own Vite, so the React plugin is replaced by esbuild's automatic JSX.
export default defineConfig({
  esbuild: { jsx: "automatic" },
  test: { environment: "jsdom", globals: true, css: false, setupFiles: ["./src/test-setup.ts"] },
});
