import type { S } from "./api";

export type Role = S["Role"];
export type MenuId = "platform" | "directory" | "charges" | "reports" | "dashboard" | "complaints" | "map" | "review" | "new" | "jobs" | "stock" | "approvals" | "notices" | "config" | "audit";

/** Which screens each role sees (WP-01). The server enforces the same rules; this only decides what to show. */
export const menusByRole: Record<Role, MenuId[]> = {
  Manager: ["dashboard", "complaints", "map", "review", "new", "directory", "jobs", "reports", "stock", "approvals", "notices", "config", "audit"],
  Supervisor: ["dashboard", "complaints", "map", "review", "new", "jobs", "reports", "stock"],
  Office: ["dashboard", "complaints", "map", "review", "new", "directory"],
  Committee: ["dashboard", "complaints", "reports", "approvals", "notices", "audit"],
  Technician: ["jobs"],
  Resident: ["complaints", "new", "notices"],
  Accounts: ["dashboard", "charges", "reports"],
  Guard: [],
  // A platform person inside a society during a support session: the read-only views only (PA-05).
  Support: ["dashboard", "complaints", "map", "reports", "audit"],
  PlatformAdmin: ["platform"],
};

export const menusFor = (role: Role | undefined): MenuId[] => (role ? menusByRole[role] : []);

export const homeFor = (role: Role | undefined): string => {
  const first = menusFor(role)[0];
  return first ? `/${first}` : "/welcome";
};

/** Roles that triage and assign work; they see the review-queue and sign-off counts. */
export const isSupervising = (role: Role | undefined) => role === "Manager" || role === "Supervisor";
export const canTriage = (role: Role | undefined) => isSupervising(role) || role === "Office";
export const canAssign = isSupervising;
/** Managers and the committee write notices; everyone else only reads the ones addressed to them. */
export const canSendNotices = (role: Role | undefined) => role === "Manager" || role === "Committee";
/** Only managers and the committee answer estimate approvals (ID-10). */
export const canApprove = (role: Role | undefined) => role === "Manager" || role === "Committee";
/** Who may read what jobs cost (the server's cost.read permission). */
export const canSeeCosts = (role: Role | undefined) => role === "Manager" || role === "Committee" || role === "Supervisor" || role === "Accounts";
export const canConfigure = (role: Role | undefined) => role === "Manager";
