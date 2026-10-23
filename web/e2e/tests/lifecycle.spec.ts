import { expect, test, type Browser, type Locator, type Page } from "@playwright/test";
import { messages } from "../../packages/ui/src/core/messages";
import { en, ur } from "../../apps/portal/src/strings";
import { png, seedWorld, signIn, type World } from "./support";

type Strings = Record<keyof typeof en, string>;

const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const starts = (s: string) => new RegExp(`^${esc(s)}`);
const exact = (s: string) => ({ name: s, exact: true });

/** The story from the task list: complaint, estimate, assign, work, actual costs, verify, resident confirms and closes. */
async function lifecycle(browser: Browser, world: World, locale: "en" | "ur") {
  const t: Strings = locale === "ur" ? ur : en;
  const ui = messages[locale];
  const labels = { email: starts(t.email), password: starts(t.password), signIn: new RegExp(`^${esc(t["login.signIn"])}$`), code: starts(t["login.code"]), confirm: new RegExp(esc(t["login.confirm"])) };
  const login = (account: World["resident"]) => signIn(browser, account, labels, locale);

  const waterLabel = locale === "ur" ? "پانی" : "Water"; // matches the seeded "Water supply" category in either language
  const pipeOption = `${world.pipeName} (Rs 320.50 / metre)`;
  const menu = (page: Page, label: string) => page.getByRole("navigation", { name: ui["nav.primary"] }).getByRole("link", { name: starts(label) });
  const card = (page: Page, title: string): Locator => page.locator(".uf-card", { has: page.getByRole("heading", { level: 2, ...exact(title) }) });
  const chip = (page: Page, label: string) => page.locator(".uf-chip", { hasText: label }).first();

  // 1. The resident reports a problem ---------------------------------------------------------------------------------------
  const resident = await login(world.resident);
  await menu(resident, t["menu.new"]).click();
  await resident.getByLabel(starts(t.description)).fill("No water since the morning, the main pipe outside the gate is leaking");
  await resident.getByRole("button", exact(t["new.submit"])).click();
  const created = await resident.locator(".uf-alert--success").innerText();
  const reference = created.match(/UF-\d{4}-\d{5}/)![0];

  // 2. The supervisor reviews it; routing rules pick the water crew -------------------------------------------------------------
  const supervisor = await login(world.supervisor);
  await menu(supervisor, t["menu.review"]).click();
  const row = supervisor.locator(".queue li", { hasText: reference });
  await expect(row).toBeVisible();
  const category = row.getByLabel(starts(t.category));
  const water = await category.locator("option", { hasText: waterLabel }).first().getAttribute("value");
  await category.selectOption(water!);
  await row.getByRole("button", exact(t["review.acceptAssign"])).click();
  await expect(supervisor.locator(".queue li", { hasText: reference })).toHaveCount(0);

  // 3. They assign the technician and estimate the job before work starts ---------------------------------------------------------
  await menu(supervisor, t["menu.jobs"]).click();
  await supervisor.getByRole("link", { name: /^J-\d{4}-\d+/ }).first().click();
  const assign = card(supervisor, t["job.assign"]);
  await expect(assign.getByLabel(starts(t.team))).toHaveValue(/.+/); // the rule's team is already chosen
  await assign.getByLabel(starts(t["job.technician"])).selectOption({ label: "Tariq Technician" });
  await assign.getByRole("button", exact(t["job.assign"])).click();
  await expect(supervisor.locator(".timeline")).toContainText("Tariq Technician");

  const estimate = card(supervisor, t["job.estimate"]);
  const e1 = estimate.locator("fieldset").nth(0);
  await e1.getByLabel(starts(t["cost.item"])).selectOption({ label: pipeOption });
  await e1.getByLabel(starts(t["cost.quantity"])).fill("2.5");
  await estimate.getByRole("button", exact(t["cost.add"])).click();
  const e2 = estimate.locator("fieldset").nth(1);
  await e2.getByLabel(starts(t["cost.kind"])).selectOption("labour");
  await e2.getByLabel(starts(t.description)).fill("Plumber");
  await e2.getByLabel(starts(t["cost.rate"])).fill("400");
  await e2.getByLabel(starts(t["cost.quantity"])).fill("3");
  await expect(estimate.locator(".cost-total")).toContainText("2,001.25"); // 2.5 x 320.50 + 3 x 400
  await estimate.getByRole("button", exact(t["job.saveEstimate"])).click();
  await expect(estimate.locator(".uf-alert--success")).toBeVisible();

  // 4. The technician does the work, with evidence and actual costs ---------------------------------------------------------------
  const tech = await login(world.technician);
  await expect(tech).toHaveURL(/\/jobs$/);
  await tech.getByRole("link", { name: /^J-\d{4}-\d+/ }).first().click();
  await tech.getByRole("button", exact(t["job.accept"])).click();
  await tech.getByRole("button", exact(t["job.start"])).click();
  await expect(chip(tech, t["jobStatus.InProgress"])).toBeVisible();

  // completing is refused until the evidence and costs exist
  await tech.getByRole("button", exact(t["job.complete"])).click();
  await expect(tech.locator(".uf-alert--warning")).toContainText(t["missing.before_photo"]);

  const evidence = card(tech, t["job.evidence"]);
  for (const [stage, seed] of [["before", 1], ["after", 2]] as const) {
    await evidence.getByLabel(starts(t["job.stage"])).selectOption(stage);
    await evidence.locator("input[type=file]").setInputFiles({ name: `${stage}.png`, mimeType: "image/png", buffer: png(seed) });
    await expect(evidence.locator(`section[aria-label="${t[`stage.${stage}`]}"] img`)).toHaveCount(1);
  }

  const actuals = card(tech, t["job.actuals"]);
  const a1 = actuals.locator("fieldset").nth(0);
  await a1.getByLabel(starts(t["cost.item"])).selectOption({ label: pipeOption });
  await a1.getByLabel(starts(t["cost.quantity"])).fill("3");
  await actuals.getByRole("button", exact(t["cost.add"])).click();
  const a2 = actuals.locator("fieldset").nth(1);
  await a2.getByLabel(starts(t["cost.kind"])).selectOption("labour");
  await a2.getByLabel(starts(t.description)).fill("Plumber");
  await a2.getByLabel(starts(t["cost.rate"])).fill("450");
  await a2.getByLabel(starts(t["cost.quantity"])).fill("2");
  await expect(actuals.locator(".cost-total")).toContainText("1,861.50"); // 3 x 320.50 + 2 x 450
  await actuals.getByRole("button", exact(t["job.saveActuals"])).click();
  await expect(actuals.locator(".uf-alert--success")).toBeVisible();

  await tech.getByLabel(starts(t["job.note"])).fill("Replaced the leaking section and tested the pressure");
  await tech.getByRole("button", exact(t["job.complete"])).click();
  await expect(chip(tech, t["jobStatus.PendingVerification"])).toBeVisible();

  // 5. The supervisor verifies: evidence side by side, estimate against actual, sign-off ------------------------------------------
  await supervisor.reload();
  await expect(chip(supervisor, t["jobStatus.PendingVerification"])).toBeVisible();
  await expect(supervisor.locator(".before-after img")).toHaveCount(2);
  const variance = supervisor.locator(`dl[aria-label="${t["job.variance"]}"]`);
  await expect(variance).toContainText("2,001.25");
  await expect(variance).toContainText("1,861.50");
  await expect(variance).not.toContainText(t["job.overrun"]); // came in under the estimate
  await supervisor.getByRole("button", exact(t["job.signOff"])).click();
  await expect(chip(supervisor, t["jobStatus.Completed"])).toBeVisible();

  // 6. The resident was asked to confirm, and closes the complaint ---------------------------------------------------------------
  await resident.reload();
  await resident.getByRole("button", { name: t["notifications.title"] }).click();
  await expect(resident.getByRole("dialog")).toContainText(reference); // receipt, assignment and confirmation request
  await resident.getByRole("dialog").getByRole("button", { name: ui["common.close"] }).click();

  await menu(resident, t["menu.complaints"]).click();
  await resident.getByRole("link", exact(reference)).click();
  await expect(chip(resident, t["complaintStatus.Resolved"])).toBeVisible();
  await resident.getByRole("button", exact(t["move.Closed"])).click();
  await resident.getByRole("dialog").getByRole("button", exact(t.confirm)).click();
  await expect(chip(resident, t["complaintStatus.Closed"])).toBeVisible();

  // 7. Every change is in the audit log for the manager ---------------------------------------------------------------------------
  const manager = await login(world.manager);
  await menu(manager, t["menu.audit"]).click();
  await manager.getByLabel(starts(t["audit.entityType"])).fill("Complaint");
  await expect(manager.getByRole("table").getByRole("row").nth(1)).toBeVisible();
  await expect(manager.getByRole("table")).toContainText("Complaint");
  await expect(manager.getByRole("table")).toContainText("Sana Supervisor");

  await Promise.all([resident, supervisor, tech, manager].map((p) => p.context().close()));
}

test.describe("complaint to closure", () => {
  test("in English", async ({ browser }) => {
    await lifecycle(browser, await seedWorld(), "en");
  });

  test("in Urdu, laid out right to left", async ({ browser }) => {
    const world = await seedWorld();
    await lifecycle(browser, world, "ur");
  });
});
