# Gate security

Entry/exit control for a gated society: a guard at a manned gate logs people and vehicles going in and
out, checks them against the resident directory, and lets in visitors the residents pre-authorised. Managers
oversee the gate log and see who is currently inside.

The feature is behind the per-society **`gate`** feature flag (off by default). It builds directly on the
existing directory of vehicles and daily help (`0020_registry`), the `Role.Guard` role, the `gate.log` /
`gate.manage` permissions, and `Membership.GateId` (a guard scoped to one gate) — all of which already
existed in the codebase before this feature.

## Actors and permissions

| Role | Permission | Can do |
|------|-----------|--------|
| **Guard** | `gate.log` | Look up plates/names, record entry & exit, see who's inside, issue/cancel visitor passes |
| **Manager** | `gate.log` + `gate.manage` | Everything a guard can, plus configure gates (checkpoints) |
| Resident | — (future) | Pre-authorise an expected guest from the mobile app (phase 2) |

Every gate endpoint also requires the society's `gate` feature to be on, enforced by an endpoint filter that
returns `403 {"error":"feature_disabled","feature":"gate"}`. The filter runs **before** the auth policy, so while
the feature is off even a fully-permissioned manager gets a 403 — the feature state masks the role.

**Switching the feature on.** `gate` is off by default. A platform admin can toggle it for any society
(`PUT /admin/organizations/{id}/features/gate`), and a society's own **manager** can self-provision it for their
own society via `PUT /my/features/{feature}` (guarded by `gate.manage`, written through the tenant-scoped db so
RLS keeps it to that org). This self-service route is limited to an allowlist (`Features.ManagerTogglable`, today
just `gate`); any other feature returns `400 {"error":"feature_not_self_service"}`. In the portal, the manager's
**Gate** page shows a "Turn on gate security" card whenever the feature is off, so they never see raw 403s.

## Data model (`0031_gate.sql`)

Three tenant-scoped tables (row-level security, composite `(organization_id, id)` foreign keys):

- **`gates`** — a manned checkpoint (boom barrier / pedestrian gate). Guards are attached to one via
  `memberships.gate_id`.
- **`gate_passes`** — a pre-authorised visitor or vehicle: a short human code (`PX7QK2`, no look-alike
  letters), the house being visited, a validity window, and an optional use limit. Created by the office, a
  manager, or a guard (and, in phase 2, the resident).
- **`gate_events`** — the append-only entry/exit log. One row per crossing. A correction is a *new* event,
  never an edit, so the log can be trusted. Most columns are optional: a registered car fills `vehicle_id` +
  `house_id`; a walk-up visitor fills only `subject_name`.

Pure rules live in `GateRules` (`UrbanFix.Organization/Gate.cs`): pass-code generation/normalisation, pass
validity (`Valid / NotYetValid / Expired / UsedUp / Cancelled`), a stable `SubjectKey` for pairing an exit
with its entry, and `Inside(events)` which reduces a run of events to the subjects currently inside. Daily-help
schedule checks reuse the existing `RegistryRules.IsAllowed`.

## API

All under the `Gate` tag. `L` = `gate.log`, `M` = `gate.manage`.

| Method | Route | Perm | Purpose |
|--------|-------|------|---------|
| GET  | `/gates` | M | List checkpoints |
| POST | `/gates` | M | Add a checkpoint |
| PUT  | `/gates/{id}` | M | Rename / activate / deactivate |
| GET  | `/gate/lookup?q=` | L | Who is this? — matches vehicles, daily help (with *allowed now?*), and passes (with state) |
| POST | `/gate/events` | L | Record an entry or exit |
| POST | `/gate/events/{id}/photo` | L | Attach a photo to an event |
| GET  | `/gate/events?q=&direction=&gateId=&from=&to=&limit=` | L | The log, newest first |
| GET  | `/gate/inside?gateId=` | L | Subjects whose latest crossing (last 36h) is an allowed entry |
| GET  | `/gate/passes?q=&houseId=&status=` | L | List / look up passes |
| POST | `/gate/passes` | L | Issue a pass (auto-generated code) |
| POST | `/gate/passes/{id}/cancel` | L | Cancel a pass |

## Guard flow, end to end

1. A car arrives. The guard types the plate into **lookup**. `abc 123` and `ABC-123` match the same vehicle
   (plate key), and the result shows the house. For daily help the result says whether they're allowed *right
   now* (day + time window). For an expected guest the guard types the **pass code** and sees it's valid.
2. The guard taps **record event** — `direction: in`, the matched `vehicleId` / `helpId` / `passId`, or free
   text for an unknown walk-up. `recordedBy` and `recordedAt` are stamped automatically; the gate defaults to
   the guard's own `gate_id`. A pass is "spent" on an allowed entry so use limits hold.
3. On the way out, another event with `direction: out`. **`/gate/inside`** then drops that subject from the
   inside list.
4. Trouble (forced entry, tailgating) is recorded with `decision: denied` and a note; it can be escalated into
   an **Incident** using the existing incidents machinery.

## Offline

The gate is the one place where connectivity is least reliable, so the mobile guard app (phase 2) is
offline-first: the directory (vehicles, help) and active passes sync down for lookups with no signal, and
recorded events queue locally and sync when the network returns — reusing the app's existing offline store.

## Phasing

- **Phase 1 — backend.** ✅ Entities, migration, `GateRules`, endpoints, feature-flag + permission enforcement,
  tests (12/12 gate tests green). OpenAPI contract + TS client regenerated.
- **Phase 2 — mobile Guard role.** ✅ `Guard` in `MOBILE_ROLES`, gate (lookup + record) and log (inside +
  recent) tabs, offline `GateQueue` that drains on reconnect, en/ur strings. Typecheck clean; 109 tests green.
- **Phase 4 — portal.** ✅ Manager **Gate** page: who's-inside board, crossing log, visitor passes (list +
  cancel) and checkpoint config. Typecheck clean; 31 portal tests green.
- **Phase 3 — resident pre-authorisation & live approval.** ✅ (core). Residents issue their own passes from the
  app (`/my/gate-passes`, `/my/houses`) and answer "someone is at the gate for you" (`/my/gate-visit-requests` →
  `decide`). A guard raises a request (`/gate/visit-requests`) → a pending `GatePass` + a `GateVisitRequested`
  event → the house's residents are notified (in-app + push, **and WhatsApp where that channel is installed**,
  via the existing notification pipeline). The resident's decision flips the pass to `active`/`denied` and raises
  `GateVisitDecided`, which notifies the guard. New mobile resident screen (`GatePasses`, reachable from Home and
  the push link `/gate/guests`); portal passes list shows the new `Pending`/`Denied` states. Backend 15 gate
  tests green; mobile 110; portal 31.
  - **WhatsApp free-text reply** ✅ — a resident can reply `ALLOW` / `DENY` (English or Urdu, optionally with the
    pass code, e.g. `ALLOW PX7QK2`) straight to the WhatsApp message. A pure parser `GateReply` reads the intent;
    `WhatsAppConversationService` intercepts it **before** the complaint flow and **only when the sender actually
    has a request pending**, so an ordinary message is never mistaken for a gate decision. One pending → decided;
    several → it replies listing the codes and asks which. 15 parser tests + 3 end-to-end WhatsApp tests green.

- **Phase 5 — RFID/UHF sticker + boom barrier.** ✅ A UHF reader at each lane reads a car's sticker and drives that
  lane's boom barrier. A registered resident vehicle opens **automatically** and is logged; an unrecognised car is
  **held** until a guard allows or denies it in the app. Manual entry/exit (phase 2) stays as the fallback until
  hardware is installed. See [the hardware integration contract](#rfid--boom-barrier-the-edge-contract) below.
  Backend 22 gate tests (16 flow + 6 RFID) green; mobile 110; portal 31.

## RFID / boom barrier: the edge contract

Each lane has an **edge gateway** (a small box, e.g. a Raspberry Pi) wired to its UHF reader and its boom relay. It
talks to the core over plain HTTPS and needs no user login — it authenticates with its **own reader key**, sent as
`X-Gate-Key: grk_…` (or `Authorization: Bearer grk_…`). A manager issues one key per lane in the portal
(**Gate → Readers**); the key is shown once. A reader is fixed to one direction, so entry and exit are separate
readers (and separate keys).

Three calls, all under the public `/api` prefix and behind the society's `gate` feature:

| Call | Who | Purpose |
|------|-----|---------|
| `POST /gate/scan` `{ "tag": "<EPC>" }` | reader | A tag was read (or a car is present with no tag). |
| `GET /gate/commands` | reader | Poll (~1 s) for barrier **opens** the guard has approved. |
| `GET /gate/waiting`, `POST /gate/waiting/{id}/decide` | guard app | See and decide held cars. |

**Scan response** (`ScanResult`): `action` is one of
- `open` — a registered resident vehicle (its `sticker_code` matched the tag). The crossing is logged automatically
  (`source=rfid`), and repeated reads within 20 s open again without double-logging. **Raise the barrier now.**
- `hold` — the tag is unknown (or empty): a `GateApproval` is created, the on-duty guards are notified
  (in-app + push), and `approvalId` is returned. **Keep the barrier shut** and poll `GET /gate/commands`.
- A denied decision never produces an open, so the barrier simply stays down.

**Opening after a guard approves.** When a guard taps *Open barrier* in the app, the hold flips to `approved`. The
edge's next `GET /gate/commands` returns `[{ approvalId, direction }]` **once** (the row is marked consumed, so one
approval opens the barrier one time), and a decision older than 30 s is not re-opened (the car has gone). Raise the
barrier on receipt.

**Tags.** A sticker is tied to a resident vehicle through the vehicle's existing `sticker_code` (set in the
directory). Matching is case-insensitive on the stored value; programme the EPC to equal the `sticker_code`.

**Issuing a sticker (portal).** In **Directory → a vehicle** a manager taps **Generate** to get a unique,
no-look-alike code (`POST /vehicles/{id}/sticker`, e.g. `UF7Q4K2`) saved on the car, then **Print sticker** for a
label (plate + a Code 39 barcode + the code). Programme the UHF tag's EPC to that exact code and stick it on the
car. (A pre-encoded tag instead: just type its EPC into the **Sticker code** field.)

**No presence sensor?** A car with no sticker never triggers the reader, so without a loop/IR detector the barrier
just stays down and the guard opens it the old (manual) way. With a presence detector, the gateway sends
`POST /gate/scan` with an empty `tag` on a car-present-no-tag event, which raises the same guard hold.

A throwaway reader you can drive from a terminal, to try the whole flow without hardware, is in
[`tools/gate-reader-sim.sh`](../tools/gate-reader-sim.sh).
