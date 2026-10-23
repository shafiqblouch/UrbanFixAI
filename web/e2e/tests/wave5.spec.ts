import { expect, test, type Browser, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { en } from "../../apps/portal/src/strings";
import { adminAccount, apiLogin, API, PASSWORD, seedWorld, signIn, type World } from "./support";

const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const starts = (s: string) => new RegExp(`^${esc(s)}`);
const exact = (s: string) => ({ name: s, exact: true });
const t = en;
const labels = { email: starts(t.email), password: starts(t.password), signIn: new RegExp(`^${esc(t["login.signIn"])}$`), code: starts(t["login.code"]), confirm: new RegExp(esc(t["login.confirm"])) };
const shots = join(import.meta.dirname, "..", "test-results", "wave5");
mkdirSync(shots, { recursive: true });

const menu = (page: Page, label: string) => page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: starts(label) });
const shot = (page: Page, name: string) => page.screenshot({ path: join(shots, `${name}.png`), fullPage: true });

async function api<T>(token: string, method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${API}${path}`, { method, headers: { "content-type": "application/json", Authorization: `Bearer ${token}` }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${text}`);
  return (text ? JSON.parse(text) : undefined) as T;
}

/** The screens built in Wave 5, driven in a real browser against the real API. Screenshots go to test-results/wave5. */
test.describe.configure({ mode: "serial" });

let world: World;
test.beforeAll(async () => { world = await seedWorld(); });

async function manager(browser: Browser) { return signIn(browser, world.manager, labels); }

test("a notice reaches the resident, and the sender sees it was read", async ({ browser }) => {
  const mgr = await manager(browser);
  await menu(mgr, t["menu.notices"]).click();
  await mgr.getByRole("button", exact(t["notices.new"])).click();
  await mgr.getByLabel(/^Title(?! in Urdu)/).fill("Water tank cleaning");
  await mgr.getByLabel(/^Message(?! in Urdu)/).fill("The tank is cleaned on Friday morning. Please store water.");
  await expect(mgr.getByText(/This reaches \d+ people today/)).toBeVisible();
  await mgr.getByRole("button", exact(t["notices.send"])).click();
  const row = mgr.locator("tr", { hasText: "Water tank cleaning" });
  await expect(row).toBeVisible();
  await shot(mgr, "notices-sender");

  const res = await signIn(browser, world.resident, labels);
  await menu(res, t["menu.notices"]).click();
  await expect(res.getByText("The tank is cleaned on Friday morning")).toBeVisible();
  await res.getByText("The tank is cleaned on Friday morning").click();
  await shot(res, "notices-resident");

  await mgr.reload();
  await mgr.getByRole("button", exact(t["notices.details"])).click();
  await expect(mgr.getByRole("dialog").getByText(/Rabia Resident/)).toBeVisible();
  await expect(mgr.getByRole("dialog").getByRole("cell", { name: t["notices.readers.read"], exact: true })).toBeVisible();
  await shot(mgr, "notices-readers");
});

test("the manager's dashboard shows widgets, the trend chart, and the chart can be read as a table", async ({ browser }) => {
  const resident = await apiLogin(world.resident.email, PASSWORD, world.resident.secret);
  const cats = await api<{ id: string; key: string }[]>((await apiLogin(world.manager.email, PASSWORD, world.manager.secret)).token, "GET", "/categories");
  const water = cats.find((c) => c.key === "water")!;
  for (const text of ["No water in street 4", "Streetlight is out near the park", "Rubbish not collected"])
    await api(resident.token, "POST", "/complaints", { description: text, channel: null, categoryId: water.id, houseId: null, locationId: null, locationText: null, reportedByUserId: null });

  const mgr = await manager(browser);
  await menu(mgr, t["menu.dashboard"]).click();
  await expect(mgr.getByRole("heading", { name: t["dashboard.trends"] })).toBeVisible();
  const chart = mgr.getByRole("img", { name: /reported and \d+ resolved/ });
  await expect(chart).toBeVisible();
  await chart.hover({ position: { x: 300, y: 100 } });
  await shot(mgr, "dashboard");
  await mgr.getByRole("button", exact(t["trends.showTable"])).click();
  await expect(mgr.getByRole("table", { name: t["trends.title"] })).toBeVisible();
  await shot(mgr, "dashboard-table");
});

test("reports and exports download real files", async ({ browser }) => {
  const mgr = await manager(browser);
  await menu(mgr, t["menu.complaints"]).click();
  const [csv] = await Promise.all([mgr.waitForEvent("download"), mgr.getByRole("button", exact(t["complaints.exportCsv"])).click()]);
  const csvPath = join(shots, "complaints.csv");
  await csv.saveAs(csvPath);
  const text = readFileSync(csvPath, "utf8");
  expect(text).toContain("Reference,Status,Priority,Category");
  expect(text).toContain("No water in street 4");

  const [pdf] = await Promise.all([mgr.waitForEvent("download"), mgr.getByRole("button", exact(t["complaints.exportPdf"])).click()]);
  const pdfPath = join(shots, "complaints.pdf");
  await pdf.saveAs(pdfPath);
  expect(readFileSync(pdfPath).subarray(0, 5).toString()).toBe("%PDF-");

  await menu(mgr, t["menu.reports"]).click();
  await expect(mgr.getByRole("tab", exact(t["reports.costs"]))).toBeVisible();
  await shot(mgr, "reports-costs");
  await mgr.getByRole("tab", exact(t["reports.summary"])).click();
  const [summary] = await Promise.all([mgr.waitForEvent("download"), mgr.getByRole("button", exact(t["summary.pdf"])).click()]);
  const summaryPath = join(shots, "summary.pdf");
  await summary.saveAs(summaryPath);
  expect(readFileSync(summaryPath).subarray(0, 5).toString()).toBe("%PDF-");
  await shot(mgr, "reports-summary");
});

test("the directory imports residents from a file, checking before it saves", async ({ browser }) => {
  const mgr = await manager(browser);
  await menu(mgr, t["menu.directory"]).click();
  await mgr.getByRole("tab", exact(t["directory.import"])).click();
  await mgr.getByLabel(starts(t["directory.importKind"])).selectOption("vehicles");
  const csvFile = join(shots, "vehicles.csv");
  writeFileSync(csvFile, "plate,type,house\nLEB-1234,car,Block A/House 7\n,car,\nABC-999,rickshaw,Block A/Nowhere\n");
  await mgr.getByLabel(t["directory.csvFile"]).setInputFiles(csvFile);
  await mgr.getByRole("button", exact(t["directory.check"])).click();
  await expect(mgr.getByText(t["directory.reportCheck"])).toBeVisible();
  await shot(mgr, "directory-check");
  await mgr.getByRole("button", exact(t["directory.importNow"])).click();
  await expect(mgr.getByText(t["directory.reportDone"])).toBeVisible();

  await mgr.getByRole("tab", exact(t["directory.vehicles"])).click();
  await expect(mgr.getByRole("cell", { name: "LEB-1234" })).toBeVisible();
  await shot(mgr, "directory-vehicles");
});

test("limits set in configuration are kept", async ({ browser }) => {
  const mgr = await manager(browser);
  await menu(mgr, t["menu.config"]).click();
  const limit = mgr.getByLabel(starts(t["policy.estimateApprovalLimit"]));
  await limit.fill("15000");
  await limit.locator("xpath=ancestor::form").getByRole("button", exact(t.save)).click();
  await expect(limit.locator("xpath=ancestor::form").getByText(t.saved)).toBeVisible();
  await mgr.reload();
  await expect(mgr.getByLabel(starts(t["policy.estimateApprovalLimit"]))).toHaveValue("15000");
  await shot(mgr, "config-society");

  await mgr.getByRole("tab", exact(t["config.autoRoute"])).click();
  await shot(mgr, "config-autoroute");
});

test("the platform team looks into a society read-only, and the society can see it", async ({ browser }) => {
  const admin = await signIn(browser, await adminAccount(), labels);
  await menu(admin, t["menu.platform"]).click();
  const row = admin.locator("tr", { hasText: `E2E Society` }).filter({ hasText: world.slug.replace("e2e-", "") });
  await row.getByRole("button", exact(t["platform.manage"])).click();
  await expect(admin.getByText(t["platform.onboarding"])).toBeVisible();
  await shot(admin, "platform-organization");
  await admin.getByLabel(starts(t["platform.supportReason"])).fill("Resident cannot see the notice about the tank");
  await admin.getByRole("button", exact(t["platform.supportStart"])).click();
  await expect(admin.getByText(/Support session for E2E Society/)).toBeVisible();
  await menu(admin, t["menu.complaints"]).click();
  await expect(admin.getByText("No water in street 4")).toBeVisible();
  await expect(admin.getByRole("button", exact(t["complaints.exportCsv"]))).toBeVisible();
  await shot(admin, "support-session");
  await admin.getByRole("button", exact(t["support.leave"])).click();
  await expect(menu(admin, t["menu.platform"])).toBeVisible();

  // The society's manager reads what the platform person did.
  const trail = await api<{ action: string }[]>((await apiLogin(world.manager.email, PASSWORD, world.manager.secret)).token, "GET", "/audit?entityType=SupportSession&order=asc");
  expect(trail.map((e) => e.action)).toEqual(expect.arrayContaining(["support_session_started", "support_viewed", "support_session_ended"]));
});
