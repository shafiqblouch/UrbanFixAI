import type { components } from "@urbanfix/api-client";

type Membership = components["schemas"]["MembershipDto"];

/** The roles that have screens in the app. Everyone else uses the web portal. */
export const MOBILE_ROLES = ["Resident", "Technician"] as const;
export type MobileRole = (typeof MOBILE_ROLES)[number];

export const isMobileRole = (role: string): role is MobileRole => (MOBILE_ROLES as readonly string[]).includes(role);

export type TabId = "home" | "complaints" | "new" | "today" | "jobs" | "settings";

/** Bottom tabs per role (RA-01, TA-01). */
export const tabsByRole: Record<MobileRole, TabId[]> = {
  Resident: ["home", "complaints", "new", "settings"],
  Technician: ["today", "jobs", "settings"],
};

export interface View {
  organizationId: string;
  organizationName: string;
  role: string;
}

/** Everything the signed-in person can act as: one entry per membership (the "role switcher"). */
export const viewsOf = (memberships: Membership[]): View[] =>
  memberships.map((m) => ({ organizationId: m.organizationId, organizationName: m.organizationName, role: m.role }));

/**
 * The view to open with: the one in the current organization with a role the app supports, preferring the one the person used last.
 * Returns undefined when the person has no role the app supports.
 */
export function pickView(memberships: Membership[], currentOrganizationId: string | null | undefined, lastRole?: string): View | undefined {
  const supported = viewsOf(memberships).filter((v) => isMobileRole(v.role));
  const here = supported.filter((v) => v.organizationId === currentOrganizationId);
  return here.find((v) => v.role === lastRole) ?? here[0] ?? supported.find((v) => v.role === lastRole) ?? supported[0];
}

/** When a job is due, as a short phrase source: the time left in minutes (negative when overdue). */
export const minutesUntil = (dueAt: string, now: Date): number => Math.round((new Date(dueAt).getTime() - now.getTime()) / 60_000);

export type Due = { kind: "overdue" | "soon" | "later"; amount: number; unit: "m" | "h" | "d" };

/** Buckets a due time into what the chip shows: overdue, due within a day ("soon"), or no rush. */
export function dueChip(dueAt: string, now: Date): Due {
  const minutes = minutesUntil(dueAt, now);
  const abs = Math.abs(minutes);
  const [amount, unit] = abs < 60 ? [abs, "m" as const] : abs < 24 * 60 ? [Math.round(abs / 60), "h" as const] : [Math.round(abs / (24 * 60)), "d" as const];
  return { kind: minutes < 0 ? "overdue" : minutes <= 24 * 60 ? "soon" : "later", amount, unit };
}

/** "Today's jobs": active jobs due within a day or already overdue, most urgent first. Jobs with no rush are listed under "My jobs". */
export function todaysJobs<T extends { job: { status: string }; dueAt: string }>(jobs: T[], now: Date): T[] {
  return jobs
    .filter((j) => ["Open", "Accepted", "InProgress"].includes(j.job.status) && dueChip(j.dueAt, now).kind !== "later")
    .sort((a, b) => new Date(a.dueAt).getTime() - new Date(b.dueAt).getTime());
}
