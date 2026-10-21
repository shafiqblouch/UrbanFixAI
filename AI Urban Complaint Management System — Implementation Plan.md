# UrbanFix Society Management System — Implementation Plan

Oct 21, 2026 · @shafiq

## Executive summary

UrbanFix becomes one platform for a housing society: complaints and repairs, gate security, maintenance charges and community services, all sharing one resident and house directory. It is built in stages: customer discovery, a proof of concept, then four build phases (Phase 1 directory, complaints, jobs and job costs; Phase 2 gate and security, facility booking, requests and NOCs; Phase 3 billing, full accounting, payroll, tenant registration and governance; Phase 4 maintenance intelligence, contractors, AI assistant, community content and hardware integrations), with a paid pilot after Phase 1. The first customers are private housing societies in Rawalpindi and Islamabad.

**Problem.** Complaints arrive by phone, WhatsApp groups, paper and office visits. The hard part is not receiving them; it is getting each one to the right team, assigned, repaired, costed and verified before closure. Societies also run gate registers, dues collection, accounts, bookings and approvals on paper or in separate apps, with no shared resident directory.

**Solution.** An AI-assisted platform that takes complaints in Urdu, Roman Urdu and English (text, voice, photo), suggests category, department and priority, turns approved complaints into maintenance jobs with material estimates and actual costs, enforces response deadlines, and requires evidence and resident confirmation before closure. The same platform runs gate entry and exit, facility booking, resident requests, maintenance billing, accounting and payroll against one directory of houses, owners, tenants, vehicles and staff, through a WhatsApp bot, native Android and iOS apps and a web portal. AI suggests and answers; configured rules and people decide, and money, access and voting decisions are always made by ordinary code.

**Objectives**

1. Prove the full complaint-to-verified-repair workflow with one society in the POC.
2. Cut manual reading and forwarding of complaints through AI classification and rule-based routing.
3. Reduce duplicate field visits by grouping complaints about the same incident.
4. Control maintenance spend: estimates before work, actual cost after, approvals above a limit, stock tracked per job, contracts tracked.
5. Give management a live view of workload, overdue jobs, gate traffic, collections vs dues and expenses.
6. Reach paying customers after Phase 1 before investing in the later phases.

**Guiding principles**

- Use hosted AI models first; no custom model training until real data exists.
- AI for language, images, similarity, summaries and answering questions from the society's own documents; ordinary code for numbering, permissions, deadlines, status, passes, bookings, voting, billing, accounting, payroll and audit.
- Every AI suggestion is visible, correctable by staff and logged; the AI assistant cites its sources and never acts on money or access.
- One directory shared by every module; no module keeps its own copy of residents, houses or vehicles.
- Gate, billing and community services put UrbanFix in direct competition with Hamari Society and Manzhil, which already offer much of this. The edge must stay in AI-assisted maintenance, verified repairs and job cost control, so those are built first and the later phases follow only if pilot evidence supports them.

## Scope, stakeholders and user roles

The product covers a single housing society (with multiple departments, gates and streets) per organization. Phases are delivered in the order below; each phase builds on the shared directory and is released in slices behind feature flags.

**In scope**

- **Phase 1 — Directory, resident app, complaints and jobs:** organizations and settings; the shared directory (sectors, streets, houses, owners, tenants, vehicles, staff); complaint intake (WhatsApp, resident mobile app, office entry); Urdu/Roman Urdu/English AI; photo interpretation; routing; job assignment and technician app; status tracking; duplicate suggestions; before/after evidence; priority rules and deadlines; material estimates with manager approval, actual costs, price list and store stock; chargeable-repair flag; cost reports; notifications with native push; dashboard; access control and audit.
- **Phase 2 — Gate, community services and requests:**
  - *2a Gate and security:* guard app (offline-capable); visitor pre-approval and QR/OTP passes; walk-in visitor approval on WhatsApp; app intercom; resident and vehicle access (QR/RFID); parking; daily help clock in/out with alerts and ratings; contractor and worker passes; deliveries and parcels; entry/exit log and who-is-inside; blacklist alerts; guard shifts, attendance, patrols, incident reports and SOS; fire and medical emergency broadcast with an emergency contact list.
  - *2b Services:* facility booking (hall, gym, ground, marquee); requests and NOCs (construction/renovation, house transfer, move-in/move-out, gate pass for moving goods) with forms, fees and approval steps.
- **Phase 3 — Money, tenants and governance:**
  - *3a Billing and payments:* charge heads and rate rules; monthly bills with late fees and fines; payments (Raast, JazzCash, Easypaisa, bank transfer, cash/cheque); receipts on WhatsApp; ledger per house; defaulters and reminders; chargeable repairs and facility/request fees posted to bills.
  - *3b Full accounting and payroll:* chart of accounts and double-entry journals; budgets; vendors, purchase orders and vendor payments; income vs expense including job costs; audit-ready reports; export to accounting software; staff attendance and payroll for guards, sweepers and maintenance workers.
  - *3c Tenants and governance:* tenant registration with police verification status and expiry reminders; resident polls and voting; AGM notices; meeting minutes; record of decisions.
- **Phase 4 — Maintenance intelligence and community:**
  - *4a Maintenance intelligence:* GIS maps and hotspots; asset register; repeat-fault analysis; preventive maintenance; contractors and service contracts (renewal reminders, ratings); AI insights; AI assistant on WhatsApp; advanced image comparison; technician offline mode and GPS check-in.
  - *4b Community content:* document library; utility notices (water schedule, tanker requests, outages, generator timings); community board (events, lost and found, buy/sell).
  - *4c Hardware integrations:* CCTV link at gates, number-plate cameras and boom-barrier integration.
- **Cross-cutting (grows through all phases):** notifications and broadcast notices with read tracking; dashboards; roles and permissions; audit log.

**Out of scope (for now)**

- Drone inspection, vehicle-video pothole detection, IoT leak sensors, predictive maintenance models.
- Government system integration and procurement.
- Multi-property facility-management companies, workload planning and area-comparison reports: deferred until after Phase 4 and real customer demand (the current focus is a single society).
- Statutory tax and payroll filings (income tax returns, EOBI or social-security submissions): UrbanFix prepares and exports the data; filing stays with the accountant.
- Supplying or installing hardware (tablets, RFID readers, cameras, boom barriers); UrbanFix integrates with it.
- Acting as an emergency service: life-safety reports show a call-emergency-services prompt. The SOS alert and the fire/medical broadcast notify the society's own supervisors and residents; they do not contact police, fire brigade or ambulances.

**Apps and users**

| App | Used by | Main jobs | Form |
| --- | --- | --- | --- |
| WhatsApp bot + resident mobile app (Android, iOS) | Residents, tenants | Report complaints, approve visitors, pay dues, book facilities, submit requests, vote, get notices | WhatsApp channel; native mobile app (resident role) |
| Gate app | Security guards | Log entry/exit of visitors, vehicles, daily help, deliveries; call residents by app intercom | Mobile app (guard role) on a tablet or phone at each gate; offline |
| Technician app | Plumbers, electricians, civil, sanitation workers | Accept jobs, upload photos, enter materials used | Mobile app (technician role) |
| Office and admin web portal | Society office, supervisors, accounts | Manage complaints, jobs, billing, residents, settings | React web app |
| Management dashboard | Society manager, committee | Reports on complaints, security, collections and expenses | Part of the web portal (manager and committee view) |

The resident, guard and technician apps are one native mobile app with role-based login; the office portal and management dashboard are one web app.

**User roles**

| Role | Who | What they do | Main interface |
| --- | --- | --- | --- |
| Resident / tenant | Owners, tenants, shop owners | Report complaints, confirm or dispute repairs, approve visitors and passes, rate daily help, book facilities, submit requests, vote, check and pay dues, read notices | WhatsApp, mobile app |
| Security guard | Gate guards and gate supervisors | Log visitors, vehicles, daily help, deliveries; call residents by intercom; check in on patrols; raise incidents, SOS and emergency broadcasts | Mobile app (guard role) |
| Technician | Plumbers, electricians, sanitation and civil workers | Accept jobs, update progress, upload evidence, list materials and estimates | Mobile app (technician role) |
| Office staff | Society office | Register walk-in and phone complaints, maintain the directory, record cash/cheque payments, process requests and bookings | Web portal |
| Supervisor | Department head (Water, Electrical, Roads, etc.) | Review AI suggestions, assign jobs, prepare estimates, verify evidence, close or reopen | Web portal |
| Accounts | Society accountant | Charge rules, bills, payments, ledgers, journals, budgets, vendors, purchase orders, payroll, exports | Web portal |
| Committee | Elected committee members | View dashboards and reports, run polls and AGM notices, record minutes and decisions, approve items assigned to them | Web portal, mobile app (resident role with committee tools) |
| Manager | Society manager, estate office | Configure departments, rules, gates, charge rules and approval limits; approve large estimates, purchase orders and requests; view dashboards | Web portal, dashboard |
| Platform admin | UrbanFix team | Onboard organizations, monitor AI quality and costs, support | Internal area of the web portal |

**Other stakeholders:** society management committees (buyers), Meta/WhatsApp (channel provider), Apple and Google (app stores), payment providers (Raast participants, JazzCash, Easypaisa, banks), AI model provider, SMS gateway provider, calling/VoIP provider, hardware vendors (CCTV, ANPR, boom barriers), and later municipal bodies and facility-management companies.

## Functional requirements by module

Modules are grouped by area and tagged with their build phase. Each complaint moves through one configurable lifecycle (diagram below).

### Platform and directory

| # | Module | Key capabilities | Phase |
| --- | --- | --- | --- |
| 1 | Organization and settings | Register societies; location hierarchy (sector → street → house); departments, teams, categories; gates, charge rules, approval limits; branding; per-organization settings | 1 |
| 2 | Shared directory | Sectors, streets, houses, owners, tenants, vehicles and staff in one directory used by every module; resident–house linking (owner and tenant); bulk import from CSV; building → floor → unit template for apartment buildings | 1 |
| 3 | Identity and access | Phone-number OTP login for residents; staff accounts with 2FA; roles (resident, guard, technician, office, supervisor, accounts, committee, manager, platform admin); device registration for gate tablets; role switcher for people with several roles | 1 |
| 4 | Notifications and notices | WhatsApp, SMS fallback, native push and email; event messages; broadcast announcements with read tracking (in-app, plus WhatsApp read status where available) | 1 (extended in 2, 3 and 4) |
| 5 | Dashboard and reports | Phase 1: counts by status/department/sector, overdue jobs, team workload, average resolution time, reopened complaints, trends, job spend; later phases add gate traffic, bookings, collections vs dues, expenses and hotspots on a map; CSV/PDF export; scheduled monthly summary; live updates | 1 (extended) |
| 6 | Audit | Append-only history of actions including AI suggestions and overrides, approvals, gate events, votes (without revealing secret ballots), journals and payroll | 1 |

### Phase 1 — Complaints

| # | Module | Key capabilities | Phase |
| --- | --- | --- | --- |
| 7 | Complaint intake | WhatsApp text, photo and voice; resident app; office entry; reference number (e.g. UF-2026-00125); asks for missing location or map pin; optional free-text asset/pole ID carried to the asset register in Phase 4 | 1 |
| 8 | WhatsApp bot | Guided conversation, language detection, status check by reference number, receipts and updates via approved templates; later phases add visitor approval buttons, bookings, requests, balance checks and polls | 1 (extended) |
| 9 | Language AI | Classify Urdu, Roman Urdu and English text; extract problem, location, landmarks; voice-note transcription | 1 |
| 10 | Photo interpretation | Size/quality checks; suggest visible issue and category; never claims hidden faults; staff can correct | 1 |
| 11 | Routing | Map category + location to department/team via configurable rules; auto-route only when confident and enabled; otherwise to supervisor queue | 1 |
| 12 | Status and lifecycle | New → Under Review → Assigned → In Progress → Resolved (pending verification) → Closed; reject (with reason shown to resident), reopen, merge. Closure needs before/after evidence, supervisor sign-off and resident confirmation | 1 |
| 13 | Duplicate detection | Compare category, distance, time window and text/photo similarity; suggest grouping into one incident; never auto-merge when unsure | 1 |
| 14 | Evidence and verification | Evidence type set per category: before/after photos, technician test checklist (e.g. "lamp tested ON") or note, because a photo alone cannot prove every repair; supervisor review; resident confirmation; dispute → reopen | 1 |
| 15 | Priority and SLA | Rule-based priority (category default adjusted for location risk) with AI flag for safety terms; target acknowledge/resolve times per category; escalation on breach | 1 |

### Phase 1 — Maintenance jobs, materials and expenses

| # | Module | Key capabilities | Phase |
| --- | --- | --- | --- |
| 16 | Job assignment | Every approved complaint becomes a job with job number, team, technician, due date, instructions and notes; acknowledge, progress notes, cannot-complete with reason | 1 |
| 17 | Estimates and approvals | Before work, technician or supervisor lists materials (quantity and rate), labour hours and transport; estimates above a set amount need manager approval before work starts; rejected estimates return for revision | 1 |
| 18 | Actual costs and variance | At completion, materials really used and final cost are recorded and compared with the estimate; large overruns are flagged for review | 1 |
| 19 | Material price list and store stock | Price list of items (cement bags, pipe, fittings, wire, bulbs, etc.) with rates; stock received and issued per job; low-stock alerts; receipts from purchase orders in Phase 3 | 1 |
| 20 | Chargeable repairs | Repairs inside a house can be marked chargeable; the final cost becomes a charge on that resident's bill once billing exists (Phase 3). Until then the flag and amount are stored | 1 (posting in 3) |
| 21 | Cost reports | Spend per job, category, street and asset; repeated repairs flagged; spend feeds accounting in Phase 3 | 1 (asset in 4) |

### Phase 2a — Gate and security

| # | Module | Key capabilities | Phase |
| --- | --- | --- | --- |
| 22 | Guard app and offline sync | Tablet/phone at every gate; works offline and syncs when signal returns; device enrollment and remote revoke | 2a |
| 23 | Visitors and passes | Resident pre-approves a guest and a QR/OTP pass is sent; walk-in visitors logged (name, phone, vehicle number, photo, house visited) and the resident approves or rejects on WhatsApp; configurable timeout fallback | 2a |
| 24 | App intercom | Guard calls the resident through the app, keeping phone numbers private; resident accepts or declines; call log; fallback to WhatsApp approval, a masked phone call or the office when the app is not reachable | 2a |
| 25 | Residents and vehicles | QR or RFID sticker for registered cars and bikes; quick entry log | 2a |
| 26 | Parking | Slots allotted per house, visitor parking zones with occupancy and overstay alerts, vehicle stickers | 2a |
| 27 | Daily help | Maids, drivers and cooks registered with photo and linked houses; clock in/out at the gate; residents get alerts and can rate them; attendance history | 2a |
| 28 | Contractors and workers | Passes with allowed days and times | 2a |
| 29 | Deliveries and parcels | Logged at gate, resident notified, collection recorded | 2a |
| 30 | Entry/exit log and presence | Entry and exit time for every person and vehicle; who is inside right now; blacklist alerts | 2a |
| 31 | Guard management and safety | Guard shifts, attendance, patrol check-ins, incident reports, SOS/panic alert to supervisors | 2a |
| 32 | Emergencies | Fire or medical broadcast to all residents (push, WhatsApp, SMS) with acknowledgement tracking; emergency contact list available offline in the app; role-limited trigger with confirm step and drill mode | 2a |

### Phase 2b — Facility booking and requests

| # | Module | Key capabilities | Phase |
| --- | --- | --- | --- |
| 33 | Facility booking | Community hall, gym, ground, marquee; calendar, opening hours and slot rules; fees and deposits; eligibility rules (e.g. no overdue dues once billing exists); optional approval; no double bookings; cancellation rules; usage reports | 2b |
| 34 | Requests and NOCs | Configurable request types (construction or renovation approval, house transfer NOC, move-in/move-out, gate pass for moving goods) with online form, documents, fee and approval steps; NOC PDF with a verification code; approved moving-goods requests create a gate pass | 2b |

### Phase 3a — Maintenance charges and billing

| # | Module | Key capabilities | Phase |
| --- | --- | --- | --- |
| 35 | Charge heads and rules | Monthly maintenance, security, water, garbage, one-off levies; rates by plot/flat size or category; late-fee and fine rules | 3a |
| 36 | Billing | Monthly bills generated automatically per house with preview and period lock; opening-balance import; chargeable repairs and facility or request fees as bill lines; corrections by reversal, never deletion | 3a |
| 37 | Payments | Raast, JazzCash, Easypaisa, bank transfer, and cash/cheque recorded at the office; receipts sent on WhatsApp and as PDF; payment reconciliation | 3a |
| 38 | Ledger and collections | Ledger per house (paid, pending, overdue, advance); defaulter list; automatic reminders; residents check balance and history on WhatsApp or the app | 3a |

### Phase 3b — Full accounting and payroll

| # | Module | Key capabilities | Phase |
| --- | --- | --- | --- |
| 39 | Full accounting | Chart of accounts; double-entry journals posted automatically from bills, payments, job costs, purchases and payroll; budgets with budget-vs-actual; vendors, purchase orders (request, approve, order, receive into store, match to vendor bill) and vendor payments; income vs expense; trial balance, balance sheet and receipts and payments reports; period close; export to accounting software in the format chosen with the accountant | 3b |
| 40 | Staff payroll | Employee records for guards, sweepers and maintenance workers; attendance from the gate app or manual entry; salary structure, overtime, advances and deductions; monthly payroll run with preview; payslips; payment recording; postings to accounting | 3b |

### Phase 3c — Tenants and governance

| # | Module | Key capabilities | Phase |
| --- | --- | --- | --- |
| 41 | Tenant registration | Tenant details and documents, police verification status, expiry reminders to owners; flags for houses with unverified tenants (confirm the local legal requirement) | 3c |
| 42 | Polls and committee | Resident polls with eligibility rules (e.g. one vote per house), optional secret ballot and deadlines; results; AGM notices with acknowledgement; meeting minutes; searchable record of decisions; committee member list and terms | 3c |

### Phase 4 — Maintenance intelligence, community and integrations

| # | Module | Key capabilities | Phase |
| --- | --- | --- | --- |
| 43 | GIS maps | Complaint markers, department layers, sector filters, heat maps, location search; gate and asset layers | 4a |
| 44 | Asset register | Streetlights, pipelines, pumps, transformers, lifts, generators with location, QR tags and history | 4a |
| 45 | Repeat-fault analysis | Flag assets and areas repaired repeatedly within a period, with their repair spend | 4a |
| 46 | Preventive maintenance | Recurring inspection schedules and reminders | 4a |
| 47 | Contractors and service contracts | Contractor records; annual maintenance contracts for lifts, generators and water pumps linked to assets; renewal reminders; payment schedule linked to vendor bills; contractor ratings after each job; performance report | 4a |
| 48 | AI insights | Plain-language summaries, trends, flags for unusually frequent repairs or rising maintenance spend, natural-language questions over complaint, job and cost data | 4a |
| 49 | AI assistant on WhatsApp | Answers resident questions ("how much do I owe?", "what are the construction rules?", "is the hall free on Friday?") from the society's own data and bylaws, with source citations, only the asking resident's own data, and a handoff to the office when unsure | 4a |
| 50 | Advanced image comparison | Same-location check and visible-change highlights between before/after photos | 4a |
| 51 | Technician offline and GPS | Offline job list, queued uploads, GPS check-in at site (reuses the gate app's sync engine) | 4a |
| 52 | Document library | Bylaws, society maps, forms and circulars with categories, versions and access rules; searchable; feeds the AI assistant | 4b |
| 53 | Utility notices | Water supply schedule by area, water tanker requests, planned power outages and generator timings, with reminders | 4b |
| 54 | Community board | Events with RSVP, lost and found, buy/sell between verified residents; posting rules, reporting and moderation | 4b |
| 55 | Hardware integrations | CCTV link at gates (snapshot or clip attached to a gate event), number-plate camera and boom-barrier integration | 4c |

&#91;embedded content: Complaint lifecycle · 6 main states, 4 exceptions\]

A complaint closes only after the required evidence is submitted, a supervisor signs off and the resident confirms. Resident confirmation is required by default. To keep jobs from hanging, an organization may enable an auto-confirm rule that closes the complaint a set number of days after sign-off with no response, after reminders; such closures are marked as auto-confirmed in the audit log. Disputes send the job back for rework, and closed complaints can be reopened.

**Job cost workflow:** an estimate is listed before work. If the total exceeds the organization's approval limit, the job waits for manager approval before work starts. At completion, actual materials and cost are entered and compared with the estimate. Stock issued to the job reduces store quantity and can raise a low-stock alert. A chargeable repair carries its final cost to the resident's bill after accounts approves the charge (Phase 3a), and the job cost posts to accounting (Phase 3b).

**Unknown-number policy (configurable per organization):** a WhatsApp number is matched against the directory. If there is no match, the organization chooses one of: hold the message in an "unverified" queue for office staff to confirm; accept it as a guest complaint flagged for verification; or ask the sender to register by OTP. Status checks by reference number still require the same phone number or a verified login. Visitor approval, billing and assistant messages are sent only to verified numbers.

**Escalation rules (configurable per organization):** not assigned in time; technician has not accepted; target date passed; marked complete without required evidence; resident disputes completion; estimate awaiting manager approval too long; purchase order or request waiting for approval too long. Deadlines are enforced by scheduled jobs, not by AI.

**Gate rules (configurable per organization):** how long to wait for a resident's approval of a walk-in visitor and what the guard does on timeout (call by intercom, call by phone, allow, or deny); pass validity and entry limits; visitor parking time limits; blacklist handling; daily-help allowed hours; which events notify the resident.

**Request workflow (configurable per organization):** each request type defines its form fields, required documents, fee, checks (for example dues must be clear before a transfer NOC) and an ordered chain of approvers. Any step can approve, reject with a reason, or send back for more information; every step is timestamped and audited.

## Non-functional requirements

The system must work on low-end Android phones over patchy mobile data, in three languages, with every action traceable. Gate devices must keep working when the internet fails. Targets below are proposed starting points for the pilot.

| Area | Requirement | Pilot target |
| --- | --- | --- |
| Availability | Core API and WhatsApp intake up | 99.5% monthly (99.9% after pilot) |
| Responsiveness | API p95 latency (non-AI calls) | < 500 ms |
| AI turnaround | WhatsApp acknowledgement with reference number | < 10 s; AI enrichment can finish asynchronously |
| Scale | Organizations / complaints per year | 50 organizations, 100,000 complaints, 300,000 photos |
| Gate scale | Gate events per society | Up to ~30,000 events/day for a large society; log queries stay fast with indexes and partitioning |
| Gate speed | Pass or sticker validation at the gate | < 2 s, including when offline |
| Gate offline | Gate app works without internet | At least 24 hours offline; no lost or duplicated events on sync |
| Intercom | In-app call from guard to resident | Rings within 5 s on 4G; ≥ 95% of attempts connect or fall back automatically within 20 s |
| Emergency broadcast | Fire or medical alert to all residents | ≥ 95% of residents reached on at least one channel within 60 s |
| Booking integrity | No two bookings overlap for a facility | Enforced by a database constraint; zero double bookings |
| Low bandwidth | Client-side image compression; resumable uploads | Photo upload ≤ 500 KB after compression |
| Offline (other apps) | Technicians view jobs and queue updates without signal | Phase 4a |
| Mobile platforms | Native Android and iOS apps | Android 8+ including low-end devices; current and previous major iOS versions |
| Languages | Urdu (right-to-left), English; Roman Urdu input | All resident-facing text in Urdu and English |
| Accessibility | Large touch targets, icon + text labels, voice input | WCAG 2.1 AA for web apps |
| Data durability | Backups and restore | RPO 15 min with point-in-time restore for financial, accounting, payroll and gate data (24 h for other data); RTO 4 h; daily backups kept 30 days |
| Financial integrity | Bills, payments, journals and ledger entries are append-only; corrections by reversal | Every journal balances (debits = credits); ledgers reconcile to payments and bills; trial balance always zero-sum |
| Voting integrity | One vote per eligible unit, results auditable | Secret ballots never link a vote to a voter in stored data |
| Auditability | Every state change, assignment, approval, AI suggestion, override, gate event and financial posting logged | 100% of actions, immutable log |
| Multi-tenancy | Strict data separation between organizations | Enforced at database level |
| Observability | Logs, metrics, traces, alerting | Alerts on failed escalation, billing-run, payroll-run and payment-webhook jobs within 5 min |
| AI cost control | Per-organization usage metering and caps | AI cost tracked per complaint and per assistant conversation |

## System architecture

UrbanFix is a modular monolith in ASP.NET Core with a separate Python AI service, so business rules stay predictable while AI models can change independently. Complaints, gate, community services, billing and accounting are separate modules over one shared directory module.

**Component overview**

- **Channels:** WhatsApp Business Platform, native mobile app (resident, guard and technician roles), office/admin web portal, SMS.
- **Core backend (ASP.NET Core):** directory, identity, complaints, jobs and costs, gate, parking, booking, requests, billing, accounting, payroll, governance, notifications, dashboard, audit; Hangfire workers for escalations, bill and payroll runs, reminders and AI jobs; SignalR for live updates; a workflow engine for approvals.
- **Calling service:** real-time audio for the app intercom through a WebRTC provider with TURN servers, with native call screens (CallKit on iOS, high-priority push on Android).
- **AI service (Python/FastAPI):** language, photo, speech, embedding and question-answering tasks through a provider abstraction; returns suggestions or answers with confidence and sources.
- **Data:** PostgreSQL (+PostGIS, pgvector), Redis, S3-compatible object storage.
- **External:** payment providers (Raast, JazzCash, Easypaisa, bank), SMS gateway, email service, AI providers, calling provider, app stores, hardware (CCTV, ANPR, boom barriers in Phase 4c).

Every channel reaches one core backend through the gateway; the core asks the AI service for suggestions and keeps all rules, deadlines, passes, ledgers, journals and records itself.

**Applications and deployables.** Four deployable pieces. The React web portal and the mobile app share TypeScript code and one design system (tokens, components, i18n/RTL, API client):

| # | Deployable | Users | Notes |
| --- | --- | --- | --- |
| 1 | Office and admin web portal (incl. management dashboard and platform admin area) | Office staff, supervisors, accounts, managers, committee, platform admins | One React codebase; screens and menus depend on role; the dashboard is the manager's landing view; platform admin area behind its own role |
| 2 | Mobile app (native Android and iOS, role-based login) | Residents, guards, technicians | One app. After login the user's role decides the screens and bottom tabs: resident (complaints, visitors, services, dues, notices, polls), guard (gate), technician (jobs). People with several roles switch role in the app. Role modules load on demand, so a resident does not download or run gate code. Offline-first local storage and the shared sync engine serve the gate (Phase 2a) and technician offline mode (Phase 4a) |
| 3 | Core backend | All apps and channels | ASP.NET Core modular monolith; API, rules, Hangfire workers and SignalR; the WhatsApp bot is a channel inside it, not a separate app |
| 4 | AI service | Core backend | Python + FastAPI |

Your requirements call for native Android and iOS apps, so the mobile app is built with React Native (Expo) on TypeScript, shared with the web portal; an early prototype (MA-00) confirms this on low-end Android phones and a cheap tablet before screens are built. Residents who prefer not to install anything use the WhatsApp bot. The platform admin area can be split into its own app later if needed.

**Key design decisions**

- **Modular monolith first.** One deployable backend with clear module boundaries is faster to build and run for a small team; modules can be split into services later if load demands it.
- **One mobile app, server-enforced roles.** Role-based screens are a convenience; every API call is authorised by the user's role, organization and (for guards) enrolled device. Role modules are feature-flagged and isolated so a resident-feature bug cannot break the gate, and the API stays compatible with older app versions.
- **One directory.** Houses, owners, tenants, vehicles and staff live in one module; complaints, gate, bookings, requests, billing and payroll reference it by id and never keep copies.
- **Asynchronous AI.** The resident gets a reference number at once; AI enrichment runs as a background job with retries, so a slow or failed model call never loses a complaint.
- **Rules before AI.** Routing, priority, SLA, approval limits, request chains and charge rules are data configured per organization; AI only fills gaps and is always shown as a suggestion.
- **Event log for every change.** Status changes publish internal events that drive notifications, escalations, live dashboards (SignalR) and the audit log.
- **Outbound messaging through one module.** WhatsApp, SMS, push and email go through Notifications, with templates, retries and delivery tracking; emergency broadcasts use the same module with a high-priority path.
- **Offline-first gate with idempotent sync.** Every gate event carries a client-generated id and device timestamp; the server accepts each id once, so retries never duplicate entries. Passes carry signed QR tokens and the device caches active passes, registered vehicles, daily help, staff passes and the blacklist, so validation works offline. Actions that need the internet (WhatsApp approval of a walk-in, intercom calls) fall back to a guard-recorded manual decision when offline.
- **Double-entry accounting with sub-ledgers.** Bills, payments, vendor bills, job costs and payroll post balanced journals to one general ledger; the per-house ledger, vendor payables and payroll are sub-ledgers. Nothing is edited or deleted; mistakes are reversed with a new entry. Payment-provider webhooks are verified, idempotent and reconciled daily.
- **Bookings and approvals enforced by the database.** Facility bookings use an exclusion constraint so overlapping bookings cannot exist even under concurrent requests; request approvals run through one workflow engine.
- **AI assistant with scoped tools.** The assistant retrieves approved society documents and calls authorised APIs for the asking resident's own data; it never has raw database access and cannot change money, bookings or access.
- **Multi-tenant from day one.** One database with organization-scoped row-level security keeps costs low while isolating each society.

## AI/ML components and pipeline

All AI runs in a separate Python service that returns structured suggestions or answers with a confidence score; the core backend decides what to do with them. Phase 1 uses hosted language and vision models through prompts and embeddings, with no custom training. Billing, accounting, payroll, voting, passes and gate decisions use no AI.

| Capability | Input | Output | Approach | Human control | Phase |
| --- | --- | --- | --- | --- | --- |
| Complaint understanding | Text (Urdu, Roman Urdu, English) | Category, problem summary, location fields, language | Multilingual LLM with JSON schema output and few-shot examples per organization's categories | Staff can edit every field | 1 |
| Voice notes | WhatsApp audio | Transcript, then as above | Speech-to-text model supporting Urdu | Transcript shown beside audio | 1 |
| Photo interpretation | Resident photo | Visible issue, suggested category, "needs more info" flag | Vision-capable model with a fixed list of visible-issue labels | Must not claim hidden faults; supervisor confirms | 1 |
| Routing suggestion | Category, location, rules | Department/team + reason | Rules engine first; AI only fills gaps | Auto-route only above threshold and if enabled | 1 |
| Priority flag | Text, category, photo | Routine / standard / prompt / urgent / safety | Rules table + AI keyword and image flags (e.g. live wire, sewage overflow) | Safety flags always go to a person | 1 |
| Duplicate detection | New complaint vs open ones | Candidate incident + similarity score | Geo radius + time window filter, then text/image embedding similarity (pgvector) | Supervisor accepts or rejects grouping | 1 |
| Before/after comparison | Two photo sets | Same-location likelihood, visible-change note | Vision model comparison; Phase 4a adds image-similarity checks | Advisory only; supervisor verifies | 1 basic, 4a advanced |
| Summaries and insights | Complaint, job and cost history | Plain-language trends, hotspots, repeat faults, unusual repair frequency or cost | LLM over pre-computed SQL aggregates (never raw guessing of numbers) | Numbers come from queries, AI only words them | 4a |
| WhatsApp AI assistant | Resident question in Urdu, Roman Urdu or English | Short answer with sources, or a handoff to the office | Retrieval over approved documents (bylaws, rules, circulars) with citations, plus authorised read-only tools for the asker's own balance, bookings and facility availability | Never acts on money, bookings or access; says "I don't know" and hands off; office can review conversations | 4a |

**Pipeline for a new complaint**

1. Message arrives; core backend stores it and immediately replies with a reference number.
2. A background job sends text, transcript and photo references to the AI service.
3. AI service strips phone numbers and names, calls the models, validates the JSON against the schema, and returns suggestions with confidence and model version.
4. Backend applies routing and priority rules; asks the resident for missing location; checks for duplicates.
5. High-confidence, rule-enabled cases are auto-assigned; everything else lands in the supervisor queue with suggestions pre-filled.
6. Every suggestion, acceptance and override is stored for evaluation and prompt improvement.

**Quality and safety controls**

- Golden test set before the POC: at least 500 labelled complaints across the three languages and 300 photos, built with a pilot society.
- Start conservatively: auto-routing off until a category reaches 90% accuracy on live data.
- Complaint text and document content are treated as data, never as instructions to the model (prompt-injection guard); outputs outside the schema are rejected.
- Assistant answers must cite a document or a data source; answers without a source are refused. An assistant question set (including attempts to read other residents' data) is run before each release.
- Model and prompt versions are pinned; any change reruns the golden set before release.
- A provider abstraction layer allows switching between AI providers (for example through a gateway such as OpenRouter) or a self-hosted multilingual model later. Select models with a short spike on real complaints before the POC build; test Urdu voice transcription separately, because audio support varies by model.

## Data model

Every table carries an `organization_id` and is protected by PostgreSQL row-level security, so one society can never read another's data. Core entities:

| Entity | Key fields | Notes |
| --- | --- | --- |
| Organization | id, name, type, plan, settings, timezone | The tenant. UrbanFix subscription invoicing is manual during the pilot |
| Location node | id, parent\_id, type (sector/street/block/house, or building/floor/unit), name, geometry | Hierarchy plus PostGIS point or polygon |
| User | id, phone, email, name, language, status | One record per person |
| Membership / role | user\_id, organization\_id, role, department\_id | A user can hold roles in several organizations |
| House / unit | id, location\_node\_id, plot\_size, category, status | The anchor for complaints, passes, bookings, requests and bills |
| Resident link | house\_id, user\_id, relation (owner/tenant/family), from, to | Owners and tenants; history kept |
| Tenant record | id, resident\_link\_id, details, documents, police\_verification\_status, verified\_on, expires\_on | Phase 3c; documents encrypted |
| Committee member | user\_id, position, from, to | Committee role holders |
| Vehicle | id, house\_id, plate, type, sticker\_code (QR/RFID), status | Registered cars and bikes |
| Daily help / worker | id, person details, photo, id (masked), houses, status, allowed days and times | Maids, drivers, cooks, workers |
| Daily help rating | id, daily\_help\_id, house\_id, rating, comment | Rated by residents |
| Department / Team | id, name, supervisor\_id, members | Configured per organization |
| Category | id, name (Urdu/English), department\_id, default priority, evidence rules (photo / checklist / note), checklist template | Drives routing and closure checks |
| Routing rule | category, location scope, team, auto\_route flag, confidence threshold | Editable by administrators |
| SLA policy | category, priority, acknowledge\_within, resolve\_within, auto\_confirm\_after | Feeds escalation scheduler and auto-confirm |
| Complaint | id, reference\_no, channel, raw\_text, language, category, priority, status, location, geo\_point, asset\_hint, reporter\_id, reporter\_verified, incident\_id, timestamps | Central record |
| Media | id, complaint\_id or job\_id or visit\_id, type (photo/voice), storage\_key, stage (before/after), hash | Files kept in object storage |
| AI suggestion | id, complaint\_id, task, output JSON, confidence, model\_version, accepted, overridden\_by | Basis for accuracy tracking |
| Incident | id, category, geo\_point, status, complaint\_ids | Groups duplicates |
| Job (work order) | id, job\_no, incident/complaint\_id, team\_id, assignee\_id, status, due\_at, instructions, chargeable, charged\_house\_id, notes | Actual field work |
| Job update | id, job\_id, status, note, gps, media, checklist\_results | Progress trail and non-photo verification |
| Material item | id, name, unit, rate, active | Price list |
| Stock movement | id, item\_id, type (receive/issue/adjust), quantity, job\_id, goods\_receipt\_id, user\_id, time | Store stock is the sum of movements; low-stock threshold on the item |
| Job estimate | id, job\_id, version, status (draft/pending approval/approved/rejected), approver\_id | Estimate before work |
| Cost line | id, job\_id, estimate\_id or actual flag, kind (material/labour/transport), item\_id, quantity, rate, amount | Estimate and actual lines side by side |
| Escalation | id, target (complaint/job/estimate/request/purchase order), rule, level, notified\_user, resolved\_at | Generated by scheduler |
| Notification | id, recipient, channel, template, payload, delivery\_status, read\_at | WhatsApp, SMS, push, email, in-app |
| Notice | id, title, body, audience, type (general/AGM/utility), sent\_at | Broadcast announcements; read status via Notification |
| Feedback | id, complaint\_id, rating, confirmed\_fixed, confirmed\_by, comment | Resident confirmation and rating |
| Gate | id, name, location, devices | Gates of the society |
| Pass | id, type (visitor/staff/contractor/delivery/moving goods), house\_id, person, valid\_from/to, signed token, OTP, status | Pre-approved passes |
| Gate event | id (client-generated), gate\_id, device\_id, person/vehicle ref, direction, time, approval\_status, approved\_by, photo, synced\_at | Entry/exit log; idempotent on id; daily help clock in/out are gate events |
| Intercom call | id, gate\_id, guard\_id, house\_id, callee\_id, started\_at, answered\_at, ended\_at, outcome, fallback\_used | Call log; no audio stored by default |
| Parking slot | id, zone, number, type (resident/visitor), status | Slots of the society |
| Parking allotment | slot\_id, house\_id, from, to | Resident slots; visitor sessions link to gate events |
| Parcel | id, gate\_event\_id, house\_id, carrier, received\_at, collected\_at | Delivery handover |
| Blacklist entry | id, person or vehicle identifiers, reason, added\_by | Alerts at the gate |
| Guard shift / attendance | id, guard\_id, gate\_id, start, end, check-in/out | Attendance; feeds payroll |
| Patrol check-in | id, guard\_id, checkpoint\_id, time | QR checkpoints |
| Incident report / SOS | id, guard\_id, type, text, media, raised\_at, acknowledged\_by | SOS alerts supervisors |
| Emergency broadcast | id, type (fire/medical/other), triggered\_by, message, sent\_at, drill | Sent through Notifications |
| Emergency acknowledgement | broadcast\_id, user\_id, time | Reach and acknowledgement tracking |
| Emergency contact | id, name, category, phone, order | Offline list in the app |
| Facility | id, name, type, capacity, hours, slot rules, fee schedule, approval\_required | Hall, gym, ground, marquee |
| Facility booking | id, facility\_id, house\_id, user\_id, start, end, status, fee, bill\_line\_id, payment\_id | Exclusion constraint on (facility, time range) for active bookings |
| Request type | id, name, form schema, required documents, fee, checks, approval chain | Configured per organization |
| Request | id, type\_id, house\_id, requester\_id, data, status, current\_step, fee | Construction, transfer NOC, move-in/out, moving goods, tanker |
| Request step | request\_id, step, approver, decision, comment, time | Approval trail |
| NOC document | id, request\_id, number, file, verification\_code | Verifiable by code |
| Charge head | id, name, kind (recurring/one-off), basis (flat, by plot size, by category), active | Society-defined |
| Rate rule | charge\_head\_id, plot\_size or category, amount, valid\_from/to | Rates over time |
| Bill | id, house\_id, period, status, due\_date, total | Generated monthly |
| Bill line | id, bill\_id, charge\_head\_id or job\_id or booking\_id or request\_id, amount | Charges including chargeable repairs and fees |
| Payment | id, house\_id, method (Raast/JazzCash/Easypaisa/bank/cash/cheque), amount, reference, status, received\_at, recorded\_by | Online, bank, cash, cheque |
| Receipt | id, payment\_id, number, sent\_at | PDF and WhatsApp |
| House ledger entry | id, house\_id, type (debit/credit), amount, source (bill/payment/fine/reversal), ref\_id, journal\_id, time | Append-only receivables sub-ledger; balance is the sum |
| Reminder | id, house\_id, channel, template, sent\_at | Overdue reminders |
| Account | id, code, name, type (asset/liability/income/expense/equity), parent\_id | Chart of accounts |
| Fiscal period | id, start, end, status (open/closed) | Period close |
| Journal entry | id, date, period\_id, source, ref\_id, status, created\_by | Append-only; reversed by a new entry |
| Journal line | id, entry\_id, account\_id, debit, credit, dimension (house/vendor/employee/asset) | Debits equal credits per entry |
| Budget | id, year, account\_id, period amounts | Budget vs actual |
| Vendor | id, name, contact, bank details (encrypted), status | Suppliers and contractors |
| Purchase order | id, vendor\_id, status, approvals, total | Request, approve, order |
| PO line | po\_id, item\_id, quantity, rate | Items to buy |
| Goods receipt | id, po\_id, lines, received\_at | Creates stock movements |
| Vendor bill | id, vendor\_id, po\_id, amount, status, due\_date | Payables sub-ledger |
| Vendor payment | id, vendor\_id, bill\_ids, method, amount, reference, approved\_by | Needs approval above a limit |
| Expense | id, category, amount, date, job\_id, notes, attachment, journal\_id | Petty and direct expenses incl. job costs |
| Employee | id, person details, role, salary structure, bank details (encrypted), status | Guards, sweepers, maintenance workers |
| Attendance record | employee\_id, date, source (gate/manual), status, hours | Feeds payroll |
| Payroll run | id, period, status (draft/approved/paid), approved\_by | Preview then approve |
| Payslip | run\_id, employee\_id, earnings, deductions, advances, net | Posts to accounting |
| Poll | id, title, question, options, eligibility, secret\_ballot, start, end, status | Resident polls |
| Voter record | poll\_id, house\_id or user\_id, voted\_at | Who has voted |
| Ballot | poll\_id, option, time | Stored without voter link for secret ballots |
| Meeting | id, type (AGM/committee), date, agenda, notice\_id, minutes\_document\_id | Meetings and minutes |
| Decision | id, meeting\_id or poll\_id, text, date, status | Searchable record |
| Contractor | id, name, services, contact, status | Links to Vendor |
| Service contract | id, contractor\_id, asset\_id, type, start, end, value, renewal\_reminder\_days, payment schedule | AMC for lifts, generators, pumps |
| Contractor rating | id, contractor\_id, job\_id or contract\_id, rating, comment | After each job |
| Utility schedule | id, kind (water/power/generator), area scope, recurrence, start/end, notice\_id | Supply and outage schedules |
| Document | id, title, category, version, file, access, effective\_date, status | Bylaws, maps, forms, circulars |
| Document chunk | id, document\_id, text, embedding | For the AI assistant |
| Community post | id, type (event/lost-found/buy-sell), author\_id, body, media, status, reports | Moderated board |
| Asset (Phase 4a) | id, type, code/QR, geo\_point, install\_date, attributes | Streetlights, pipes, pumps, lifts, generators |
| Audit log | id, actor, action, entity, before/after, timestamp, ip, device | Append-only |

Indexes: spatial (GiST) on `geo_point`, vector index on complaint and document embeddings, composite indexes on (organization\_id, status, created\_at), on gate events (organization\_id, gate\_id, time) with time partitioning, on journal lines (organization\_id, account\_id, date) and on house ledger entries (organization\_id, house\_id, time); exclusion constraint on facility bookings.

## Design and user experience

The interface follows familiar management-dashboard conventions that society staff already understand: a left sidebar with count badges, KPI tiles at the top, recent-activity cards with status chips, a collection-rate progress bar, and a one-tap WhatsApp menu. UrbanFix has its own visual identity: no competitor's logo, text, icons, illustrations or colour theme is copied.

**Design principles**

- One design system for the web portal and the mobile app (shared tokens and components), in light and dark themes, in English (left-to-right) and Urdu (right-to-left, fully mirrored).
- Fewest taps for the busiest jobs: a guard logs a visitor in under a minute, a technician updates a job in three taps, a resident reports a problem in one message.
- Status is never shown by colour alone: every status chip carries an icon and a text label.
- Large touch targets (at least 48 px), high contrast outdoors, minimal typing, and clear offline and sync states.
- Plain wording in Urdu and English reviewed by native speakers; no technical terms in resident-facing screens.
- Emergency and SOS controls are always one tap away and never hidden in menus.

**Visual identity (proposed tokens, to be refined with the designer and tested for contrast)**

| Token | Value | Use |
| --- | --- | --- |
| Primary | Deep indigo `#4338CA` (hover `#3730A3`, soft `#EEF0FF`) | Sidebar highlight, primary buttons, links, active tabs |
| Accent | Coral `#FF6B4A` (soft `#FFF0EC`) | Calls to action, SOS and urgent highlights, notification dots |
| KPI tile gradients | Indigo → violet (`#4338CA` → `#7C3AED`); coral → amber (`#FF6B4A` → `#F5A524`); sky → indigo (`#0EA5E9` → `#4338CA`); rose → coral (`#E5484D` → `#FF6B4A`) | The four top-of-dashboard tiles (e.g. residents, pending complaints, today's visitors, unpaid dues) |
| Success / warning / danger | `#12B76A` / `#F5A524` / `#E5484D` | Status chips (paid, pending, overdue), always with icon and text |
| Neutrals | Text `#0F172A`, secondary `#475569`, borders `#E2E8F0`, page background `#F6F7FB`, cards `#FFFFFF`; dark theme uses deep navy surfaces | Layout and text |
| Typography | Modern geometric sans (e.g. Inter or Plus Jakarta Sans) for English; Urdu face chosen after readability tests on low-end phones (Noto Nastaliq Urdu vs Noto Naskh Arabic) | All screens |
| Shape and depth | 12–16 px rounded cards, soft shadows, 8 px spacing grid | Cards, tiles, sheets |

Every colour pair must meet WCAG 2.1 AA contrast; the palette is final only after that check and a test with real users.

**Web portal (office, supervisors, accounts, committee, manager, platform admin)**

| Area | Layout |
| --- | --- |
| Navigation | Left sidebar with icons and count badges (open complaints, pending approvals, visitors inside, parcels, bookings today, requests waiting, unpaid dues); items depend on role; collapses to icons on small screens; mirrored for Urdu |
| Header | Society name, global search (house, resident, plate, reference number), sync/refresh, notifications, language and theme switch, profile |
| Dashboard | Four KPI tiles (e.g. active residents, pending complaints, overdue jobs, unpaid dues), then cards for recent complaints, jobs awaiting approval, a maintenance/collection summary with a collection-rate progress bar, and role-specific cards (gate activity, bookings, expenses, low stock, polls) |
| Lists and detail | Filterable tables with status chips; detail pages with a timeline, evidence side by side, AI suggestions marked as suggestions, and an audit tab |
| Forms | Short, grouped, with sensible defaults and inline validation; the estimate editor uses the price list picker; request forms are built from each request type's fields |

**Mobile app (one native app, role-based login)**

| Role | Home screen | Bottom tabs |
| --- | --- | --- |
| Resident | Quick-action grid (Report a problem, Visitors, Book a facility, Requests, Dues, Notices), recent complaints with status chips, pending approvals, emergency contacts always reachable | Home, Complaints, Gate, Services, Dues (tabs appear as phases are enabled); More holds notices, polls, documents, board and profile |
| Guard | Large action buttons (Scan pass, Walk-in visitor, Call resident, Vehicle, Daily help, Parcel, Exit), inside-now count, sync banner, SOS and emergency buttons always visible | Gate, Inside now, Log, Shift |
| Technician | Today's jobs with due-time chips, accept / start / complete buttons, materials and estimate entry | Jobs, Photos, Materials, Profile |

After login the user's role decides the screens; people with more than one role switch role from the profile menu. Server-side permissions, not the screens, enforce what each role can do.

**WhatsApp bot (residents, no install needed)**

- A greeting in the resident's language, then a numbered menu with quick-reply buttons for the most common choices. Numbers work for low-literacy users; typing a problem, or sending a photo or a voice note, at any time starts a complaint without using the menu.
- Phase 1 menu: 1 Report a problem, 2 Check complaint status, 3 My complaints (confirm or dispute a repair), 4 Notices, 9 Language, 0 Talk to the office.
- Phase 2 adds: Visitor pass, My visitors (approve or reject with buttons), Parcels, Book a facility, Submit a request, Emergency contacts.
- Phase 3 adds: Dues and payments, Receipts, Polls and voting.
- Phase 4 adds: Ask a question (AI assistant), Water and power schedule, Documents, Community board.
- Every message is short, in Urdu or English, and ends with the next step ("Reply 1–4"). The bot always offers a way back to the menu or to a person.

**Design deliverables**

- Design tokens and a shared component library for web and mobile (light, dark, RTL).
- Page templates for the dashboard, list/detail and forms; mobile home screens per role; the intercom call screen.
- WhatsApp conversation flows (menu, errors, fallbacks) in Urdu and English.
- Clickable prototypes tested with real residents, technicians, guards and accounts staff before each phase is built.
- Original logo, app icon, splash screen, store listing and WhatsApp business profile assets.

## Technology stack

The stack follows the concept paper's recommendation, with PostgreSQL chosen over SQL Server so that spatial queries (PostGIS) and similarity search (pgvector) live in one database with no licence cost.

| Layer | Choice | Why | Alternative |
| --- | --- | --- | --- |
| Office and admin web | React + TypeScript, Vite, TanStack Query, i18n with RTL | Large talent pool in Pakistan; typed UI | Angular |
| UI design system | Shared design tokens, Figma library and component library for web and mobile | One look across portal and app, light and dark, English and Urdu RTL | — |
| Mobile app (resident, guard, technician roles) | React Native (Expo) with TypeScript, native Android and iOS builds, shared code with the web portal; push via FCM/APNs; confirmed by prototype MA-00 | One codebase for both platforms; reliable offline storage, camera QR scanning, push and kiosk mode; store distribution and over-the-air updates | Flutter, or separate native apps if the prototype fails |
| Offline sync (gate, Phase 4a technician) | Local SQLite queue in the mobile app, idempotent event ids, signed pass tokens cached on device | Works without signal; no duplicate events | — |
| Calling (intercom) | WebRTC through a provider (e.g. LiveKit, Agora or Twilio) with TURN; CallKit on iOS and high-priority push on Android | Private in-app calls with no phone numbers exposed | Masked PSTN calls through a telephony provider |
| Core backend | ASP.NET Core (.NET 8 LTS), modular monolith, EF Core | Strong typing, performance, mature tooling | Node.js / NestJS |
| Workflow | Approval engine inside the core backend (steps, approvers, checks, timeouts) | One engine for estimates, requests, purchase orders and payroll approval | A workflow library |
| AI service | Python 3.12 + FastAPI | Best AI library support | AI functions inside .NET |
| Language and vision AI | Hosted multimodal LLM via provider abstraction (e.g. OpenRouter) | No training needed; handles Roman Urdu and images | Fine-tuned multilingual classifier; self-hosted open model |
| Speech-to-text | Hosted speech or audio-capable model with Urdu support | Voice notes are common on WhatsApp | Open-source Whisper self-hosted |
| Assistant retrieval | pgvector over document chunks, with citations; read-only tool APIs | Answers from the society's own bylaws and data | Dedicated search service |
| Database | PostgreSQL 16 + PostGIS + pgvector | Relational + spatial + vector in one place; exclusion constraints for bookings | SQL Server with spatial functions |
| File storage | S3-compatible object storage | Cheap, durable photo and document storage with signed URLs | Azure Blob |
| Cache and realtime | Redis; SignalR for live dashboards and gate alerts | Fast reads, SignalR backplane | WebSockets |
| Background jobs | Hangfire (on PostgreSQL) | Escalations, bill and payroll runs, reminders, notifications, AI jobs, retries | Quartz.NET |
| Messaging channel | Official WhatsApp Business Platform (Cloud API) with interactive buttons | Residents already use WhatsApp; approve/reject visitors in one tap | Web form, SMS |
| Payments | Raast, JazzCash and Easypaisa integrations (directly or through a local aggregator or bank); bank transfer by reference; cash and cheque recorded manually | Payment methods the plan requires; no card data stored | Local payment aggregator |
| Accounting | Double-entry journals in PostgreSQL; reports generated server-side; export in the format of the accounting software chosen with the accountant | Audit-ready and exportable | Integration with an existing accounting package |
| Documents | Server-side PDF generation (bills, receipts, NOCs, payslips, reports); CSV/Excel export | Printable documents with verification codes | — |
| QR / RFID | QR generation and camera scanning in the mobile app; RFID readers that act as keyboard input | Works on a tablet without custom drivers | Native hardware SDK |
| Hardware (Phase 4c) | CCTV via RTSP/ONVIF snapshots or the vendor's API; ANPR and boom barriers through vendor APIs or relay controllers | Link gate events to cameras and barriers | Vendor-specific SDKs |
| SMS / email | Local SMS gateway; transactional email service | Fallback, OTP and emergency broadcast | — |
| Maps | Leaflet + OpenStreetMap tiles | Free, flexible | MapLibre |
| Auth | ASP.NET Identity / OpenID Connect, OTP for residents, device registration for gate tablets | Role-based access, 2FA for staff | Keycloak |
| DevOps | Docker, GitHub Actions, Terraform | Repeatable builds and environments | Azure DevOps |
| Monitoring | OpenTelemetry, Grafana/Prometheus, Sentry | Logs, metrics, traces, errors | Azure Application Insights |

## Security, privacy and compliance

The platform holds residents' names, phone numbers, addresses, photos of their homes, visitor photos and ID details, vehicle numbers, movement history, tenant documents, payment records, salaries and votes, so security is built in from the POC, with an external penetration test before the first paid pilot and again before billing, accounting and payroll go live.

**Access and identity**

- Role-based access control (resident, guard, technician, office staff, supervisor, accounts, committee, manager, platform admin), scoped by organization, department and gate.
- Residents log in with phone OTP; staff use password + two-factor authentication; sessions expire and can be revoked.
- Gate tablets are registered devices with a guard PIN, short session, automatic lock and remote revoke; a lost tablet can be blocked and its local cache expires.
- Tenant isolation through PostgreSQL row-level security plus organization checks in every API handler.
- Segregation of duties for money: the person who records a payment or reversal is logged; large reversals, estimates and purchase orders need a second approver; the person who requests a purchase, approves it and pays the vendor must be different people above a configured amount.
- Salary and payroll data are visible only to accounts and the manager; employee bank details are encrypted at field level.

**Data protection**

- TLS 1.2+ for all traffic; encryption at rest for database, backups and object storage; ID numbers (e.g. CNIC) encrypted at field level and masked in the UI.
- Photos served only through short-lived signed URLs; EXIF metadata stripped from stored copies after location is extracted.
- Personal details (names, phone numbers) removed from text before it is sent to AI providers; provider contracts must exclude training on customer data. Visitor, gate, billing, accounting, payroll and tenant data is never sent to AI providers; the assistant sends only approved document text and the asker's own minimal figures.
- Visitor photos and ID details kept only as long as the society's retention policy allows (configurable; automatic deletion); gate and movement history visible only to authorised roles; residents see only their own house.
- Intercom calls use in-app audio with no phone numbers shown; call metadata is kept for a configured period and audio is not recorded by default.
- Tenant verification documents are stored encrypted and visible only to office staff, the manager and the owner of that house.
- Votes: one vote per eligible unit; secret ballots store the vote without the voter link; results are verifiable from counts and voter records without revealing choices.
- No card numbers stored: online payments go through the payment provider's hosted flow; the platform keeps only references and status.
- Configurable retention (e.g. delete media of closed complaints after 24 months) and resident data export/deletion on request, subject to financial record-keeping rules.
- App-store requirements: privacy disclosures, in-app account deletion, and only the permissions each role needs (camera for guards and technicians, location where used).

**Application security**

- Follow OWASP ASVS Level 2; dependency and container scanning in CI; secrets in a managed vault.
- Verify WhatsApp and payment webhook signatures; rate-limit intake per phone number and OTP requests; replay protection on pass tokens.
- Validate all AI outputs against schemas; treat complaint text and document content as untrusted data; the assistant only calls read-only tools scoped to the authenticated resident.
- Emergency broadcasts are limited to named roles, need a confirm step and are audited; drill mode is clearly labelled.
- Append-only audit log of logins, assignments, status changes, approvals, AI suggestions and overrides, gate overrides, journals, payroll runs and ledger reversals.

**Compliance and policy**

- Pakistan's Prevention of Electronic Crimes Act 2016 and the pending federal personal-data-protection law; confirm current requirements with legal counsel before launch, including rules for CCTV-like visitor photography, handling ID numbers, tenant police-verification records, and collecting maintenance charges and late fees.
- Confirm with an accountant how receipts, late fees, fines, vendor payments and withholding taxes must be recorded; and with an accountant or labour adviser what payroll deductions and records are legally required.
- Confirm the local legal requirement for tenant police verification and what the society may store; confirm the society's bylaws or cooperative rules for voting, AGMs and NOCs.
- Meta WhatsApp Business policies: opt-in, approved message templates (including visitor, bill, reminder and poll templates), the 24-hour customer-service window, and Meta's current rules on AI assistants; verify that a task-specific society assistant is permitted before Phase 4a.
- Apple App Store and Google Play policies for the mobile app, including permissions, background calling and account deletion.
- Raast and other payment-provider requirements, including merchant onboarding and settlement rules.
- Clear privacy notice and terms in Urdu and English; data processing agreement with each customer organization; society bylaws or committee approval for visitor and tenant data collection.
- Safety disclaimer: the app is not an emergency service; life-safety keywords, the SOS alert and the fire/medical broadcast notify the society's own supervisors and residents and show an instruction to call emergency services.
- Choose a hosting region close to Pakistan and document where data is stored; check data-residency needs before any government contract.

## Phased roadmap

Work proceeds in gated phases with no fixed calendar; each phase starts when the previous gate is met and is released in slices behind feature flags. Phase 1 reaches paying customers through the pilot; Phases 2 to 4 follow only on evidence.

| Phase | Main deliverables | Exit gate |
| --- | --- | --- |
| Discovery | 15–20 interviews with administrators, supervisors, technicians, guards, accountants, committee members and residents; hands-on review of Hamari Society, NPF Maintenance, Manzhil, Aap Ki Baldia and Pakistan Citizen Portal with a feature-gap matrix; baseline metrics; pricing test; test which of gate, billing, accounting, booking, requests and governance societies would adopt from UrbanFix versus keep in their current app; pilot society signed; WhatsApp business verification started; 500+ sample complaints collected | Costliest problem confirmed; pilot partner committed; decision on which later phases are in demand |
| Proof of concept | Design system foundations; one society, four departments (water, roads, streetlights, sewerage); web intake with text/photo; AI category, location and department suggestions; supervisor assignment; technician evidence upload; a basic estimate and actual cost; verification and closure; basic dashboard | End-to-end demo with real staff; AI corrections work; full audit trail; ≥ 85% category accuracy |
| Phase 1 — Directory, resident app, complaints, jobs and costs | Shared directory; native resident and technician app; WhatsApp bot, voice notes, routing rules, duplicates, priority and SLA escalation; estimates with approval limits, actual costs, price list and stock, chargeable flag, cost reports; notifications; dashboard; multi-tenant admin; security hardening | Feature complete; pen test passed; UAT signed off |
| Pilot | Live use at 1–3 societies, weekly KPI review, prompt tuning, fixes, onboarding playbook and training material refined | KPI targets met; at least 2 pilots convert to paid plans |
| Phase 2a — Gate and security | Guard role with offline sync; visitor passes and WhatsApp approval; app intercom; vehicles, stickers and parking; daily help; contractor passes; deliveries; entry/exit log and presence; blacklist; guard shifts, patrols, incidents, SOS; emergency broadcast and contacts | A pilot society runs one gate on UrbanFix for a trial period with no lost events; gate and intercom KPIs met |
| Phase 2b — Facility booking and requests | Facilities and calendar with fees; request types, forms, fees and approval chains; NOCs; moving-goods gate passes | One facility and two request types run live at a pilot society with zero double bookings |
| Phase 3a — Billing and payments | Charge heads and rates; monthly bills; late fees; Raast, JazzCash, Easypaisa, bank, cash/cheque; receipts; house ledgers; defaulters and reminders; chargeable repairs and fees on bills | One billing cycle reconciles to the society's accountant; payment pen test passed; collection KPIs met |
| Phase 3b — Full accounting and payroll | Chart of accounts and journals; budgets; vendors, purchase orders and vendor payments; reports and export; staff attendance and payroll | A month closes with accounts matching the accountant's own books; one payroll run matches manual calculation |
| Phase 3c — Tenants and governance | Tenant registration and reminders; polls and voting; AGM notices; minutes and decisions | A live poll and an AGM notice run with correct, auditable results |
| Phase 4a — Maintenance intelligence | GIS maps and hotspots; asset register with QR tags; repeat-fault analysis; preventive maintenance; contractors and service contracts; AI insights; WhatsApp AI assistant; advanced image comparison; technician offline mode and GPS check-in | Assistant answers meet accuracy and privacy tests; Phase 4a live for paying customers |
| Phase 4b — Community content | Document library; utility notices; community board | Content features used by residents at the pilot society |
| Phase 4c — Hardware integrations | CCTV link, number-plate cameras and boom barriers at the gates of a customer that supplies the hardware | One gate runs end to end with cameras and barrier |
| Scale (ongoing) | More societies and Punjab cities, partnerships, later facility-management companies and municipal pilots | — |

## Team and resources

A core team of about 10 full-time equivalents builds Phase 1 (customer success joins at the pilot); discovery and the POC need only 4–5 of them. Phases 2 to 4 grow the team to about 14 with accounting, calling and mobile expertise.

| Role | FTE | Active in | Responsibilities |
| --- | --- | --- | --- |
| Product manager / business analyst | 1 | All phases | Customer interviews, requirements, backlog, pilot relationships, pricing tests |
| UI/UX designer | 1 | Discovery → Phase 3, then as needed | Design system, web and mobile UX, Urdu/English RTL, WhatsApp conversation design, gate, intercom and technician usability testing |
| Backend engineers (.NET) | 3 (4 from Phase 3) | POC onward (2 in POC) | Core API, workflow, rules, SLA scheduler, gate sync, billing, accounting, payroll, payments, integrations, security |
| Frontend and mobile engineers (React, React Native) | 3 (4 from Phase 2) | POC onward (1 in POC) | Office portal, dashboard, native mobile app, offline sync, intercom, maps |
| AI/ML engineer (Python) | 1 | POC onward | AI service, prompts, evaluation set, duplicate detection, assistant, cost control |
| QA engineers | 1 (2 from Phase 2) | Phase 1 onward | Test plans, automation, AI regression, offline, payment and accounting testing, UAT support |
| DevOps engineer | 0.5 (1 from Phase 2) | POC onward | Cloud, CI/CD, app-store releases, monitoring, backups, security hardening |
| Customer success / trainer | 1 | Pilot onward | Onboarding, Urdu training, support line, feedback collection |

**Other resources**

- A pilot housing society willing to share sample complaints and give 2–3 hours a week of staff time, plus a gate, an accountant and a committee for Phases 2 and 3.
- A Meta Business account with verified WhatsApp Business number and approved message templates.
- Accounts for AI provider, cloud hosting, SMS gateway, calling provider, payment providers (Raast, JazzCash and Easypaisa merchant accounts), Apple Developer and Google Play, and error monitoring.
- 3–4 low-end Android test phones, iPhones, a gate tablet and (later) RFID readers, stickers, and a CCTV/boom-barrier setup for testing.
- Part-time legal advisor for privacy policy, terms, NOC and voting rules, and customer contracts; part-time accountant for billing, accounting and payroll rules.

## Testing and QA strategy

Conventional code is tested like any business system; AI is tested separately against a labelled dataset on every model or prompt change, because its errors are statistical rather than pass/fail.

| Test type | What it covers | Tools | When |
| --- | --- | --- | --- |
| Unit tests | Rules, SLA calculation, status transitions, permissions, estimate approval limits, bill and late-fee calculation, journal balance, payroll calculation, booking overlaps, vote eligibility, pass validity | xUnit, pytest, Vitest | Every commit; 80% coverage on core modules (100% of billing, accounting and payroll rules) |
| Integration tests | API + database + background jobs, tenant isolation, billing and payroll runs, stock movements, workflow approvals | Testcontainers | Every pull request |
| Contract tests | WhatsApp webhooks, payment webhooks, SMS gateway, calling provider, AI service API | Recorded payloads, mocks, provider sandboxes | Every pull request |
| End-to-end tests | Complaint → estimate → approve → assign → complete → verify → confirm → close; visitor pre-approval → gate entry; booking → fee → bill; request → approvals → NOC; bill → payment → receipt, in Urdu and English UI | Playwright (web), Maestro or Detox (mobile app) | Nightly and before release |
| Offline and sync tests | Gate app offline for 24 h, reconnect, duplicate and out-of-order events, clock drift, lost device | Playwright with network control, real tablets | Each gate release |
| Intercom tests | Call setup, answer, decline, no answer, fallback, background and locked-screen calls on Android and iOS, poor networks | Real devices, network throttling | Each calling release |
| Concurrency tests | Simultaneous booking attempts for the same slot, simultaneous payments, duplicate webhooks | Scripted load tests | Each booking and billing release |
| Financial reconciliation tests | Duplicate payment webhooks, partial payments, reversals, advance payments, period locks, trial balance after every scenario | Scripted scenarios, sandbox accounts | Each billing and accounting release |
| AI evaluation | Category, routing, priority, duplicate and photo accuracy per language; assistant answer accuracy, citation and data-leak tests | Golden set (500+ texts, 300+ photos), assistant question set, custom harness | Every model/prompt change; weekly on live samples |
| Performance | 50 organizations, peak complaint bursts, gate-event bursts at shift change, billing and payroll runs for large societies, emergency broadcast fan-out, dashboard queries | k6 | Before pilot and each major release |
| Security | OWASP Top 10, dependency CVEs, external penetration test including payments, gate devices and the mobile app | OWASP ZAP, Trivy, third-party testers | CI scans; pen test before pilot and before billing, accounting and payroll |
| Device and network | Low-end Android, iPhones, 3G throttling, offline queue | Real phones, browser throttling | Each release of the mobile app |
| Accessibility and language | RTL layout, Urdu fonts, screen readers | axe, manual review | Each UI release |
| Usability | Residents, guards, technicians, accounts staff and committee members complete core tasks on the prototype and on each release | Moderated sessions; task success and time | Before each phase is built and at UAT |
| User acceptance | Real staff, guards, accountants and committee run real scenarios at the pilot society | Scripted UAT checklist | End of POC and end of each phase |

**POC acceptance criteria (from the concept paper):** the full complaint-to-repair workflow works end to end; staff can correct every AI suggestion; all changes are traceable in the audit log. Proposed AI bar for the POC: ≥ 85% correct category on the golden set across all three languages.

## Deployment, DevOps and operations

Three environments (development, staging, production) run as Docker containers on a managed cloud in a region near Pakistan, deployed automatically from the main branch every two weeks.

**Infrastructure**

- Containers: core API, AI service, background workers, web front ends; start on a small managed container service, move to Kubernetes only when scale needs it.
- Managed PostgreSQL with PostGIS/pgvector, automated daily backups and point-in-time restore; managed Redis; S3-compatible storage with lifecycle rules.
- CDN for web assets; WAF and rate limiting in front of public endpoints.
- TURN servers or the calling provider's relay for intercom audio.
- All infrastructure defined in Terraform; separate cloud accounts or projects per environment.

**CI/CD**

1. Pull request: build, lint, unit and integration tests, security scans.
2. Merge to main: build images, deploy to staging, run end-to-end and AI regression suites.
3. Release: manual approval, blue-green deploy to production, database migrations run first and stay backward compatible; the mobile app ships through the app stores with staged rollout and over-the-air updates, and the API stays compatible with older app versions because gate tablets update slowly.
4. Feature flags per organization let new features (e.g. auto-routing, gate, intercom, billing, accounting, payroll, assistant) be switched on for one society at a time.

**Operations**

- Monitoring dashboards: API latency and errors, WhatsApp delivery rates, background job queue depth, failed escalations, failed billing and payroll runs, payment webhook failures, gate-device sync lag, intercom success rate, emergency broadcast reach, AI latency, AI cost per organization.
- Alerting to an on-call rota during business hours in the pilot, extended later (gate, intercom, emergency and payment issues need faster response once live); status page for customers.
- Incident process: severity levels, response targets, written post-incident reviews.
- Disaster recovery drill every quarter (restore backup to a fresh environment).
- Daily payment reconciliation report, month-end ledger check and trial balance.
- Gate device fleet: enrolment, last-seen, app version, remote revoke.
- Emergency broadcast drill with each new society before go-live.
- Monthly cost review across cloud, AI, WhatsApp, SMS, calling and payment-provider fees.

## Risks and mitigations

The biggest risk is commercial, not technical: existing society apps already handle complaints, and Hamari Society and Manzhil already offer gate, billing and community services, so UrbanFix must prove it saves money or effort.

| Risk | Likelihood | Impact | Mitigation |
| --- | --- | --- | --- |
| Weak differentiation from Hamari Society, Manzhil, NPF Maintenance and similar apps, especially for gate, billing and community features | High | High | Discovery interviews first; build complaints, jobs and job cost control first; sell on verified repairs, accountability and spend control; build later phases only after pilot evidence and test whether societies will replace their current app |
| Low willingness to pay | Medium | High | Test price ranges during discovery; paid pilot with clear success metrics; tiered plans by phase |
| Scope is now a full society platform (complaints, gate, bookings, billing, accounting, payroll, governance, AI assistant) for a small team | High | High | Strict phase and slice gates; shared directory, workflow, event and audit core built once; every slice behind feature flags; add the team growth shown above only when a phase is approved; resist features outside the "What to Build" document |
| Accounting and payroll errors or missing local rules cause financial or legal problems | Medium | High | Part-time accountant on the team; double-entry with append-only journals and reconciliations; preview and approval before every run; confirm statutory rules before payroll goes live; export rather than file taxes |
| Roman Urdu and mixed-language accuracy below target | Medium | High | Golden set from real complaints, few-shot examples per organization, staff correction loop, auto-routing off until proven |
| AI assistant gives wrong answers about bylaws or leaks another resident's data | Medium | High | Answers only from approved documents with citations; refuse without a source; tools scoped to the asking resident; question set and leak tests before each release; office review of conversations; handoff to a person |
| WhatsApp policy on AI assistants changes or blocks the assistant | Medium | Medium | Verify Meta's current policy before Phase 4a; keep the assistant inside the app as a fallback |
| AI overstates what a photo shows | Medium | Medium | Fixed visible-issue labels, wording rules ("possible"), supervisor confirmation required |
| Wrong duplicate merges hide real problems | Medium | Medium | Suggest only, never auto-merge; residents stay linked and notified |
| WhatsApp Business approval delays or message costs, higher with visitor approvals, bills, reminders, polls and broadcasts | Medium | Medium | Start verification in discovery; keep most messages inside the free service window; use in-app push and SMS for reminders; track message cost per society |
| Technicians or guards resist or struggle with the apps | High | High | Very simple role screens, Urdu voice and icons, training, supervisor incentives tied to evidence compliance, guard-friendly large buttons and minimal typing |
| Mobile app offline behaviour unreliable on cheap tablets, or a resident-feature bug breaking the gate | Medium | High | Early prototype on real devices (MA-00); idempotent event ids; role modules isolated and feature-flagged; API backward compatible with older app versions; staged rollout; paper fallback procedure |
| App intercom unreliable (iOS call handling, battery restrictions on Android, weak networks) or calling costs too high | Medium | High | Prototype on real phones early; use a mature calling provider; automatic fallback to WhatsApp approval, masked phone call or the office; measure connect rate as a KPI |
| Resident approval of walk-in visitors is slow, causing queues at the gate | High | Medium | Pre-approval passes, intercom, timeout rules, resident reminders; measure approval time as a KPI |
| Billing errors or wrong late fees cause disputes | Medium | High | Preview and period lock, append-only ledger with reversals, accountant sign-off on rules, reconciliation reports, pen test |
| Payment provider onboarding delays or integration limits (Raast, JazzCash, Easypaisa) | Medium | Medium | Start merchant applications early; support manual bank and cash recording first; consider a local aggregator or bank |
| Poll or vote disputes (eligibility, secrecy) | Medium | Medium | Eligibility rules agreed with the committee and written in the poll; one vote per unit enforced by code; audit trail of counts; clear secret-ballot design |
| Tenant verification and NOC rules vary or change | Medium | Medium | Confirm local requirements with legal counsel; configure request types and checks per society instead of hard-coding rules |
| Facility double bookings or fee disputes | Low | Medium | Database exclusion constraint; clear cancellation rules; fees posted to bills with receipts |
| App-store review delays or rejection (calling, permissions, account deletion) | Medium | Medium | Follow store policies from the start; early test builds; plan store submissions ahead of releases |
| Poor location data (no street numbers, landmarks only) | High | Medium | Map pin requests, location hierarchy, landmarks list per society, office staff correction |
| AI provider cost spikes or outages | Medium | Medium | Provider abstraction, caching, per-organization caps, rules-only fallback mode |
| Data breach or privacy complaint (visitor photos, ID details, tenant documents, salaries, movement history, payments) | Low | High | Field-level encryption, retention limits, role-based access, device controls, pen test, minimal data sent to AI, DPA with customers |
| Safety incident reported through the app is handled slowly | Low | High | Hard-coded safety keywords, immediate supervisor alert, SOS and emergency broadcast with acknowledgement tracking and drills, emergency-services prompt, clear disclaimer |
| Customers ask for features beyond the document (e.g. full tax filing, visitor kiosks, facility-management multi-site) | High | Medium | Published roadmap; export to the accountant's tools; integrate or partner instead of building beyond this plan |
| CCTV, ANPR and boom-barrier hardware varies by site | High | Low | Phase 4c only, for customers who supply compatible hardware; build an adapter layer and test one vendor first |
| Long government sales cycles | High | Low (not in plan) | Focus on private societies first |

## KPIs and success metrics

The pilot succeeds if the society resolves complaints faster with fewer repeat visits and agrees to pay. Targets are proposed and should be reset once a baseline is measured in discovery.

| KPI | Definition | Pilot target | Source |
| --- | --- | --- | --- |
| Category accuracy | AI category accepted without change | ≥ 85% (≥ 90% before auto-routing) | AI suggestion log |
| Routing acceptance | AI/rule department accepted by supervisor | ≥ 80% | AI suggestion log |
| Time to acknowledge | Complaint received → staff action, median | 30% faster than baseline | Complaint timestamps |
| Time to resolve | Received → closed, median per category | 25% faster than baseline | Complaint timestamps |
| SLA compliance | Jobs completed within target time | ≥ 80% | Jobs, SLA policies |
| Evidence compliance | Closed jobs with required evidence and resident confirmation | ≥ 95% | Media records, feedback |
| Reopen rate | Closed complaints reopened within 14 days | < 10% | Status history |
| Duplicate precision | Suggested groupings accepted | ≥ 90% | Incident log |
| Resident satisfaction | Average rating after closure | ≥ 4 / 5 | Feedback |
| Digital intake share | Complaints via WhatsApp or app vs phone/paper | ≥ 70% by end of pilot | Channel field |
| App adoption | Residents (houses) who have installed the app or used the WhatsApp bot in the last 30 days | ≥ 60% of houses by end of pilot | App and bot usage |
| Estimate coverage | Jobs with an estimate before work (where required) | ≥ 90% | Job estimates |
| Estimate accuracy | Actual cost within 15% of estimate, median job | ≥ 70% of jobs | Cost lines |
| Stock accuracy | Stock count matches system count at audit | ≥ 95% | Stock movements |
| AI cost per complaint | Model spend ÷ complaints | Within budget set in pricing model | Usage metering |
| Visitor processing time (Phase 2a) | Arrival → entry logged, median | < 60 s for pre-approved, < 3 min for walk-in | Gate events |
| Digital visitor approvals (Phase 2a) | Visits pre-approved or approved on WhatsApp/app | ≥ 70% | Gate events |
| Intercom connect rate (Phase 2a) | Calls answered or automatically fallen back within 20 s | ≥ 95% | Intercom calls |
| Gate sync reliability (Phase 2a) | Events synced without loss or duplicates after offline period | 100% | Sync logs |
| Emergency reach (Phase 2a) | Residents reached within 60 s in drills | ≥ 95% | Emergency acknowledgements |
| Facility utilisation and conflicts (Phase 2b) | Booked hours vs available; overlapping bookings | Zero overlaps; utilisation tracked | Bookings |
| Request cycle time (Phase 2b) | Submission → decision, median per request type | Shorter than the paper process baseline | Request steps |
| Collection rate (Phase 3a) | Billed amount collected by due date | Improves on baseline; target set with the society | Ledger |
| Digital payments (Phase 3a) | Payments made online | ≥ 40% by end of trial | Payments |
| Billing accuracy (Phase 3a) | Bills needing correction after issue | < 2% | Reversals |
| Month-end close time (Phase 3b) | Days from month end to closed books | Shorter than baseline | Fiscal periods |
| Payroll accuracy (Phase 3b) | Payslips corrected after approval | < 2% | Payroll runs |
| Poll participation (Phase 3c) | Eligible houses that voted | Tracked against baseline AGM attendance | Voter records |
| Assistant resolution (Phase 4a) | Questions answered correctly without handoff | ≥ 70% with zero data-leak failures | Assistant log, review sample |
| Commercial | Pilot societies converting to paid plans | ≥ 2 of 3 | CRM |

## Change management, training and maintenance

Adoption by supervisors, technicians, guards, accounts staff and residents decides whether the product works, so each new organization follows a fixed two-week onboarding playbook and gets Urdu-first training.

**Onboarding playbook (per organization)**

1. Kick-off with management: confirm departments, categories, SLA targets, approval limits and escalation contacts.
2. Configure the organization: location hierarchy, landmarks, routing rules, evidence requirements, message templates, price list and opening stock.
3. Import houses, owners, tenants, vehicles and staff from spreadsheets; issue staff logins.
4. Train staff, run a one-week soft launch with office staff entering complaints alongside the old process.
5. Launch to residents: QR-code posters at gates and mosques, WhatsApp broadcast, app-store links, short Urdu explainer video.
6. Week-4 review: KPIs, AI overrides, staff feedback; adjust rules.

*Phase 2 adds:* list gates and devices, enrol tablets, create guard accounts and shifts, register daily help, allot parking slots, set visitor approval timeouts and blacklist, load emergency contacts and run an emergency drill, issue vehicle stickers and staff passes, define facilities, fees and booking rules, configure request types and approval chains, run old and new gate registers in parallel for a week.

*Phase 3 adds:* define charge heads and rates, import opening balances from the accountant, run a preview bill cycle with the accountant before the first real bill, connect payment accounts, set up the chart of accounts, budgets and vendors, set up employees and salary structures and run a preview payroll, register tenants and set verification reminders, record the committee and set poll eligibility rules, announce payment options to residents.

*Phase 4 adds:* upload bylaws and circulars to the document library and review the assistant's answers with the committee, record assets and service contracts, set utility schedules, agree board posting rules, and (if used) connect cameras and barriers.

**Training**

| Audience | Format | Length | Focus |
| --- | --- | --- | --- |
| Managers / committee | In person + recorded | 2 hours | Configuration, dashboards, approvals, reports, escalation rules, polls and minutes |
| Supervisors | In person | 2 hours | Review queue, correcting AI, assigning, estimates, verifying evidence |
| Office staff | In person | 1 hour | Manual entry, directory upkeep, location fixes, bookings, requests, resident questions |
| Technicians | Hands-on in Urdu, on their own phones | 1 hour + cheat sheet | Accept job, update status, take before/after photos, enter materials |
| Guards (Phase 2) | Hands-on at the gate, in Urdu | 2 hours + cheat sheet | Visitor flow, passes, intercom, daily help, offline mode, SOS and emergency broadcast |
| Accounts (Phase 3) | In person | 3 hours | Charge rules, billing run, payments, ledgers, journals, budgets, purchase orders, payroll, reconciliation, exports |
| Residents | WhatsApp video and poster | 2 minutes | How to report, track, approve visitors, book, pay and vote |

**Support**

- WhatsApp and phone support line in business hours (extended for gate and emergency issues once live); response targets by severity.
- In-app help in Urdu and English; knowledge base of short videos.
- Monthly check-in with each customer during the first six months.

**Ongoing maintenance**

- Two-week release cycle; security patches within 7 days of a critical advisory.
- Monthly review of AI overrides to improve prompts and category examples; quarterly re-run of the full evaluation set and model comparison; assistant answers sampled and reviewed monthly.
- Quarterly dependency upgrades, disaster-recovery drill and emergency broadcast drill.
- Customer feedback board feeding the roadmap; later phases prioritised by pilot evidence.
- Documentation kept current: API reference, admin guide, runbooks, architecture decision records.
