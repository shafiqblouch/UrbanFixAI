import { defineConfig } from "@playwright/test";

const API = "http://localhost:5181";
const PORTAL = "http://localhost:4173";

/**
 * End-to-end tests against the real stack: the core API (needs the dev PostgreSQL and MinIO from infra/docker-compose.yml) and the
 * built portal. Browser: the installed Edge by default (PW_CHANNEL=chrome or chromium to change; some managed machines block Chrome), so nothing needs downloading.
 */
export default defineConfig({
  testDir: "./tests",
  timeout: 120_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: PORTAL,
    channel: process.env.PW_CHANNEL ?? "msedge",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: [
    {
      command: "dotnet run --project ../../core/src/UrbanFix.Api --no-launch-profile --urls " + API,
      url: `${API}/health/ready`,
      timeout: 180_000,
      reuseExistingServer: !process.env.CI,
      env: { ASPNETCORE_ENVIRONMENT: "Development", Ai__Enabled: "false" },
    },
    {
      command: "pnpm --filter @urbanfix/portal build && pnpm --filter @urbanfix/portal exec vite preview --port 4173 --strictPort",
      cwd: "..",
      url: PORTAL,
      timeout: 180_000,
      reuseExistingServer: !process.env.CI,
      env: { VITE_API_URL: API },
    },
  ],
  metadata: { api: API },
});
