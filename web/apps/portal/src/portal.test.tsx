import { LocaleProvider } from "@urbanfix/ui";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { lineAmount, toInput, total, type Draft } from "./CostEditor";
import { homeFor, menusByRole, menusFor, type Role } from "./roles";
import { en, ur } from "./strings";
import { catalog } from "./i18n";

// A stand-in for the API. openapi-fetch captures global fetch when the client is created, so stub it before the app loads.
type Handler = (url: URL, init?: RequestInit) => { status?: number; body?: unknown; headers?: Record<string, string> } | undefined;
let handler: Handler = () => undefined;
const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
  const request = input instanceof Request ? input : undefined;
  const url = new URL(request?.url ?? String(input), "http://localhost");
  const reply = handler(url, init ?? (request ? { method: request.method, body: await request.clone().text() } : undefined)) ?? { status: 404, body: { error: "not_found" } };
  return new Response(JSON.stringify(reply.body ?? {}), { status: reply.status ?? 200, headers: { "content-type": "application/json", ...reply.headers } });
});
vi.stubGlobal("fetch", fetchMock);

describe("role menus (WP-01)", () => {
  it("gives every role exactly the screens it should see", () => {
    expect(menusFor("Manager")).toEqual(["dashboard", "complaints", "review", "new", "directory", "jobs", "reports", "stock", "approvals", "notices", "config", "audit"]);
    expect(menusFor("Supervisor")).toEqual(["dashboard", "complaints", "review", "new", "jobs", "reports", "stock"]);
    expect(menusFor("Office")).toEqual(["dashboard", "complaints", "review", "new", "directory"]);
    expect(menusFor("Committee")).toEqual(["dashboard", "complaints", "reports", "approvals", "notices", "audit"]);
    expect(menusFor("Technician")).toEqual(["jobs"]);
    expect(menusFor("Resident")).toEqual(["complaints", "new", "notices"]);
    expect(menusFor(undefined)).toEqual([]);
  });

  it("only managers configure, only managers and the committee read the audit log", () => {
    const roles = Object.keys(menusByRole) as Role[];
    expect(roles.filter((r) => menusFor(r).includes("config"))).toEqual(["Manager"]);
    expect(roles.filter((r) => menusFor(r).includes("audit")).sort()).toEqual(["Committee", "Manager", "Support"]);
  });

  it("starts each role on its first screen, or the welcome page when it has none", () => {
    expect(homeFor("Technician")).toBe("/jobs");
    expect(homeFor("Office")).toBe("/dashboard");
    expect(homeFor("Guard")).toBe("/welcome");
    expect(homeFor(undefined)).toBe("/welcome");
  });
});

describe("translations", () => {
  const placeholders = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

  it("has every English string in Urdu, with the same placeholders", () => {
    for (const key of Object.keys(en) as (keyof typeof en)[]) {
      expect(ur[key], key).toBeTruthy();
      expect(placeholders(ur[key]), key).toEqual(placeholders(en[key]));
    }
    expect(Object.keys(ur).length).toBe(Object.keys(en).length);
  });

  it("writes the Urdu in Urdu script, except for names and codes that stay Latin", () => {
    const latinOk = new Set(["email", "app.name"]);
    for (const key of Object.keys(ur) as (keyof typeof ur)[]) {
      if (latinOk.has(key)) continue;
      expect(ur[key], key).toMatch(/[؀-ۿ]/);
    }
  });
});

describe("cost lines (WP-08)", () => {
  const items = [{ id: "pipe", name: "PVC pipe", nameUr: null, unit: "metre", isActive: true, currentRate: { id: "r", rate: 320.5, effectiveFrom: "", createdAt: "" } }];
  const draft = (patch: Partial<Draft>): Draft => ({ kind: "labour", priceItemId: "", description: "Plumber", quantity: "1", unit: "", unitRate: "0", ...patch });

  it("prices materials from the list and other lines from the typed rate", () => {
    expect(lineAmount(draft({ kind: "material", priceItemId: "pipe", quantity: "2.5" }), items)).toBe(801.25);
    expect(lineAmount(draft({ quantity: "3", unitRate: "400" }), items)).toBe(1200);
    expect(lineAmount(draft({ quantity: "", unitRate: "400" }), items)).toBe(0);
  });

  it("totals to two decimal places", () => {
    const lines = [draft({ quantity: "1.5", unitRate: "6.675" }), draft({ quantity: "2", unitRate: "0.1" })];
    expect(total(lines, items)).toBe(10.21);
  });

  it("sends the list rate by id and never a typed rate for list items", () => {
    expect(toInput(draft({ kind: "material", priceItemId: "pipe", unitRate: "1" }))).toMatchObject({ priceItemId: "pipe", unitRate: null });
    expect(toInput(draft({ unitRate: "400", description: "Plumber" }))).toMatchObject({ priceItemId: null, unitRate: 400, description: "Plumber" });
  });
});

describe("app", () => {
  beforeEach(() => {
    sessionStorage.clear();
    window.history.pushState({}, "", "/");
    handler = () => undefined;
    fetchMock.mockClear();
    vi.resetModules();
  });

  const me = (role: string) => ({
    userId: "u1", email: "a@x.test", displayName: "Ayesha", organizationId: "o1",
    memberships: [{ id: "m1", organizationId: "o1", organizationName: "Green Society", role, departmentId: null, gateId: null }],
  });

  const empty: Handler = (url) => {
    if (url.pathname.endsWith("/notifications")) return { body: { unread: 0, items: [] } };
    if (url.pathname.endsWith("/review-queue") || url.pathname.endsWith("/jobs") || url.pathname.endsWith("/directory")) return { body: [] };
    if (url.pathname.includes("/hubs/")) return { status: 404 };
    if (/\/(categories|departments|teams|locations\/tree|complaints)$/.test(url.pathname)) return { body: [], headers: { "X-Total-Count": "0" } };
    return undefined;
  };

  async function mount(role: string) {
    sessionStorage.setItem("uf.token", "tok");
    handler = (url, init) => (url.pathname === "/api/me" ? { body: me(role) } : empty(url, init));
    const { App } = await import("./App");
    render(<App />);
  }

  it.each([
    ["Manager", ["Dashboard", "Complaints", "Review queue", "Directory", "Jobs", "Reports", "Store", "Approvals", "Notices", "Configuration", "Audit log"]],
    ["Supervisor", ["Dashboard", "Complaints", "Review queue", "Jobs", "Reports", "Store"]],
    ["Office", ["Dashboard", "Complaints", "Review queue", "Directory"]],
    ["Technician", ["My jobs"]],
  ])("shows a %s only their menus", async (role, labels) => {
    await mount(role);
    const nav = await screen.findByRole("navigation", { name: "Main navigation" });
    expect(within(nav).getAllByRole("link").map((l) => l.textContent?.replace(/\d+$/, "").trim())).toEqual(labels);
  });

  it("sends a signed-in user to their own first screen and away from screens their role does not have", async () => {
    await mount("Office");
    await waitFor(() => expect(window.location.pathname).toBe("/dashboard"));
    window.history.pushState({}, "", "/config");
    window.dispatchEvent(new PopStateEvent("popstate"));
    await waitFor(() => expect(window.location.pathname).toBe("/dashboard"));
  });

  it("lets a manager write a notice and see how far it got (WP-19)", async () => {
    const sent: unknown[] = [];
    window.history.pushState({}, "", "/notices");
    sessionStorage.setItem("uf.token", "tok");
    const notice = { id: "n1", title: "Water cut", body: "Friday", titleUr: null, bodyUr: null, audienceKind: "owners", locationId: null, status: "Sent", scheduledAt: null,
      sentAt: "2026-10-23T08:00:00Z", createdAt: "2026-10-23T08:00:00Z", recipients: 10, delivered: 9, read: 4, failed: 1 };
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Manager") };
      if (url.pathname === "/api/notices" && (init?.method ?? "GET") === "GET") return { body: [notice] };
      if (url.pathname === "/api/notices" && init?.method === "POST") { sent.push(JSON.parse(String(init.body))); return { status: 201, body: notice }; }
      if (url.pathname === "/api/notices/audience") return { body: { recipients: 7 } };
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    const row = (await screen.findByText("Water cut")).closest("tr")!;
    expect(within(row).getByText("10")).toBeTruthy();
    expect(within(row).getByText("4")).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "New notice" }));
    await user.type(await screen.findByLabelText(/^Title(?! in Urdu)/), "Tank cleaning");
    await user.type(screen.getByLabelText(/^Message(?! in Urdu)/), "Saturday morning");
    expect(await screen.findByText("This reaches 7 people today.")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Send notice" }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ title: "Tank cleaning", body: "Saturday morning", audienceKind: "all", locationId: null, scheduledAt: null, titleUr: null, bodyUr: null });
  });

  it("shows a resident the notices sent to them and marks one read when opened (RA-09)", async () => {
    const read: string[] = [];
    window.history.pushState({}, "", "/notices");
    sessionStorage.setItem("uf.token", "tok");
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Resident") };
      if (url.pathname === "/api/me/notices") return { body: [{ id: "k1", noticeId: "n1", title: "Water cut", body: "Friday morning", createdAt: "2026-10-23T08:00:00Z", readAt: null }] };
      if (url.pathname === "/api/notifications/k1/read") { read.push("k1"); return { body: {} }; }
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();
    expect(screen.queryByRole("button", { name: "New notice" })).toBeNull();
    await user.click(await screen.findByText("Friday morning"));
    await waitFor(() => expect(read).toEqual(["k1"]));
  });

  it("lets a manager approve an estimate in one click and refuses to reject without a reason (ID-10)", async () => {
    const answers: unknown[] = [];
    window.history.pushState({}, "", "/approvals");
    sessionStorage.setItem("uf.token", "tok");
    const approval = { id: "a1", kind: "estimate", jobId: "j1", jobNumber: "J-7", reference: "UF-12", estimateVersion: 2, amount: 12500, requestedBy: "u9",
      requestedAt: "2026-10-23T08:00:00Z", expiresAt: "2026-10-26T08:00:00Z", approvalsNeeded: 2, status: "Pending", note: null,
      decisions: [{ userId: "u3", approve: true, note: null, decidedAt: "2026-10-23T09:00:00Z" }] };
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Manager") };
      if (url.pathname === "/api/approvals") return { body: [approval] };
      if (url.pathname === "/api/approvals/a1/decision") { answers.push(JSON.parse(String(init?.body))); return { body: approval }; }
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    expect(await screen.findByText("Estimate for job J-7")).toBeTruthy();
    expect(screen.getByText("1 of 2 approvals so far")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Approve" }));
    await waitFor(() => expect(answers).toEqual([{ approve: true, note: null }]));

    await user.click(screen.getByRole("button", { name: "Do not approve" }));
    const confirm = await screen.findByRole("button", { name: "Confirm" });
    expect((confirm as HTMLButtonElement).disabled).toBe(true); // a "no" needs a reason
    await user.type(screen.getByLabelText(/^Reason/), "Too high");
    await user.click(confirm);
    await waitFor(() => expect(answers[1]).toEqual({ approve: false, note: "Too high" }));
  });

  it("lets the storekeeper receive stock and issue only the requests that stock can cover (JC-06, TA-09)", async () => {
    const calls: string[] = [];
    const bodies: unknown[] = [];
    window.history.pushState({}, "", "/stock");
    sessionStorage.setItem("uf.token", "tok");
    const request = (id: string, onHand: number) => ({ id, jobId: "j1", jobNumber: `J-${id}`, requestedBy: "u9", status: "Open", note: null, createdAt: "2026-10-23T08:00:00Z", decisionNote: null,
      lines: [{ itemId: "i1", item: "Pipe", unit: "m", quantity: 5, onHand }] });
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Supervisor") };
      if (url.pathname === "/api/stock") return { body: [{ itemId: "i1", item: "Pipe", unit: "m", onHand: 3, lowThreshold: 5, low: true }] };
      if (url.pathname === "/api/stock/requests") return { body: [request("ok", 9), request("short", 3)] };
      if (url.pathname === "/api/stock/receipts") { bodies.push(JSON.parse(String(init?.body))); return { status: 201, body: {} }; }
      if (url.pathname.endsWith("/fulfil")) { calls.push(url.pathname); return { body: request("ok", 9) }; }
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    const row = (await screen.findByText("Pipe")).closest("tr")!;
    expect(within(row).getByText("Low")).toBeTruthy();
    await user.click(within(row).getByRole("button", { name: "Receive" }));
    await user.type(await screen.findByLabelText(/^Quantity/), "20");
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(bodies).toEqual([{ itemId: "i1", quantity: 20, reason: null }]));

    await user.click(screen.getByRole("tab", { name: "Material requests" }));
    const issue = await screen.findAllByRole("button", { name: "Issue everything asked for" });
    expect(issue).toHaveLength(2);
    expect((issue[1] as HTMLButtonElement).disabled).toBe(true); // 5 asked, 3 on hand
    await user.click(issue[0]);
    await waitFor(() => expect(calls).toEqual(["/api/stock/requests/ok/fulfil"]));
  });

  it("lets accounts approve, change or reject a repair charge, and a change needs a reason (JC-11)", async () => {
    const decisions: unknown[] = [];
    window.history.pushState({}, "", "/charges");
    sessionStorage.setItem("uf.token", "tok");
    const charge = { id: "c1", jobId: "j1", jobNumber: "J-3", reference: "UF-4", description: "Broken gate", houseId: "h1", houseLabel: "Block A › House 7",
      residents: [{ userId: "u1", name: "Ali", relation: "owner" }], proposedAmount: 4200, finalAmount: null, status: "Pending", readyToPost: false, note: null,
      createdAt: "2026-10-23T08:00:00Z", decidedAt: null, costs: [{ kind: "material", description: "Hinge", quantity: 2, amount: 1200 }], evidence: [] };
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Accounts") };
      if (url.pathname === "/api/charges") return { body: [charge] };
      if (url.pathname === "/api/charges/c1/decision") { decisions.push(JSON.parse(String(init?.body))); return { body: charge }; }
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    expect(await screen.findByText("Block A › House 7")).toBeTruthy();
    expect(screen.getByText(/Ali \(owner\)/)).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Change the amount" }));
    const confirm = await screen.findByRole("button", { name: "Confirm" });
    await user.clear(screen.getByLabelText(/^Amount to charge/));
    await user.type(screen.getByLabelText(/^Amount to charge/), "3000");
    expect((confirm as HTMLButtonElement).disabled).toBe(true); // a change needs a reason
    await user.type(screen.getByLabelText(/^Reason/), "Labour waived");
    await user.click(confirm);
    await waitFor(() => expect(decisions).toEqual([{ action: "adjust", amount: 3000, note: "Labour waived" }]));
    await user.click(screen.getByRole("button", { name: "Approve as proposed" }));
    await waitFor(() => expect(decisions[1]).toEqual({ action: "approve", amount: null, note: null }));
  });

  it("shows the cost report grouped the way the user chooses (JC-10)", async () => {
    const asked: string[] = [];
    window.history.pushState({}, "", "/reports");
    sessionStorage.setItem("uf.token", "tok");
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Manager") };
      if (url.pathname === "/api/reports/costs") {
        asked.push(url.searchParams.get("groupBy") ?? "");
        return { body: { groupBy: "category", from: null, to: null, jobs: 3, estimated: 1000, actual: 1500, rows: [{ key: "k1", name: "Water", jobs: 3, estimated: 1000, actual: 1500, variance: 500 }] } };
      }
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    const row = (await screen.findByText("Water")).closest("tr")!;
    expect(within(row).getByText("Rs 500.00")).toBeTruthy();
    await user.selectOptions(screen.getByLabelText(/^Group by/), "street");
    await waitFor(() => expect(asked).toEqual(["category", "street"]));
  });

  it("checks a bulk file without saving, shows problems by line, and imports only after the check (OR-10)", async () => {
    const calls: string[] = [];
    window.history.pushState({}, "", "/directory");
    sessionStorage.setItem("uf.token", "tok");
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Manager") };
      if (url.pathname === "/api/directory/import") {
        const dryRun = url.searchParams.get("dryRun") === "true";
        calls.push(`${url.searchParams.get("kind")}:${dryRun ? "check" : "import"}`);
        return { body: { kind: "residents", dryRun, rows: 3, created: dryRun ? 2 : 2, updated: 0, unchanged: 0, issues: [{ line: 3, field: "phone", message: "This does not look like a phone number" }] } };
      }
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("tab", { name: "Import" }));
    const importNow = screen.getByRole("button", { name: "Import the good rows" }) as HTMLButtonElement;
    expect(importNow.disabled).toBe(true);
    const file = new File(["house,name,phone,relation"], "people.csv", { type: "text/csv" });
    await user.upload(screen.getByLabelText("CSV file"), file);
    await user.click(screen.getByRole("button", { name: "Check without saving" }));
    expect(await screen.findByText("This does not look like a phone number")).toBeTruthy();
    expect(screen.getByText("3")).toBeTruthy(); // the line number
    await waitFor(() => expect(importNow.disabled).toBe(false));
    await user.click(importNow);
    await waitFor(() => expect(calls).toEqual(["residents:check", "residents:import"]));
  });

  it("saves the society's limits and only offers automatic routing where the evidence is there (OR-12, AI-17)", async () => {
    const saved: unknown[] = [];
    window.history.pushState({}, "", "/config");
    sessionStorage.setItem("uf.token", "tok");
    const policy = { unknownSender: "unverified_queue", closureNeedsResident: true, autoConfirm: false, autoConfirmDays: 3, reminderDays: 1, aiMonthlyTokenCap: 2000000,
      estimateApprovalLimit: 0, secondApproverAbove: 0, approvalExpiryHours: 72, estimateWaitHours: 24, overrunPercent: 20, repeatedRepairCount: 3, repeatedRepairMonths: 6,
      duplicateRadiusMetres: 300, duplicateWindowDays: 7, duplicateThresholdPercent: 70 };
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Manager") };
      if (url.pathname === "/api/organization/settings") return { body: { displayName: "Gulshan", timezone: "Asia/Karachi", locale: "en", locationTemplate: "society", primaryColor: null, logoKey: null } };
      if (url.pathname === "/api/organization/policies") {
        if (init?.method === "PUT") { saved.push(JSON.parse(String(init.body))); return { body: policy }; }
        return { body: policy };
      }
      if (url.pathname === "/api/reports/auto-route-readiness") return { body: [
        { categoryId: "c1", key: "water", name: "Water", autoRoute: false, samples: 50, accuracy: 0.95, requiredAccuracy: 0.9, requiredSamples: 30, ready: true },
        { categoryId: "c2", key: "roads", name: "Roads", autoRoute: false, samples: 4, accuracy: 0.5, requiredAccuracy: 0.9, requiredSamples: 30, ready: false }] };
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    const limit = await screen.findByLabelText(/^Estimates above this need approval/);
    await user.clear(limit);
    await user.type(limit, "15000");
    const form = limit.closest("form")!;
    await user.click(within(form).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]).toMatchObject({ estimateApprovalLimit: 15000, approvalExpiryHours: 72 });

    await user.click(screen.getByRole("tab", { name: "Automatic routing" }));
    const rows = await screen.findAllByRole("row");
    const water = rows.find((r) => within(r).queryByText("Water"))!;
    const roads = rows.find((r) => within(r).queryByText("Roads"))!;
    expect((within(water).getByRole("button", { name: "Turn on" }) as HTMLButtonElement).disabled).toBe(false);
    expect((within(roads).getByRole("button", { name: "Turn on" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("builds the dashboard from the widgets the server names for the role, and offers the chart as a table (DB-02, DB-08)", async () => {
    window.history.pushState({}, "", "/dashboard");
    sessionStorage.setItem("uf.token", "tok");
    const zeros = [0, 0, 0, 0];
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Accounts") };
      if (url.pathname === "/api/dashboard/layout") return { body: { view: "Accounts", widgets: ["spend", "trends", "charges"] } };
      if (url.pathname === "/api/dashboard/spend") return { body: { from: "2026-10-01T00:00:00Z", to: "2026-10-23T00:00:00Z", jobs: 3, estimated: 1000, actual: 1500, byCategory: [{ key: "k", name: "Water", jobs: 3, estimated: 1000, actual: 1500, variance: 500 }], overruns: [], lowStock: [], chargesWaiting: 2 } };
      if (url.pathname === "/api/dashboard/trends") return { body: { granularity: "week", by: "none", periods: ["2026-10-05", "2026-10-12", "2026-10-19", "2026-10-26"], series: [{ key: "all", name: "All", created: [1, 4, 2, 0], resolved: zeros }] } };
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    // The first tab the role has widgets for opens by itself; the others are one click away.
    expect(await screen.findByRole("img", { name: /7 reported and 0 resolved/ })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Show as table" }));
    const row = (await screen.findByText("12 Oct")).closest("tr")!;
    expect(within(row).getByText("4")).toBeTruthy();
    await user.click(screen.getByRole("tab", { name: /Finance and inventory/ }));
    expect((await screen.findAllByText("Rs 1,500.00")).length).toBeGreaterThan(0);
    expect(screen.getByText("Repair charges to review")).toBeTruthy();
  });

  it("downloads the summary for the chosen dates and lets a manager set who gets the monthly one (DB-04, DB-05)", async () => {
    const asked: string[] = [];
    const saved: unknown[] = [];
    URL.createObjectURL = vi.fn(() => "blob:test");
    URL.revokeObjectURL = vi.fn();
    window.history.pushState({}, "", "/reports");
    sessionStorage.setItem("uf.token", "tok");
    handler = (url, init) => {
      if (url.pathname === "/api/me") return { body: me("Manager") };
      if (url.pathname === "/api/directory") return { body: [{ userId: "u1", displayName: "Sana", role: "Manager", teamIds: [] }, { userId: "u2", displayName: "Bilal", role: "Technician", teamIds: [] }] };
      if (url.pathname === "/api/exports/summary") { asked.push(url.search); return { body: {}, headers: { "content-disposition": 'attachment; filename="summary-2026-10-23.pdf"' } }; }
      if (url.pathname === "/api/reports/summaries" && (init?.method ?? "GET") === "GET") return { body: [{ id: "s1", period: "2026-09", periodStart: "2026-09-01", periodEnd: "2026-09-30", language: "en", includesCosts: true, sizeBytes: 9000, recipients: 2, createdAt: "2026-10-01T02:00:00Z", scheduled: true }] };
      if (url.pathname === "/api/reports/summary-schedule") {
        if (init?.method === "PUT") { saved.push(JSON.parse(String(init.body))); return { body: {} }; }
        return { body: { enabled: false, dayOfMonth: 1, language: null, recipientUserIds: [] } };
      }
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("tab", { name: "Summaries" }));
    await user.type(await screen.findByLabelText("From"), "2026-09-01");
    await user.click(screen.getByRole("button", { name: "Download PDF" }));
    await waitFor(() => expect(asked).toHaveLength(1));
    expect(asked[0]).toContain("format=pdf");
    expect(asked[0]).toContain("from=2026-08-31T");

    expect(await screen.findByText("2026-09")).toBeTruthy();
    // Only people who can read the dashboards are offered as recipients.
    await user.click(await screen.findByLabelText(/Sana/));
    expect(screen.queryByLabelText(/Bilal/)).toBeNull();
    await user.click(screen.getByLabelText("Send the monthly summary"));
    await user.click(within(screen.getByText("Send it every month").closest(".uf-card") as HTMLElement).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saved).toEqual([{ enabled: true, dayOfMonth: 1, language: null, recipientUserIds: ["u1"] }]));
  });

  it("lets the platform team open a read-only support session with a reason, and shows a banner with a way out (PA-05)", async () => {
    const opened: unknown[] = [];
    const ended: string[] = [];
    window.history.pushState({}, "", "/platform");
    sessionStorage.setItem("uf.token", "admin-token");
    let inSupport = false;
    handler = (url, init) => {
      if (url.pathname === "/api/me") {
        return inSupport
          ? { body: { ...me("PlatformAdmin"), organizationId: "o2", memberships: [{ id: "00000000-0000-0000-0000-000000000000", organizationId: "o2", organizationName: "Gulshan", role: "Support", departmentId: null, gateId: null }], support: { sessionId: "s1", expiresAt: "2099-01-01T10:30:00Z" } } }
          : { body: me("PlatformAdmin") };
      }
      if (url.pathname === "/api/admin/organizations/overview") return { body: [{ id: "o2", name: "Gulshan", slug: "gulshan", createdAt: "2026-01-01T00:00:00Z", members: 9, houses: 120, complaints: 40, lastComplaintAt: null }] };
      if (url.pathname === "/api/admin/organizations/o2/onboarding") return { body: { organizationId: "o2", readyForUse: false, steps: [{ key: "manager", done: true, count: 1 }, { key: "houses", done: false, count: 0 }] } };
      if (url.pathname === "/api/admin/organizations/o2/features") return { body: [{ feature: "auto_routing", enabled: true, default: true }, { feature: "gate", enabled: false, default: false }] };
      if (url.pathname === "/api/admin/organizations/o2/support-sessions") { opened.push(JSON.parse(String(init?.body))); inSupport = true; return { status: 201, body: { sessionId: "s1", token: "support-token", expiresAt: "2099-01-01T10:30:00Z", organizationId: "o2" } }; }
      if (url.pathname === "/api/admin/support-sessions/s1/end") { ended.push(sessionStorage.getItem("uf.token") ?? ""); inSupport = false; return { status: 204, body: {} }; }
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Manage" }));
    expect(await screen.findByText("A manager")).toBeTruthy();
    expect(screen.getByText("Still being set up")).toBeTruthy();
    const start = await screen.findByRole("button", { name: "Start looking" });
    expect((start as HTMLButtonElement).disabled).toBe(true); // a reason is required
    await user.type(screen.getByLabelText(/^Why do you need to look/), "Resident cannot upload a photo");
    await user.click(start);

    await waitFor(() => expect(opened).toEqual([{ reason: "Resident cannot upload a photo", minutes: 30 }]));
    expect(await screen.findByText(/Support session for Gulshan: you can look but not change anything/)).toBeTruthy();
    expect(sessionStorage.getItem("uf.token")).toBe("support-token");

    await user.click(screen.getByRole("button", { name: "Leave support session" }));
    await waitFor(() => expect(ended).toHaveLength(1));
    expect(ended[0]).toContain("admin-token"); // the session is ended with the person's own token, not the support one
    expect(sessionStorage.getItem("uf.token")).toBe("admin-token");
  });

  it("redirects to sign-in without a session", async () => {
    const { App } = await import("./App");
    render(<App />);
    await waitFor(() => expect(window.location.pathname).toBe("/login"));
    expect(await screen.findByLabelText(/^Email/)).toBeTruthy();
  });

  it("asks for the authenticator code only after the password was accepted", async () => {
    const calls: unknown[] = [];
    handler = (url, init) => {
      if (url.pathname === "/api/auth/login") {
        const body = JSON.parse(String(init?.body ?? "{}")) as { totpCode: string | null };
        calls.push(body.totpCode);
        return body.totpCode ? { body: { token: "t", organizationId: "o1", totpEnrollmentRequired: false } } : { status: 401, body: { error: "totp_required" } };
      }
      if (url.pathname === "/api/me") return { body: me("Supervisor") };
      return empty(url, init);
    };
    const { App } = await import("./App");
    render(<App />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText(/^Email/), "a@x.test");
    await user.type(screen.getByLabelText(/^Password/), "secret");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    const code = await screen.findByLabelText(/Authenticator code/);
    expect(screen.queryByRole("alert")).toBeNull(); // asking for the code is not an error
    await user.type(code, "123456");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(window.location.pathname).toBe("/dashboard"));
    expect(calls).toEqual([null, "123456"]);
    expect(sessionStorage.getItem("uf.token")).toBe("t");
  });

  it("renders right-to-left in Urdu", async () => {
    localStorage.setItem("uf.locale", "ur");
    try {
      await mount("Manager");
      await screen.findByRole("navigation", { name: "مرکزی نیویگیشن" });
      expect(document.documentElement.dir).toBe("rtl");
      expect(document.documentElement.lang).toBe("ur");
    } finally { localStorage.removeItem("uf.locale"); }
  });
});

describe("duplicate review (WP-10)", () => {
  it("shows the pair side by side with the reason, and groups them only when the supervisor says so", async () => {
    const decisions: unknown[] = [];
    handler = (url, init) => {
      if (url.pathname === "/api/complaints/c1/duplicates") return { body: [{ id: "s1", complaintId: "c2", reference: "UF-2", description: "No water in street 4", createdAt: "2026-10-23T09:00:00Z",
        score: 0.91, textSimilarity: 0.9, distanceMetres: 40, hoursApart: 1, sameCategory: true, reason: "Similar words, 40 m apart, same category", status: "Pending" }] };
      if (url.pathname === "/api/duplicate-suggestions/s1/decision") { decisions.push(JSON.parse(String(init?.body))); return { body: {} }; }
      return undefined;
    };
    const { DuplicatesCard } = await import("./pages/Duplicates");
    const { MemoryRouter } = await import("react-router-dom");
    // The modules are reloaded between tests, so take the provider from the same copy the page uses.
    const { LocaleProvider } = await import("@urbanfix/ui");
    const { catalog } = await import("./i18n");
    const complaint = { id: "c1", reference: "UF-1", description: "Tap dry", createdAt: "2026-10-23T08:00:00Z", incidentId: null } as never;
    render(<LocaleProvider locale="en" catalog={catalog}><MemoryRouter><DuplicatesCard complaint={complaint} onChanged={() => undefined} /></MemoryRouter></LocaleProvider>);
    const user = userEvent.setup();

    expect(await screen.findByText("No water in street 4")).toBeTruthy();
    expect(screen.getByText("Tap dry")).toBeTruthy();
    expect(screen.getByText("91% likely the same problem.")).toBeTruthy();
    expect(screen.getByText(/Similar words, 40 m apart/)).toBeTruthy();
    expect(decisions).toEqual([]); // nothing happens by itself
    await user.click(screen.getByRole("button", { name: "Same problem: group them" }));
    await waitFor(() => expect(decisions).toEqual([{ accept: true, note: null }]));
  });
});

describe("job extras", () => {
  const job = { id: "j1", number: "J-1", status: "InProgress", complaintId: "c1", chargeable: false, chargeHouseId: null } as never;

  async function mountCard(card: (x: typeof import("./pages/JobExtras")) => React.ReactNode) {
    const extras = await import("./pages/JobExtras");
    const { MemoryRouter } = await import("react-router-dom");
    const { LocaleProvider } = await import("@urbanfix/ui");
    const { catalog } = await import("./i18n");
    const { LookupProvider } = await import("./lookups");
    render(<LocaleProvider locale="en" catalog={catalog}><MemoryRouter><LookupProvider>{card(extras)}</LookupProvider></MemoryRouter></LocaleProvider>);
  }

  it("lets the technician ask the store for materials and flags what does not add up (TA-09, JC-07)", async () => {
    const sent: unknown[] = [];
    handler = (url, init) => {
      if (url.pathname === "/api/stock/jobs/j1/materials") return { body: [{ itemId: "i1", item: "Pipe", unit: "m", issued: 5, returned: 1, used: 2, unexplained: 2, flagged: true }] };
      if (url.pathname === "/api/stock/requests" && init?.method === "POST") { sent.push(JSON.parse(String(init.body))); return { status: 201, body: {} }; }
      if (url.pathname === "/api/stock/requests") return { body: [] };
      if (url.pathname === "/api/stock") return { body: [{ itemId: "i1", item: "Pipe", unit: "m", onHand: 9, lowThreshold: null, low: false }] };
      return undefined;
    };
    await mountCard((x) => <x.MaterialsCard job={job} canRequest />);
    const user = userEvent.setup();

    const row = (await screen.findAllByText("Pipe (m)")).map((e) => e.closest("tr")).find(Boolean)!;
    expect(within(row).getByText("2", { selector: "strong" })).toBeTruthy(); // the unexplained difference is highlighted
    await waitFor(() => expect(within(screen.getByLabelText(/^Item/)).getAllByRole("option").length).toBeGreaterThan(1));
    await user.selectOptions(screen.getByLabelText(/^Item/), "i1");
    await user.type(screen.getByLabelText(/^Quantity/), "3");
    await user.click(screen.getByRole("button", { name: "Send request" }));
    await waitFor(() => expect(sent).toEqual([{ jobId: "j1", note: null, lines: [{ itemId: "i1", quantity: 3 }] }]));
  });

  it("marks a repair chargeable only once a house is chosen (JB-10)", async () => {
    const sent: unknown[] = [];
    handler = (url, init) => {
      if (url.pathname === "/api/houses") return { body: [{ id: "h1", locationId: "l1", locationPath: "/l1/", plotSizeSqYd: null, houseCategory: null, status: "occupied" }] };
      if (url.pathname === "/api/jobs/j1/chargeable") { sent.push(JSON.parse(String(init?.body))); return { body: {} }; }
      return undefined;
    };
    await mountCard((x) => <x.ChargeableCard job={job} onDone={() => undefined} />);
    const user = userEvent.setup();

    const mark = await screen.findByRole("button", { name: "Mark as chargeable" });
    expect((mark as HTMLButtonElement).disabled).toBe(true);
    const select = screen.getByLabelText(/^House/);
    await waitFor(() => expect(within(select as HTMLElement).getAllByRole("option").length).toBeGreaterThan(1));
    await user.selectOptions(select, "h1");
    await user.click(mark);
    await waitFor(() => expect(sent).toEqual([{ chargeable: true, houseId: "h1" }]));
  });
});

describe("LocaleProvider catalog", () => {
  it("offers the portal strings through the shared translator", () => {
    const { container } = render(<LocaleProvider locale="ur" catalog={catalog}><span>x</span></LocaleProvider>);
    expect(container.querySelector("[dir=rtl]")).not.toBeNull();
  });
});
