// Adds demo people to an existing society and puts the technicians in the "Water crew" team (created if missing).
// Safe to run again: people who already exist are left alone.
//
//   UF_EMAIL=<manager email> UF_PASSWORD=<manager password> [UF_TOTP=<6-digit code>] node scripts/seed-demo.mjs
//
// UF_API defaults to http://localhost:5000. UF_TOTP is only needed once the manager has enrolled an authenticator app.
// Every demo person gets the password in DEMO_PASSWORD below and enrols an authenticator app at first sign-in.

const API = process.env.UF_API ?? "http://localhost:5000";
const DEMO_PASSWORD = "Demo-Pass-12345!";
const TEAM = "Water crew";

const PEOPLE = [
  { email: "tariq.technician@demo.test", name: "Tariq Technician", role: "Technician", team: true },
  { email: "bilal.technician@demo.test", name: "Bilal Technician", role: "Technician", team: true },
  { email: "sana.supervisor@demo.test", name: "Sana Supervisor", role: "Supervisor" },
  { email: "owais.office@demo.test", name: "Owais Office", role: "Office" },
  { email: "amna.accounts@demo.test", name: "Amna Accounts", role: "Accounts" },
  { email: "rabia.resident@demo.test", name: "Rabia Resident", role: "Resident" },
  { email: "gul.guard@demo.test", name: "Gul Guard", role: "Guard" },
];

async function call(token, method, path, body) {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: { "content-type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${text}`);
  return text ? JSON.parse(text) : undefined;
}

const { UF_EMAIL, UF_PASSWORD, UF_TOTP } = process.env;
if (!UF_EMAIL || !UF_PASSWORD) {
  console.error("Set UF_EMAIL and UF_PASSWORD to the society manager's sign-in.");
  process.exit(1);
}

const login = await call(undefined, "POST", "/auth/login", { email: UF_EMAIL, password: UF_PASSWORD, totpCode: UF_TOTP ?? null, organizationId: null });
if (login.totpEnrollmentRequired) {
  console.error("This manager has not enrolled an authenticator app yet. Sign in once on the portal to enrol, then re-run with UF_TOTP=<current code>.");
  process.exit(1);
}
const token = login.token;

const members = await call(token, "GET", "/members");
const userIds = new Map(members.map((m) => [m.email.toLowerCase(), m.userId]));

for (const p of PEOPLE) {
  if (userIds.has(p.email)) { console.log(`exists   ${p.role.padEnd(10)} ${p.email}`); continue; }
  const m = await call(token, "POST", "/members", { email: p.email, displayName: p.name, password: DEMO_PASSWORD, role: p.role, departmentId: null, gateId: null });
  userIds.set(p.email, m.userId);
  console.log(`created  ${p.role.padEnd(10)} ${p.email}`);
}

let teams = await call(token, "GET", "/teams");
let team = teams.find((t) => t.name === TEAM);
if (!team) {
  const depts = await call(token, "GET", "/departments");
  const dept = depts[0] ?? await call(token, "POST", "/departments", { name: "Water works", nameUr: null, isActive: null });
  team = await call(token, "POST", "/teams", { departmentId: dept.id, name: TEAM });
  team.members = [];
  console.log(`created  team       ${TEAM}`);
}
for (const p of PEOPLE.filter((x) => x.team)) {
  const userId = userIds.get(p.email);
  if (team.members.some((m) => m.userId === userId)) continue;
  await call(token, "POST", `/teams/${team.id}/members`, { userId, isSupervisor: false });
  console.log(`added    ${p.email} to ${TEAM}`);
}
console.log(`\nDone. Demo password for everyone: ${DEMO_PASSWORD}`);
