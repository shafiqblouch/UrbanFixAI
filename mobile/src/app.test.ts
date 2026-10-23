import { describe, expect, it } from "vitest";
import { en, ur } from "./i18n/strings";
import { dueChip, isMobileRole, pickView, tabsByRole, todaysJobs, viewsOf } from "./roles";

const membership = (role: string, org = "o1", name = "Green Society") => ({ id: `${role}-${org}`, organizationId: org, organizationName: name, role, departmentId: null, gateId: null });
const now = new Date("2026-10-22T08:00:00Z");
const at = (minutes: number) => new Date(now.getTime() + minutes * 60_000).toISOString();

describe("roles and the role switcher (DS-05)", () => {
  it("has app screens for residents and technicians only, with the tabs each needs", () => {
    expect(["Resident", "Technician", "Supervisor", "Manager", "Guard"].map(isMobileRole)).toEqual([true, true, false, false, false]);
    expect(tabsByRole.Resident).toEqual(["home", "complaints", "new", "settings"]);
    expect(tabsByRole.Technician).toEqual(["today", "jobs", "settings"]);
  });

  it("opens as the supported role in the current organization, preferring the one used last", () => {
    const both = [membership("Supervisor"), membership("Resident"), membership("Technician")];
    expect(pickView(both, "o1")?.role).toBe("Resident");
    expect(pickView(both, "o1", "Technician")?.role).toBe("Technician");
    expect(pickView([membership("Manager")], "o1")).toBeUndefined(); // nothing the app supports: show "use the portal"
  });

  it("falls back to another organization's supported role, and lists every membership to switch between", () => {
    const m = [membership("Supervisor", "o1"), membership("Resident", "o2", "Blue Towers")];
    expect(pickView(m, "o1")).toMatchObject({ organizationId: "o2", role: "Resident" });
    expect(viewsOf(m).map((v) => `${v.organizationName}:${v.role}`)).toEqual(["Green Society:Supervisor", "Blue Towers:Resident"]);
  });
});

describe("due-time chips (TA-01)", () => {
  it("shows minutes, hours or days, and whether the job is overdue, due soon or has no rush", () => {
    expect(dueChip(at(-30), now)).toEqual({ kind: "overdue", amount: 30, unit: "m" });
    expect(dueChip(at(-5 * 60), now)).toEqual({ kind: "overdue", amount: 5, unit: "h" });
    expect(dueChip(at(-3 * 24 * 60), now)).toEqual({ kind: "overdue", amount: 3, unit: "d" });
    expect(dueChip(at(90), now)).toEqual({ kind: "soon", amount: 2, unit: "h" });
    expect(dueChip(at(24 * 60), now).kind).toBe("soon");
    expect(dueChip(at(48 * 60), now)).toEqual({ kind: "later", amount: 2, unit: "d" });
  });

  it("lists today's jobs most urgent first and leaves finished and no-rush jobs out", () => {
    const jobs = [
      { id: "later", job: { status: "Open" }, dueAt: at(72 * 60) },
      { id: "soon", job: { status: "Accepted" }, dueAt: at(120) },
      { id: "overdue", job: { status: "InProgress" }, dueAt: at(-600) },
      { id: "done", job: { status: "Completed" }, dueAt: at(-60) },
      { id: "waiting", job: { status: "PendingVerification" }, dueAt: at(-60) },
    ];
    expect(todaysJobs(jobs, now).map((j) => j.id)).toEqual(["overdue", "soon"]);
  });
});

describe("translations", () => {
  const holes = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

  it("has every English string in Urdu with the same placeholders", () => {
    for (const key of Object.keys(en) as (keyof typeof en)[]) {
      expect(ur[key], key).toBeTruthy();
      expect(holes(ur[key]), key).toEqual(holes(en[key]));
    }
    expect(Object.keys(ur)).toHaveLength(Object.keys(en).length);
  });

  it("writes Urdu in Urdu script (names and codes excepted)", () => {
    const latinOk = new Set(["email"]);
    for (const key of Object.keys(ur) as (keyof typeof ur)[]) {
      if (!latinOk.has(key)) expect(ur[key], key).toMatch(/[؀-ۿ]/);
    }
  });
});
