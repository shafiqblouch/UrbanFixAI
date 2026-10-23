import { createHmac, randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { deflateSync } from "node:zlib";
import { expect, type Browser, type Page } from "@playwright/test";

export const API = "http://localhost:5181";
export const PASSWORD = "E2e-Pass-12345!";

// TOTP (RFC 6238, SHA-1, 6 digits, 30 s), the same scheme the authenticator apps and the server use -------------------

const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

function base32Decode(input: string): Buffer {
  let bits = "";
  for (const ch of input.replace(/=+$/, "").toUpperCase()) bits += ALPHABET.indexOf(ch).toString(2).padStart(5, "0");
  const bytes: number[] = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}

export function totp(secret: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const h = createHmac("sha1", base32Decode(secret)).update(counter).digest();
  const o = h[h.length - 1] & 0xf;
  const code = ((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
  return String(code % 1_000_000).padStart(6, "0");
}

// A valid image with unique content, so the server does not treat two photos as duplicates -------------------------------

export function png(seed: number, size = 96): Buffer {
  const rows: Buffer[] = [];
  let x = seed * 2654435761 >>> 0;
  for (let y = 0; y < size; y++) {
    const row = Buffer.alloc(1 + size * 3);
    for (let i = 1; i < row.length; i++) { x = (x * 1664525 + 1013904223) >>> 0; row[i] = x >>> 24; }
    rows.push(row);
  }
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = (buf: Buffer) => { let c = 0xffffffff; for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (type: string, data: Buffer) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type), data]);
    const sum = Buffer.alloc(4); sum.writeUInt32BE(crc(body));
    return Buffer.concat([len, body, sum]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size, 0); header.writeUInt32BE(size, 4); header[8] = 8; header[9] = 2;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", header), chunk("IDAT", deflateSync(Buffer.concat(rows))), chunk("IEND", Buffer.alloc(0))]);
}

// API seeding ---------------------------------------------------------------------------------------------------------

export interface Account { email: string; secret: string; password?: string }

async function call<T>(token: string | undefined, method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: { "content-type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${text}`);
  return (text ? JSON.parse(text) : undefined) as T;
}

/** Signs in through the API, enrolling a second factor the first time, and returns a verified token. */
export async function apiLogin(email: string, password: string, known?: string): Promise<{ token: string; secret: string }> {
  const first = await fetch(`${API}/auth/login`, {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email, password, totpCode: known ? totp(known) : null, organizationId: null }),
  });
  if (!first.ok) throw new Error(`login ${email} -> ${first.status} ${await first.text()}`);
  const login = (await first.json()) as { token: string; totpEnrollmentRequired: boolean };
  if (!login.totpEnrollmentRequired) return { token: login.token, secret: known! };
  const enrol = await call<{ secret: string }>(login.token, "POST", "/auth/totp/enroll");
  await call(login.token, "POST", "/auth/totp/confirm", { code: totp(enrol.secret) });
  return { token: login.token, secret: enrol.secret };
}

export interface World {
  slug: string;
  manager: Account;
  supervisor: Account;
  technician: Account;
  resident: Account;
  /** Names a test can look for on screen. */
  teamName: string;
  pipeName: string;
}

export const ADMIN_PASSWORD = "Dev-Admin-Pass-123!";
const ADMIN_EMAIL = "admin@urbanfix.local";
const ADMIN_STATE = join(import.meta.dirname, "..", ".state", "admin.json");

let admin: Promise<{ token: string; secret: string }> | undefined;
/**
 * The development platform admin. Its authenticator secret is kept in .state/admin.json (git-ignored) after the first run, because
 * the API only bootstraps that account once. If the file is lost, reset the admin's second factor in the dev database:
 * UPDATE users SET totp_enabled = false, totp_secret_protected = NULL WHERE email = 'admin@urbanfix.local';
 */
function platformAdmin() {
  admin ??= (async () => {
    const known = existsSync(ADMIN_STATE) ? (JSON.parse(readFileSync(ADMIN_STATE, "utf8")) as { secret: string }).secret : undefined;
    const session = await apiLogin(ADMIN_EMAIL, "Dev-Admin-Pass-123!", known);
    mkdirSync(dirname(ADMIN_STATE), { recursive: true });
    writeFileSync(ADMIN_STATE, JSON.stringify({ secret: session.secret }));
    return session;
  })();
  return admin;
}

/** The development platform admin as an account a browser can sign in with. */
export async function adminAccount(): Promise<Account> {
  const session = await platformAdmin();
  return { email: ADMIN_EMAIL, secret: session.secret, password: ADMIN_PASSWORD };
}

/** A fresh society: one person per role, the standard categories (water needs photos and actual costs), a team, a house and a price list. */
export async function seedWorld(): Promise<World> {
  const stamp = randomBytes(4).toString("hex");
  const slug = `e2e-${stamp}`;
  const admin = await platformAdmin();
  const managerEmail = `manager@${slug}.test`;
  await call(admin.token, "POST", "/admin/organizations", { name: `E2E Society ${stamp}`, slug, managerEmail, managerName: "Mariam Manager", managerPassword: PASSWORD });
  const manager = await apiLogin(managerEmail, PASSWORD);
  const m = manager.token;

  const add = async (role: string, name: string) => {
    const email = `${role.toLowerCase()}@${slug}.test`;
    const member = await call<{ userId: string }>(m, "POST", "/members", { email, displayName: name, password: PASSWORD, role, departmentId: null, gateId: null });
    return { email, userId: member.userId };
  };
  const supervisor = await add("Supervisor", "Sana Supervisor");
  const technician = await add("Technician", "Tariq Technician");
  const resident = await add("Resident", "Rabia Resident");

  await call(m, "POST", "/categories/seed-templates");
  const categories = await call<{ id: string; key: string; nameEn: string; nameUr: string | null; defaultPriority: string; checklist: string[] }[]>(m, "GET", "/categories");
  const water = categories.find((c) => c.key === "water")!;
  await call(m, "PUT", `/categories/${water.id}`, { key: "water", nameEn: water.nameEn, nameUr: water.nameUr, departmentId: null, defaultPriority: water.defaultPriority, evidenceRule: "photo", checklist: water.checklist, isActive: null, requiresCosts: true });

  const dept = await call<{ id: string }>(m, "POST", "/departments", { name: "Water works", nameUr: null, isActive: null });
  const teamName = "Water crew";
  const team = await call<{ id: string }>(m, "POST", "/teams", { departmentId: dept.id, name: teamName });
  await call(m, "POST", `/teams/${team.id}/members`, { userId: technician.userId, isSupervisor: false });
  await call(m, "POST", "/routing-rules", { name: "Water to crew", categoryId: water.id, locationId: null, teamId: team.id, sortOrder: 0, isActive: true });

  const block = await call<{ id: string }>(m, "POST", "/locations", { parentId: null, kind: "block", name: "Block A", nameUr: null, latitude: null, longitude: null });
  const loc = await call<{ id: string }>(m, "POST", "/locations", { parentId: block.id, kind: "house", name: "House 7", nameUr: null, latitude: null, longitude: null });
  const house = await call<{ id: string }>(m, "POST", "/houses", { locationId: loc.id, plotSizeSqYd: null, houseCategory: null, status: null });
  await call(m, "POST", `/houses/${house.id}/residents`, { userId: resident.userId, relation: "owner", fromDate: null });

  const pipeName = "PVC pipe 1 inch";
  await call(m, "POST", "/price-items", { name: pipeName, nameUr: null, unit: "metre", rate: 320.5, effectiveFrom: null });

  return { slug, manager: { email: managerEmail, secret: manager.secret }, supervisor: { email: supervisor.email, secret: "" }, technician: { email: technician.email, secret: "" }, resident: { email: resident.email, secret: "" }, teamName, pipeName };
}

// Browser sessions ------------------------------------------------------------------------------------------------------

/** Opens a fresh browser context for one person and signs in through the screen, enrolling the authenticator on first use. */
export async function signIn(browser: Browser, account: Account, labels: { email: RegExp; password: RegExp; signIn: RegExp; code: RegExp; confirm: RegExp }, locale: "en" | "ur" = "en"): Promise<Page> {
  const context = await browser.newContext({ locale: locale === "ur" ? "ur-PK" : "en-GB" });
  await context.addInitScript((l) => { try { localStorage.setItem("uf.locale", l); } catch { /* ignore */ } }, locale);
  const page = await context.newPage();
  await page.goto("/login");
  await page.getByLabel(labels.email).fill(account.email);
  await page.getByLabel(labels.password).fill(account.password ?? PASSWORD);
  await page.getByRole("button", { name: labels.signIn }).click();

  if (account.secret) {
    await page.getByLabel(labels.code).fill(totp(account.secret));
    await page.getByRole("button", { name: labels.signIn }).click();
  } else {
    // First sign-in: the screen shows the key to put into an authenticator app.
    const key = await page.locator(".secret code").innerText();
    account.secret = key.trim();
    await page.getByLabel(labels.code).fill(totp(account.secret));
    await page.getByRole("button", { name: labels.confirm }).click();
  }
  await expect(page.getByRole("navigation", { name: /./ }).first()).toBeVisible();
  return page;
}
