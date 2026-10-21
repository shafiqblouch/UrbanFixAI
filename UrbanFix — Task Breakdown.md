# UrbanFix Society Management System — Task Breakdown

Companion to *AI Urban Complaint Management System — Implementation Plan.md* (updated for the "What to Build" document). Tasks are ordered by dependency only, with no estimates or dates.

## How to read this document

- **Phase tag:** `POC` = proof of concept; `P1` = Phase 1 (directory, resident app, complaints, jobs, job costs); `P2a` gate and security; `P2b` facility booking, requests and NOCs; `P3a` billing and payments; `P3b` full accounting and payroll; `P3c` tenants and governance; `P4a`/`P4b`/`P4c` maintenance intelligence, community content, hardware. A task tagged POC is also part of everything after it.
- **Depends on:** task IDs that must be at least usable first. A blank means it can start immediately.
- **Done when:** the acceptance criteria.
- **Deployables:** office/admin web portal (WP), one mobile app with role-based login (shell MA; role modules RA resident, TA technician, GA guard/gate), core backend (CORE), AI service (AI). Design tasks are in DS.
- **POC scope:** one society, four departments (water, roads, streetlights, sewerage), web intake only (no WhatsApp), AI suggestions, assignment, evidence, a basic estimate and actual cost, closure, basic dashboard.

| Prefix | Epic | Prefix | Epic |
| --- | --- | --- | --- |
| FD | Foundation and DevOps | NT | Notifications and notices |
| ID | Identity, tenancy and access | DB | Dashboard and reports |
| OR | Organization setup and directory | GT | Gate and security (backend) |
| CP | Complaint core and intake | BL | Billing, payments and expenses (backend) |
| WA | WhatsApp channel | WP | Office and admin web portal |
| AI | AI service | RA | Mobile app: resident role |
| RT | Routing, priority and SLA | TA | Mobile app: technician role |
| JB | Jobs and evidence | GA | Mobile app: guard (gate) role |
| JC | Job costs, materials and stock | PA | Platform admin area |
| DP | Duplicate detection | SC | Security and compliance |
| QA | QA and AI evaluation | LN | Launch, onboarding and operations |
| DS | Design system and UX | MA | Mobile app shell and shared services |
| IC | Intercom and emergencies | FB | Facility booking |
| RQ | Requests, NOCs and workflow | AC | Full accounting |
| PR | Staff payroll | TG | Tenants and governance |

---

## DS — Design system and UX

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| DS-01 | Visual identity and design tokens: indigo/coral palette, KPI tile gradients, status colours, neutrals, typography (Latin and Urdu), spacing, radius, shadows; light and dark themes; contrast checked against WCAG 2.1 AA | | Tokens documented in Figma and exported to code; every text/background pair passes AA | POC |
| DS-02 | Shared component library for web and mobile: buttons, inputs, KPI tiles, status chips (icon + text), cards, tables, sidebar with count badges, bottom tabs, sheets, empty/error/offline states; mirrored for RTL | DS-01 | Components render identically in light, dark, English and Urdu | POC |
| DS-03 | Urdu typography test on low-end phones: compare Noto Nastaliq Urdu and Noto Naskh Arabic at small sizes outdoors | DS-01 | Typeface chosen from user readability results | POC |
| DS-04 | Web portal templates: shell with sidebar and header, dashboard (four KPI tiles, recent cards, collection-rate bar), list/detail, forms | DS-02 | Templates reviewed with supervisors and the manager | POC |
| DS-05 | Mobile role home designs and role switcher: resident, technician (POC); guard (Phase 2) | DS-02 | Prototype tasks completed by test users; guard design tested at a real gate before Phase 2 build | POC |
| DS-06 | WhatsApp conversation design for Phase 1: greeting, numbered menu with quick-reply buttons, free-text/photo/voice path, errors and fallbacks, in Urdu and English | | Flows reviewed by native speakers and pilot staff | P1 |
| DS-07 | WhatsApp menu extensions: visitor pass, my visitors, parcels, booking, requests, emergency contacts (Phase 2); dues, payments, receipts, polls (Phase 3); assistant, schedules, documents, board (Phase 4) | DS-06 | Flows tested with residents before each phase | P2a |
| DS-08 | Clickable prototypes tested with real users (residents, technicians, supervisors) before the build; repeated for guards before Phase 2 and accounts staff before Phase 3 | DS-04, DS-05 | Task success and time recorded; changes fed back into the designs | POC |
| DS-09 | Dark-mode and accessibility audit of the design system and key screens | DS-02 | No contrast failures; screen reader labels present | P1 |
| DS-10 | Original brand assets: logo, app icon, splash screen, store listing, WhatsApp business profile image | DS-01 | Assets approved; none copied from another product | P1 |
| DS-11 | Intercom and emergency designs: incoming call screen, guard calling flow, fallback states, emergency alert and acknowledge screen | DS-05 | Tested with guards and residents; alert cannot be missed on a locked screen | P2a |
| DS-12 | Booking calendar, request form and approval inbox designs | DS-04, DS-05 | Prototype tested with residents and office staff | P2b |
| DS-13 | Accounting, purchasing and payroll screen designs | DS-04 | Reviewed with the accountant | P3b |
| DS-14 | Polls, voting, tenants and governance screen designs | DS-04, DS-05 | Tested with committee members and residents | P3c |

## MA — Mobile app shell and shared services

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| MA-00 | Technology prototype for the single native mobile app (Android and iOS): React Native (Expo) vs Flutter vs separate native apps, tested on low-end Android and an iPhone for offline queue, camera QR scan, push, kiosk mode and cold-start speed | | Written decision; failing options listed | POC |
| MA-01 | App project setup: navigation, theming from the design tokens, English/Urdu with RTL, typed API client | MA-00, DS-02, FD-07 | App builds and runs on a test phone and tablet | POC |
| MA-02 | Role-based login and session: staff-style login for technicians and guards, OTP for residents (with ID-05), role claims, secure token storage, role switcher for people with several roles | MA-01, ID-04, ID-06 | A user with two roles switches role without logging out; sessions can be revoked | POC |
| MA-03 | Role modules loaded on demand; server rejects any call outside the user's role; guard role works only on enrolled devices | MA-02, ID-06 | A resident token cannot call guard or technician endpoints (tested); resident build runs without gate code | POC |
| MA-04 | Push notification registration and handling (FCM/APNs) with deep links to complaint, pass or bill | FD-16, MA-02 | Notification opens the right screen | P1 |
| MA-05 | App versioning: minimum-supported version check, forced-update screen, API backward-compatibility policy and tests | MA-01 | An old app version still works against the new API or shows a clear update prompt | P1 |
| MA-06 | Release pipeline: signed builds, app-store accounts, test tracks, staged rollout, over-the-air updates | MA-01, FD-03 | A build reaches testers and a staged release is rolled back in a rehearsal | P1 |
| MA-07 | Crash reporting and privacy-safe analytics | MA-01 | Crashes appear with app version and role | P1 |
| MA-08 | Secure on-device storage: tokens in the platform keystore, encrypted local database for the gate cache | MA-02 | A rooted-device review finds no plain-text secrets | P2a |
| MA-09 | Deep links from WhatsApp messages (pass, bill, complaint) | MA-04 | A WhatsApp link opens the right screen or the install page | P2a |
| MA-10 | Low-end device performance budget: install size, cold start, memory; tested on 3–4 low-end phones and a cheap tablet | MA-01 | Budget documented and met on the test devices | P1 |
| MA-11 | Native platform features: iOS CallKit and background modes, Android foreground services and permissions, permission prompts per role, store policy review (account deletion, privacy labels) | MA-01 | App passes store review checklist for calling, camera and notifications | P2a |

---

## FD — Foundation and DevOps

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| FD-01 | Repository layout: .NET modular solution, Python AI service, React web portal, React Native mobile app (technology confirmed by MA-00) and shared packages | | All projects build locally and in CI with one command | POC |
| FD-02 | Local dev environment in Docker: PostgreSQL 16 + PostGIS + pgvector, Redis, S3-compatible storage (MinIO) | FD-01 | New developer runs the full stack with one command | POC |
| FD-03 | CI pipeline: build, lint, unit tests, dependency and container scans | FD-01 | A pull request fails on test failure or high-severity CVE | POC |
| FD-04 | Domain event bus (in-process, persisted outbox) | FD-01 | Status changes publish events; subscribers retry on failure | POC |
| FD-05 | Background job infrastructure (Hangfire on PostgreSQL) with retries and failure alerting hook | FD-02 | A failing job retries, then lands in a visible failed queue | POC |
| FD-06 | Object storage service: signed short-lived URLs, EXIF stripped after location extraction | FD-02 | Files are never publicly reachable; stored copy has no EXIF | POC |
| FD-07 | OpenAPI contract and generated typed clients for all front ends | FD-01 | Front ends compile against the generated client | POC |
| FD-08 | Shared UI package for web and mobile: design tokens from DS-01, English/Urdu i18n, RTL layout, Urdu fonts, large touch targets | FD-01, DS-01 | A sample screen renders correctly in both directions on a low-end Android phone | POC |
| FD-09 | Observability baseline: OpenTelemetry, structured logs, Sentry, health endpoints | FD-02 | Traces and errors from CORE and AI appear in one place | POC |
| FD-10 | Terraform for dev, staging and production (separate accounts/projects), managed Postgres/Redis/storage, WAF, CDN | FD-03 | Environments are recreated from code | P1 |
| FD-11 | Secrets in a managed vault; no secrets in repo or images | FD-10 | Secret scan in CI passes; rotation documented | P1 |
| FD-12 | CD: auto-deploy to staging on merge, manual-approval blue-green to production, backward-compatible migrations first | FD-10 | A release deploys with no downtime in a staging rehearsal | P1 |
| FD-13 | Feature flags per organization (gate, billing, auto-routing, etc.) | ID-01 | A feature can be enabled for one organization only | P1 |
| FD-14 | Backups (daily, 30 days) and point-in-time restore; restore drill | FD-10 | Restore to a fresh environment meets RPO 15 min for financial/gate data and RTO 4 h | P1 |
| FD-15 | PDF generation service (reports now; bills and receipts later) with Urdu/English templates | FD-02, FD-08 | A report PDF renders Urdu text correctly | P1 |
| FD-16 | Push notification infrastructure (mobile push via FCM/APNs, web push for the portal) | FD-02 | A test push reaches the mobile app and the portal | P1 |
| FD-17 | Shared offline sync engine package: local SQLite queue, client-generated event ids, retry with backoff, failed-item handling | FD-08 | Queued items survive app restart; retries never create duplicates in a test API | P2a |
| FD-18 | Signed token library for QR passes (signing, expiry, key rotation, replay protection) | FD-01 | Token verifies offline with a public key; tampered or expired tokens fail | P2a |

## ID — Identity, tenancy and access

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| ID-01 | Multi-tenant schema: `organization_id` on every table, PostgreSQL row-level security, tenant context middleware | FD-02 | A query without tenant context returns nothing | POC |
| ID-02 | Tenant isolation integration tests (cross-tenant read/write attempts via API and SQL) | ID-01 | Suite proves no cross-organization access; runs on every PR | POC |
| ID-03 | Users, memberships and roles (a user may hold roles in several organizations) | ID-01 | A user can switch organization and sees only that data | POC |
| ID-04 | Staff authentication: password + two-factor (TOTP), session expiry and revoke | ID-03 | Revoked session is rejected immediately | POC |
| ID-05 | Resident phone-OTP login | ID-03, NT-03 | OTP is rate-limited, expires, single-use | P1 |
| ID-06 | Role-based permission matrix (resident, guard, technician, office, supervisor, accounts, committee, manager, platform admin) scoped by organization, department and gate; enforced in every API handler | ID-03 | Permission tests per role and endpoint pass | POC |
| ID-07 | Append-only audit log (DB rights deny update/delete); actor, action, entity, before/after, time, IP, device | FD-04, ID-01 | Updating or deleting an audit row fails; every state change appears | POC |
| ID-08 | Audit query API with filters | ID-07 | Manager can trace one complaint, one gate event or one payment end to end | POC |
| ID-09 | Per-IP and per-phone rate limiting on public endpoints and OTP requests | FD-01 | Burst traffic is throttled without affecting other tenants | P1 |
| ID-10 | Approval workflow service: configurable threshold, approver role, status, expiry, second-approver rule | ID-06 | Used by estimates (P1) and reversals/waivers (P3) without code changes | P1 |
| ID-11 | Gate device registration: enroll, guard PIN, short sessions, auto-lock, last-seen, remote revoke | ID-03, OR-13 | A revoked tablet cannot sync or log events | P2a |
| ID-12 | Field-level encryption and UI masking for ID numbers (e.g. CNIC) | ID-01 | Database dump shows ciphertext; UI shows masked values to unauthorised roles | P2a |

## OR — Organization setup and directory

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| OR-01 | Organization CRUD, branding, timezone, per-organization settings | ID-01 | Settings persist and are tenant-scoped | POC |
| OR-02 | Location hierarchy with templates: society (sector → street → block → house) and building (building → floor → unit); optional PostGIS geometry | OR-01 | Both templates creatable and editable; tree queries are fast | POC |
| OR-03 | Landmarks list per organization (searchable, linked to location nodes) | OR-02 | Landmarks appear in location pickers and AI prompts | P1 |
| OR-04 | Departments and teams with supervisors and members | OR-01, ID-03 | A team belongs to one department; membership changes are audited | POC |
| OR-05 | Categories (Urdu/English), department link, default priority, evidence rule (photo / checklist / note), checklist template; seeded templates for water, roads, streetlights, sewerage, sanitation, parks, building maintenance, parking | OR-04 | Admin can add a category without code changes | POC |
| OR-06 | House/unit entity (plot size, category, status) linked to the location tree | OR-02 | Every house has a unique location and can be the anchor for complaints, passes and bills | POC |
| OR-07 | Resident links: owner, tenant, family member, with from/to dates and history | OR-06, ID-03 | A house can have an owner and a tenant; moving out keeps history | POC |
| OR-08 | Vehicles in the directory (plate, type, house, sticker code, status) | OR-06 | Plate uniqueness enforced per organization; searchable | P1 |
| OR-09 | Daily help and worker records (person details, photo, masked ID, linked houses, allowed days and times, status) | OR-06 | Staff can be linked to several houses | P1 |
| OR-10 | CSV bulk import of houses, residents, vehicles and staff with validation report | OR-07, OR-08, OR-09 | Bad rows reported by line, good rows import; re-import is idempotent | P1 |
| OR-11 | Directory search and duplicate warnings (same phone, same plate, same person in two houses) | OR-07 | Search returns in under a second; duplicates flagged for staff review | P1 |
| OR-12 | Organization policy settings: unknown-number policy, resident auto-confirm days, estimate approval limit, overrun threshold, languages, AI toggles, escalation contacts | OR-01 | Each setting changes behaviour in the relevant module | P1 |
| OR-13 | Gates and gate rules: list of gates, visitor approval timeout and fallback (call, allow, deny), pass defaults, resident notification events | OR-01 | Rules readable by the sync API and the guard app | P2a |

## CP — Complaint core and intake

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| CP-01 | Complaint entity and reference number generator (UF-YYYY-NNNNN) | ID-01 | Numbers are unique and race-free under concurrent creation | POC |
| CP-02 | Status state machine (New → Under Review → Assigned → In Progress → Resolved pending verification → Closed; reject, reopen, merge) with role-based transitions | CP-01, ID-06 | Illegal transitions are rejected; each transition emits an event | POC |
| CP-03 | Complaint creation API used by all channels; returns reference number immediately and queues AI enrichment | CP-01 | Complaint is stored even if the AI service is down | POC |
| CP-04 | Media upload: file-type and size checks, image quality check, hash, resumable upload, thumbnails | FD-06 | A 5 MB photo uploads over throttled 3G and resumes after drop | POC |
| CP-05 | Missing-location handling: hold incomplete complaints and request street number or map pin | CP-03, OR-02 | Incomplete complaint is held with a request; reply completes it | POC |
| CP-06 | Office entry on behalf of a resident (phone, walk-in) | CP-03 | Entry records the staff member and the channel | POC |
| CP-07 | Reject-with-reason, reopen, and cannot-complete handling | CP-02 | Reason is mandatory and visible to the resident | POC |
| CP-08 | Unknown-number policy: match against directory; unverified queue / guest flag / OTP registration per organization | OR-07, OR-12 | Unmatched sender follows the configured policy; `reporter_verified` set | P1 |
| CP-09 | Resident status lookup by reference number plus verified phone or login | CP-01, ID-05 | A wrong phone cannot see another person's complaint | P1 |
| CP-10 | Resident confirmation required for closure; optional auto-confirm job (reminders, then close N days after sign-off, marked auto-confirmed) | CP-02, FD-05, OR-12 | Default configuration never closes without confirmation; auto-confirm is logged | P1 |
| CP-11 | Optional free-text asset/pole ID on complaints | CP-01 | Field is stored and searchable | P1 |

## WA — WhatsApp channel

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| WA-01 | Meta business verification, WhatsApp number, message-template approval for Phase 1 (non-code; start in discovery) | | Verified number and approved templates in Urdu and English | P1 |
| WA-02 | Webhook receiver: signature verification, idempotency, raw message storage | CP-03 | Replayed or forged webhooks are ignored | P1 |
| WA-03 | Inbound media and voice-note download into object storage | WA-02, CP-04 | Photos and audio attach to the right complaint | P1 |
| WA-04 | Complaint conversation state machine: guided flow, language detection, location/pin request, confirmation | WA-02, CP-05 | Complaint completed through chat only; resumes after a long pause | P1 |
| WA-05 | Status check by reference number inside chat | CP-09 | Returns current status in the user's language | P1 |
| WA-06 | Opt-in handling, per-phone rate limit, 24-hour window logic with template fallback | WA-02, NT-02 | No free-form message sent outside the window | P1 |
| WA-07 | Conversation scripts in Urdu and English, reviewed by a native speaker | DS-06 | Signed off by pilot-society staff | P1 |
| WA-08 | Contract tests with recorded webhook payloads | WA-02 | Run on every PR | P1 |
| WA-09 | Interactive button messages (approve/reject) and reply handling | WA-02 | A button tap reaches the right workflow once, even if tapped twice | P2a |
| WA-10 | Visitor approval conversation: request to resident, reply, timeout | WA-09, GT-06 | Walk-in approval round trip works and is logged | P2a |
| WA-11 | Template approvals for Phase 2 and 3 (visitor request, pass, parcel, bill, receipt, reminder), in Urdu and English | WA-01 | Approved templates available | P2a |
| WA-12 | Balance check and payment link conversation | WA-09, BL-19 | Resident gets balance and a payment link in chat | P3a |
| WA-13 | Facility booking conversation: choose facility, date and slot, confirm, pay or add to bill, cancel | WA-09, FB-02 | Booking completed in chat without double booking | P2b |
| WA-14 | Request conversation: choose request type, answer questions, upload documents, track status | WA-09, RQ-03 | Request submitted and tracked in chat | P2b |
| WA-15 | Emergency contacts in chat and receipt of emergency broadcasts with acknowledge button | WA-09, IC-08 | Contacts returned in one tap; acknowledgement recorded | P2a |
| WA-16 | Polls and voting conversation with one-vote enforcement | WA-09, TG-04 | Vote recorded once per eligible unit; receipt sent | P3c |

## AI — AI service

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| AI-01 | Model selection spike: compare candidate LLMs (e.g. through OpenRouter) on 100 labelled complaints and 50 photos; test Urdu voice transcription and multilingual embeddings separately | | Written recommendation with accuracy, cost and latency | POC |
| AI-02 | FastAPI service skeleton: versioned API, service-to-service auth, request/response schemas | FD-01 | CORE calls AI with typed contracts | POC |
| AI-03 | Provider abstraction for LLM, vision, speech-to-text and embeddings; pinned model and prompt versions | AI-02 | Switching provider is configuration only | POC |
| AI-04 | PII scrubber: strip names and phone numbers before any provider call | AI-02 | Test corpus shows no PII reaches the provider | POC |
| AI-05 | Complaint understanding: category, problem summary, location fields, language; JSON-schema output; per-organization few-shot examples from its categories | AI-03, AI-04 | Valid schema on every call; invalid output rejected and retried | POC |
| AI-06 | Prompt-injection guard: complaint text treated strictly as data | AI-05 | Injection test set cannot change output format or category logic | POC |
| AI-07 | Language detection for Urdu, Roman Urdu and English | AI-05 | Meets agreed accuracy on the golden set | POC |
| AI-08 | Photo interpretation: fixed visible-issue labels, "possible" wording, needs-more-info flag, never claims hidden faults | AI-03 | Output uses only allowed labels; hidden-fault claims fail tests | POC |
| AI-09 | Voice-note transcription (Urdu and English) | AI-03, AI-01 | Transcript returned with confidence; shown beside audio | P1 |
| AI-10 | Safety flag from text and image (live wire, sewage overflow, etc.) | AI-05, AI-08 | Safety terms always flagged in test set | P1 |
| AI-11 | Embeddings for text and images stored in pgvector | AI-03, FD-02 | Similar-complaint query returns in under a second | P1 |
| AI-12 | Basic before/after comparison (advisory) | AI-08 | Returns same-location likelihood and a change note | P1 |
| AI-13 | Usage metering and per-organization caps; cost per complaint | AI-03 | Over-cap organizations fall back to rules-only | P1 |
| AI-14 | Rules-only fallback mode when AI is down or over cap | AI-02 | Complaints still flow to the supervisor queue | P1 |
| AI-15 | CORE integration: background enrichment job with retries; store every suggestion with confidence and model version | CP-03, FD-05, AI-05 | Suggestion rows exist for each task; failure never loses the complaint | POC |
| AI-16 | Capture acceptance and override of each suggestion | AI-15 | Accuracy per category and language can be queried | POC |
| AI-17 | Auto-route gate: accuracy per category from live data; auto-routing can be enabled only above 90% | AI-16, RT-02 | Switch is blocked below threshold | P1 |

## RT — Routing, priority and SLA

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| RT-01 | Routing rule model and engine (category + location scope → team) | OR-04, OR-05 | Rules evaluated in order; most specific wins | POC |
| RT-02 | AI gap-filling, confidence threshold and per-category auto-route flag | RT-01, AI-15 | Auto-assign only above threshold and when enabled; otherwise queue | P1 |
| RT-03 | Supervisor review queue with AI suggestions pre-filled | RT-01 | Supervisor can edit any field; edits are logged | POC |
| RT-04 | Priority engine: category default, location risk, AI flags; levels routine / standard / prompt / urgent / safety | OR-05 | Safety-flagged complaints always require a person | P1 |
| RT-05 | Safety handling: emergency-services prompt to the resident and immediate supervisor alert | RT-04, NT-01 | Triggered within seconds in tests | P1 |
| RT-06 | SLA policy model and deadline calculation per category and priority | OR-05 | Deadlines respect organization timezone | P1 |
| RT-07 | Escalation scheduler: not assigned, not accepted, target passed, complete without evidence, resident dispute | RT-06, FD-05, CP-02 | Each rule fires once per level and records an escalation | P1 |
| RT-08 | Escalation levels and contacts; resolve on action | RT-07 | Escalations clear when the condition clears | P1 |
| RT-09 | Alert when an escalation job fails (within 5 minutes) | RT-07, FD-09 | Simulated failure pages the on-call | P1 |
| RT-10 | Rule administration API (routing, priority, SLA) with validation and audit | RT-01, RT-06 | Invalid rule combinations are rejected | P1 |
| RT-11 | Escalation rule: estimate waiting for manager approval too long | RT-07, JC-03 | Manager and supervisor notified at the configured delay | P1 |

## JB — Jobs and evidence

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| JB-01 | Job entity with job number; create from a complaint or incident, with instructions | CP-02 | Rework creates a new job under the same complaint; numbers unique | POC |
| JB-02 | Assign team and technician; accept; reassign | JB-01 | Technician sees only own jobs | POC |
| JB-03 | Progress updates: status, notes, optional GPS, media | JB-01 | Timeline shows every update with author | POC |
| JB-04 | Cannot-complete with reason, returned to supervisor | JB-03 | Complaint returns to review with the reason | POC |
| JB-05 | Evidence rules enforcement: required before/after photos, checklist or note by category | OR-05, CP-04 | Job cannot be marked complete without required evidence | POC |
| JB-06 | Resolved-pending-verification and supervisor sign-off or return | JB-05 | Return sends job to In Progress with a reason | POC |
| JB-07 | Resident confirmation or dispute (required for closure); dispute reopens | JB-06, CP-07, CP-10 | Closure blocked until confirmed or auto-confirmed; dispute escalates per RT-07 | P1 |
| JB-08 | Feedback: rating, confirmed-fixed, comment | JB-06 | Stored and shown on dashboard | P1 |
| JB-09 | Advisory AI before/after comparison shown to the reviewer | AI-12, JB-06 | Clearly labelled advisory; never blocks the supervisor | P1 |
| JB-10 | Chargeable flag on a job with target house | JB-01, OR-06 | Only supervisors can set it; change is audited | P1 |

## JC — Job costs, materials and stock

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| JC-01 | Material price list: items, units, rates, rate history, active flag | OR-01 | Rate changes keep history and do not alter past estimates | POC |
| JC-02 | Estimate: lines for materials (quantity × rate), labour hours and rate, transport; versions; total | JC-01, JB-01 | Technician or supervisor can create and revise an estimate before work | POC |
| JC-03 | Approval limit: estimates above the limit go to the manager and block work start until approved; reject returns for revision | JC-02, ID-10, OR-12 | A job over the limit cannot move to In Progress without approval; decision logged | P1 |
| JC-04 | Actual cost lines at completion: materials really used, labour, transport; final cost | JC-02, JB-06 | Completion requires actuals when the category requires costs | POC |
| JC-05 | Variance against estimate; overrun above threshold flagged for supervisor/manager review | JC-04, OR-12 | Variance shown on job and in reports; flagged jobs listed | P1 |
| JC-06 | Store stock ledger: receive, adjust, issue movements; current stock per item | JC-01 | Stock equals the sum of movements; every change has a user and reason | P1 |
| JC-07 | Issue stock to a job; return unused; reconcile issued vs used materials at completion | JC-06, JC-04 | Issued minus used minus returned differences are flagged | P1 |
| JC-08 | Low-stock thresholds and alerts | JC-06, NT-01 | Alert fires once when stock crosses the threshold | P1 |
| JC-09 | Physical stock count with adjustments (reason required) | JC-06 | Count creates adjustment movements and an audit record | P1 |
| JC-10 | Cost reports: spend per job, category and street; repeated repairs flagged by house/street and period | JC-04, OR-02 | Totals match job actuals; repeated-repair flag configurable | P1 |
| JC-11 | Chargeable repairs review queue for accounts (cost, evidence, resident) | JB-10, JC-04 | Accounts can approve, adjust or reject a charge; posting waits for billing (BL-20) | P1 |

## DP — Duplicate detection

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| DP-01 | Incident entity grouping complaints | CP-01 | A complaint belongs to at most one incident | P1 |
| DP-02 | Candidate search: geo radius, time window and category | DP-01, OR-02 | Returns candidates within the agreed radius and window | P1 |
| DP-03 | Similarity scoring from text and image embeddings | AI-11, DP-02 | Score and reason stored with each suggestion | P1 |
| DP-04 | Supervisor accept/reject; never auto-merge; all residents stay linked | DP-03 | Rejected pairs are not re-suggested | P1 |
| DP-05 | Updates on the incident notify every linked resident | DP-04, NT-06 | Three residents on one incident all get the completion message | P1 |
| DP-06 | Threshold tuning against labelled duplicate pairs | DP-03, QA-04 | Precision ≥ 90% on the labelled set | P1 |

## NT — Notifications and notices

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| NT-01 | Notification module: templates per language, channel preference, retries, delivery status | FD-05 | Failed send retries and is visible in a log | POC |
| NT-02 | WhatsApp Cloud API adapter with approved templates | NT-01, WA-01 | Delivered and read states recorded | P1 |
| NT-03 | SMS gateway adapter (OTP and fallback) | NT-01 | Fallback triggers when WhatsApp fails | P1 |
| NT-04 | Email adapter | NT-01 | Transactional email delivered | P1 |
| NT-05 | In-app notifications over SignalR (mobile push is added by MA-04 and FD-16 in Phase 1) | NT-01, FD-04 | Appear in the open app or portal without refresh | POC |
| NT-06 | Complaint event triggers: receipt, assignment, completion, escalation, rejection, confirmation request, estimate approval | NT-01, FD-04 | Each lifecycle event produces the right message in the resident's language | POC |
| NT-07 | Delivery webhooks and delivery-rate metrics | NT-02, NT-03 | Dashboard shows failure rate per channel | P1 |
| NT-08 | Notices and broadcast announcements: audience targeting (all, street, owners, tenants), schedule, read tracking | NT-01, OR-07 | Sender sees delivered and read counts per notice | P1 |
| NT-09 | Gate event notifications to residents (arrival, parcel, SOS to supervisors) per configuration | NT-01, GT-18 | Settings control which events notify | P2a |
| NT-10 | Billing notifications: bill issued, payment received, reminders | NT-01, BL-06 | Messages sent in the resident's language with caps | P3a |
| NT-11 | Poll and AGM notifications: new poll, reminder before deadline, result published, AGM notice and reminder | NT-01, TG-04 | Eligible residents notified once per event with reminders capped | P3c |

## DB — Dashboard and reports

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| DB-01 | Aggregation queries: by status, department, sector; overdue jobs; team workload; average resolution time; reopened complaints | CP-02, JB-01 | Results match a hand-checked sample | POC |
| DB-02 | Trend queries over time by category and sector | DB-01 | Weekly and monthly series available | P1 |
| DB-03 | Live updates over SignalR | FD-04, DB-01 | Counts change within seconds of an event | P1 |
| DB-04 | CSV and PDF export of lists and summaries | DB-01, FD-15 | Export respects the user's permissions | P1 |
| DB-05 | Scheduled monthly summary | DB-04, FD-05 | Delivered on schedule to configured recipients | P1 |
| DB-06 | Indexes and query tuning for dashboard load | DB-01 | Dashboard under 500 ms p95 with load-test data | P1 |
| DB-07 | Job spend, estimate-vs-actual, overrun and low-stock widgets | JC-05, JC-08 | Figures match the cost reports | P1 |
| DB-08 | Role-based dashboard composition (manager, supervisor, accounts, office) | DB-01 | Each role lands on its own view | P1 |
| DB-09 | Gate widgets: traffic by hour, inside now, incidents, SOS, pending approvals | GT-10 | Matches gate event data | P2a |
| DB-10 | Finance widgets: collections vs dues, aging, collection-rate bar | BL-17, BL-04 | Matches ledger and payments | P3a |
| DB-13 | Accounting widgets: expenses, income vs expense, budget vs actual | AC-12, AC-13 | Matches the accounting reports | P3b |
| DB-11 | Booking and request widgets: bookings today, utilisation, requests waiting, approvals overdue | FB-02, RQ-03 | Figures match booking and request records | P2b |
| DB-12 | Governance and tenant widgets: open polls and turnout, unverified tenants, expiring verifications | TG-02, TG-04 | Figures match records | P3c |

## GT — Gate and security (backend)

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| GT-01 | Gate event model and API: client-generated id, batch upload, device time plus server time, idempotent accept, out-of-order handling | OR-13, ID-11 | The same event sent ten times yields one record; late events keep device time | P2a |
| GT-02 | Sync download API: versioned delta of active passes, residents, vehicles, staff passes, blacklist, gate rules | GT-01 | Device gets only changes since its last version; size stays small | P2a |
| GT-03 | Visitor pre-approval: resident creates a pass with date range, entry limit and visitor details; signed QR plus OTP | FD-18, OR-07 | Pass is valid only within its window and entry limit | P2a |
| GT-04 | Pass delivery to the visitor (WhatsApp/SMS link) and revocation by the resident | GT-03, NT-02 | Revoked pass fails at the gate once the device syncs; online devices fail immediately | P2a |
| GT-05 | Pass validation rules (shared online and offline): window, count, status, house, blacklist | GT-03 | Same rule set passes the shared test vectors on server and device | P2a |
| GT-06 | Walk-in visitor flow: record name, phone, vehicle, photo, house; approval request to the resident; approve/reject; timeout policy; guard manual decision with reason | GT-01, OR-13 | Every outcome is recorded with who decided and how | P2a |
| GT-07 | Resident vehicle access: lookup by plate or sticker code (QR or RFID keyboard input), entry log, unknown-vehicle handling | OR-08, GT-01 | Registered vehicles pass in under 2 s; unknown ones go to the visitor flow | P2a |
| GT-08 | Contractor and worker passes: allowed days and times, validity, houses | OR-09, GT-03 | Entry outside the allowed window is flagged and needs a guard reason | P2a |
| GT-09 | Deliveries and parcels: log, notify resident, record collection | GT-01, NT-09 | Parcel shows as uncollected until the resident or an authorised person collects | P2a |
| GT-10 | Entry/exit pairing and presence ("who is inside now"); handling of missing exits | GT-01 | Presence list is correct after offline sync; stale entries flagged | P2a |
| GT-11 | Blacklist management and match alerts (person, phone, vehicle) | GT-02 | Match alerts the guard and the supervisor; entries have reason and owner | P2a |
| GT-12 | Guard shifts and attendance | ID-03 | Attendance shows who was at which gate and when | P2a |
| GT-13 | Patrol checkpoints (QR) and check-ins; missed-patrol alert | GT-12 | Missed patrol raises an alert to the supervisor | P2a |
| GT-14 | Incident reports with photos | GT-01 | Reports searchable by gate, date and type | P2a |
| GT-15 | SOS/panic alert: raise from the guard app, fan-out to supervisors (push, WhatsApp/SMS), acknowledgement, escalation if unacknowledged | NT-01, GT-12 | Unacknowledged SOS escalates within the configured time | P2a |
| GT-16 | Gate reports and search: visitor history, vehicle log, parcels, export; retention jobs for visitor photos and ID details | GT-01, FD-05 | Retention deletes expired data and logs the deletion | P2a |
| GT-17 | Resident-facing gate API: my visitors, passes, vehicles, parcels, approvals | GT-03, GT-06 | A resident sees only their own house's data | P2a |
| GT-18 | Event notification rules per resident (arrival, parcel, staff entry) | GT-01 | Residents can switch events on or off | P2a |
| GT-19 | Parking: slots and zones, resident slot allotment per house with history, sticker issue | OR-06, OR-08 | Each house shows its slots; one slot cannot be allotted twice for the same period | P2a |
| GT-20 | Visitor parking sessions: assign a visitor slot at entry, time limit, overstay alert, occupancy count | GT-19, GT-01 | Guard sees free visitor slots; overstay notifies the guard and the resident | P2a |
| GT-21 | Daily help registry: profile, photo, masked ID, linked houses, allowed hours, status, optional verification record | OR-09, ID-12 | Help can be linked to several houses and removed by the office | P2a |
| GT-22 | Daily help clock in/out at the gate (card/QR or guard selection) with allowed-hours check | GT-21, GT-01 | Entries outside allowed hours are flagged with a reason | P2a |
| GT-23 | Resident alerts when daily help arrives or leaves, configurable per house | GT-22, NT-09 | Alerts respect each household's settings | P2a |
| GT-24 | Daily help ratings by residents with visibility rules (office and the rater only by default) | GT-21 | A resident rates only help linked to their house | P2a |

## IC — Intercom and emergencies

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| IC-01 | Calling provider spike: compare 2–3 WebRTC providers on Pakistani mobile and Wi-Fi networks; test iOS CallKit and Android background and locked-screen behaviour, audio quality, setup time and cost per minute | MA-00 | Written decision with measured connect rate, ring time and cost | P2a |
| IC-02 | Call session API: start, ring, answer, decline, end, timeout; signalling and tokens through the provider; call log without recording | IC-01, GT-01 | A guard-to-resident call completes in a test environment and is logged | P2a |
| IC-03 | Resident incoming-call experience: native call screen (CallKit / Android ConnectionService), works when locked or in the background, accept or decline | IC-02, MA-04, MA-11 | Rings on a locked iPhone and a low-end Android within 5 s | P2a |
| IC-04 | Guard calling experience: call button per house, call status, retry, history | IC-02, GA-02 | Guard sees ringing, answered, declined or no answer clearly | P2a |
| IC-05 | Fallback chain per organization: another household member, WhatsApp approval, masked phone call through a telephony provider, or the office | IC-02, WA-10, OR-13 | A no-answer call falls through the configured steps within 20 s | P2a |
| IC-06 | Privacy: no phone numbers shown to guards or residents; call metadata retention setting; no recording by default | IC-02 | Review confirms no numbers in the app, logs or screens | P2a |
| IC-07 | Intercom analytics: connect rate, ring time, fallback rate per gate and network type | IC-02, DB-01 | Dashboard values match call logs | P2a |
| IC-08 | Emergency broadcast service: types (fire, medical, other), role-limited trigger with confirm step, audience (all, street, house group), push, WhatsApp and SMS on a high-priority path, acknowledgement tracking, drill mode | NT-01, NT-02, NT-03, MA-04 | A drill reaches at least 95% of test devices within 60 s and records acknowledgements | P2a |
| IC-09 | Emergency contact list: categories (fire brigade, ambulance, police, hospital, society), order, offline copy in the app | OR-01, MA-02 | List opens without internet | P2a |
| IC-10 | Emergency UI: trigger in the guard app and portal; resident alert screen with acknowledge; contacts one tap away | IC-08, GA-02, WP-01 | Alert is full screen and cannot be missed on a locked screen | P2a |
| IC-11 | Reach monitoring and drill report: delivered, read, acknowledged, failed by channel | IC-08 | Report produced after each drill | P2a |

## BL — Billing, payments and expenses (backend)

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| BL-01 | Charge heads: recurring and one-off, basis (flat, by plot size, by category) | OR-06 | Heads configured per organization without code | P3a |
| BL-02 | Rate rules by plot size or category with validity periods | BL-01 | A rate change applies from its effective date only | P3a |
| BL-03 | Late-fee and fine rules (percentage or fixed, grace days, cap) | BL-01 | Rules unit-tested with edge cases | P3a |
| BL-04 | Ledger service: append-only entries (debit/credit), per-house balance, reversals | ID-01 | No update or delete path exists; balance equals the sum of entries | P3a |
| BL-05 | Opening balances import with checks against the accountant's totals | BL-04, OR-10 | Imported totals match a control total or the import is rejected | P3a |
| BL-06 | Monthly bill generation: scheduled run, preview, idempotent per house and period, publish, period lock; one-off levy bills; accepts bill lines from bookings and requests | BL-02, BL-04, FD-05 | Running twice never double-bills; preview totals match published totals | P3a |
| BL-07 | Bill PDF in Urdu/English and delivery (WhatsApp, app, print) | BL-06, FD-15 | Bill reads correctly on mobile and paper | P3a |
| BL-08 | Late-fee run by rule with preview; waivers need approval | BL-03, BL-04, ID-10 | Fees match rules; waiver recorded with approver | P3a |
| BL-09 | Payment recording at the office: cash, cheque, bank transfer; cheque bounce reversal | BL-04 | Each payment gets a receipt number; bounced cheque posts a reversal | P3a |
| BL-10 | Payment allocation: oldest dues first, partial and advance payments | BL-09 | Allocation rules tested; advance credit applies to later bills | P3a |
| BL-11 | JazzCash integration (sandbox then live) | BL-04 | A test payment reaches the ledger exactly once | P3a |
| BL-12 | Easypaisa integration or local aggregator (sandbox then live) | BL-04 | A test payment reaches the ledger exactly once | P3a |
| BL-13 | Payment webhook handling: signature verification, idempotency, status transitions, unknown-reference handling | BL-11 | Duplicate or forged webhooks do not create entries; unknown references queued for review | P3a |
| BL-14 | Bank transfer reconciliation: reference matching and statement import | BL-09 | Matching transfers auto-match; others go to a manual queue | P3a |
| BL-15 | Daily reconciliation report and mismatch alerts (provider vs ledger) | BL-13, BL-14, FD-09 | A seeded mismatch alerts accounts within the day | P3a |
| BL-16 | Receipts: PDF and WhatsApp delivery | BL-09, FD-15, NT-10 | Receipt number sequential and unique per organization | P3a |
| BL-17 | Defaulter list with aging buckets | BL-04 | Matches ledger balances | P3a |
| BL-18 | Automatic reminders: schedule, channel, message caps, stop on payment | BL-17, NT-10 | No reminder after payment; frequency cap respected | P3a |
| BL-19 | Resident balance and history API | BL-04 | A resident sees only their own house | P3a |
| BL-20 | Chargeable repairs posting: accounts-approved job cost becomes a bill line linked to the job; resident sees itemised charge; dispute flag | JC-11, BL-06 | A charge cannot post twice for the same job; dispute pauses reminders for that line | P3a |
| BL-24 | Maker-checker for reversals and waivers over limit | ID-10, BL-04 | Large reversal needs a second approver | P3a |
| BL-26 | Raast integration (instant payment through a participant bank or aggregator; sandbox then live) | BL-04 | A test payment reaches the ledger exactly once | P3a |
| BL-27 | Post facility booking and request fees as bill lines; unpaid fees follow the same reminder rules | FB-04, RQ-05, BL-06 | A fee appears once on the next bill and clears on payment | P3a |

## FB — Facility booking

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| FB-01 | Facility setup: facilities (hall, gym, ground, marquee), capacity, opening hours, slot rules and buffers, fees and deposits, approval flag, maintenance blocks | OR-01 | Admin configures a new facility without code | P2b |
| FB-02 | Availability and booking API with a database exclusion constraint on (facility, time range) | FB-01, ID-01 | 100 concurrent attempts for one slot create exactly one booking | P2b |
| FB-03 | Eligibility rules: maximum bookings per house per month, advance-booking window, and a dues check that switches on when billing is enabled | FB-02, OR-12 | Rules enforced and explained to the resident | P2b |
| FB-04 | Fees and deposits: calculate; collect online or at the office; add to the bill once billing is on (BL-27); deposit refund | FB-02 | Each booking shows its fee status; refunds are logged | P2b |
| FB-05 | Optional approval for selected facilities through the workflow engine | FB-02, RQ-01 | Booking stays pending until approved; slot is held meanwhile | P2b |
| FB-06 | Cancellation, rescheduling and no-show rules with refunds | FB-04 | Rules applied automatically and shown before confirming | P2b |
| FB-07 | Notifications and reminders: confirmation, reminder, cancellation | FB-02, NT-01 | Messages in the resident's language | P2b |
| FB-08 | Usage reports: utilisation, revenue, cancellations; export | FB-02, DB-04 | Figures match booking records | P2b |

## RQ — Requests, NOCs and workflow

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| RQ-01 | Workflow engine: ordered or parallel approval steps, approver by role or person, conditions, timeouts, send-back, delegation | ID-10, ID-06 | Estimates, requests and bookings run from configuration; every step is audited | P2b |
| RQ-02 | Request type builder: form fields, required documents, fee, checks, approval chain, target time; versioned | RQ-01, OR-12 | Admin creates a new request type without code | P2b |
| RQ-03 | Request submission API with attachments and validation; status tracking | RQ-02, CP-04 | Resident submits, tracks and responds to information requests | P2b |
| RQ-04 | Pluggable checks: dues clear (when billing is on), tenant verified (when Phase 3c is on), no open request of the same type | RQ-02 | Check results shown to approvers | P2b |
| RQ-05 | Request fees: collect online or at the office; add to the bill once billing is on (BL-27); receipt | RQ-03 | Fee status visible on the request | P2b |
| RQ-06 | NOC and permit PDF with number, QR code and a public verification page | RQ-03, FD-15 | A scanned code shows valid, expired or revoked | P2b |
| RQ-07 | Moving-goods request creates a time-limited gate pass | RQ-03, GT-03 | Approved request appears on guard devices after sync | P2b |
| RQ-08 | Move-in and move-out: update resident links, notify the gate, final-dues check at move-out | RQ-03, OR-07 | Directory updated on approval | P2b |
| RQ-09 | House transfer NOC: ownership change workflow, checks, NOC issue, directory update | RQ-03, RQ-06, OR-07 | Previous owner ends and new owner starts on approval, with history kept | P2b |
| RQ-10 | Construction and renovation approval: rules and bylaw text shown, documents, optional inspection step, deposit or fine | RQ-03 | Approval, rejection or conditions recorded and visible to the resident | P2b |
| RQ-11 | Escalations and reminders for waiting requests | RQ-01, RT-07 | Overdue steps notify the approver and the manager | P2b |
| RQ-12 | Seed templates for the four request types | RQ-02 | A new organization gets working defaults | P2b |

## AC — Full accounting

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| AC-01 | Chart of accounts: default template for societies, hierarchy, account types, accountant review | ID-01 | Accountant approves the template; accounts can be added without code | P3b |
| AC-02 | Journal engine: balanced double-entry journals, append-only, reversal, source links, fiscal periods | AC-01, BL-04 | An unbalanced journal is rejected; no update or delete path exists | P3b |
| AC-03 | Automatic postings from billing, payments, late fees and reversals; house ledger as receivables sub-ledger | AC-02, BL-06, BL-09 | Ledger totals equal the receivable account at all times | P3b |
| AC-04 | Automatic postings from job actual costs and store stock movements without duplicates | AC-02, JC-04, JC-06 | A job's cost appears once in the books | P3b |
| AC-05 | Opening balances for all accounts with control totals | AC-02, BL-05 | Imported opening trial balance balances or is rejected | P3b |
| AC-06 | Vendor master with encrypted bank details, documents and status | ID-12, OR-01 | Bank details visible only to authorised roles | P3b |
| AC-07 | Purchase requests and orders: create, approve through the workflow, send, status, amount limits | RQ-01, AC-06 | Orders above the limit need the configured approvers | P3b |
| AC-08 | Goods receipt into store stock, with partial receipts | AC-07, JC-06 | Received quantities appear in stock and on the order | P3b |
| AC-09 | Vendor bills: capture, match to order and receipt, approval | AC-07, AC-08 | Mismatches are flagged before approval | P3b |
| AC-10 | Vendor payments: single and batch, approval, method, reference, journal, segregation of duties | AC-09, ID-10 | The requester, approver and payer must differ above the limit | P3b |
| AC-11 | Direct and petty expenses with attachments and approval limits | AC-02 | Expense posts a journal and shows in reports | P3b |
| AC-12 | Reports: trial balance, general ledger, income and expense, balance sheet, receipts and payments, receivable and payable ageing, budget vs actual; PDF and Excel; audit-ready references | AC-03, AC-04, AC-11 | Totals match the ledger and the accountant's check | P3b |
| AC-13 | Budgets: yearly budget per account and period, import, budget vs actual, overspend alerts | AC-01 | Alert fires when spend passes the threshold | P3b |
| AC-14 | Period close checklist and lock; changes only by reversal in an open period | AC-02 | Closed period rejects postings | P3b |
| AC-15 | Export to accounting software in the format chosen with the accountant, with an import test | AC-12 | Accountant imports the file without manual fixes | P3b |
| AC-16 | Access controls: accounts full, manager and committee read-only views, all actions audited | ID-06, ID-07 | Role tests pass | P3b |

## PR — Staff payroll

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| PR-01 | Employee records for guards, sweepers and maintenance workers: salary structure, payment method, encrypted bank details, status | ID-12, OR-04 | Employees can be created and linked to a guard login where relevant | P3b |
| PR-02 | Attendance: from guard shifts, manual or QR entry for other staff, leave types, corrections with approval | GT-12, PR-01 | Attendance totals match source records | P3b |
| PR-03 | Pay rules: overtime, absence, holidays calendar, advances, deductions and fines | PR-01 | Rules unit-tested with edge cases | P3b |
| PR-04 | Payroll run: preview, calculation, maker-checker approval, lock | PR-02, PR-03, ID-10 | Approved run cannot be edited; corrections go to the next run | P3b |
| PR-05 | Payslips in Urdu and English (PDF) with delivery | PR-04, FD-15 | Payslips match the approved run | P3b |
| PR-06 | Salary payment recording and bank payment file export; journals posted to accounting | PR-04, AC-02 | Each payment posts once and reconciles | P3b |
| PR-07 | Payroll reports: register, advances outstanding, attendance summary; export | PR-04 | Figures match the runs | P3b |
| PR-08 | Salary privacy controls: visible to accounts and manager only; field-level encryption | ID-06, ID-12 | Role tests confirm other roles cannot read salaries | P3b |
| PR-09 | Statutory deduction hooks configured only where the accountant or labour adviser confirms they are required | SC-11 | Hooks present but off by default | P3b |

## TG — Tenants and governance

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| TG-01 | Tenant registration: details, documents, police verification status (not submitted, pending, verified, expired), dates; encrypted documents | OR-07, ID-12 | Documents visible only to office, manager and the house owner | P3c |
| TG-02 | Expiry reminders to owners and office; list of unverified tenants; configurable rules | TG-01, FD-05 | Reminders sent at the configured intervals and stop on renewal | P3c |
| TG-03 | Link tenant records to move-in and move-out requests | TG-01, RQ-08 | Move-out ends the tenant record and keeps history | P3c |
| TG-04 | Poll engine: options, eligibility (per house or owner), deadline, secret or open ballot, one vote per eligible unit | OR-07, ID-06 | Duplicate and ineligible votes are rejected; secret ballots store no voter link | P3c |
| TG-05 | Voting in the app and through WhatsApp links; vote receipt; change-vote policy | TG-04, MA-02 | Vote recorded once with a receipt | P3c |
| TG-06 | Results and audit: counts, turnout, verification report without revealing ballots; PDF | TG-04, FD-15 | Result recomputed from records matches the published result | P3c |
| TG-07 | AGM notices with agenda, attachments and acknowledgement | NT-08 | Delivery and acknowledgement counts visible | P3c |
| TG-08 | Meeting minutes: record, attendance, attachments, approve and publish, versions | TG-07 | Published minutes are read-only; changes create a new version | P3c |
| TG-09 | Decisions register: searchable, linked to meetings and polls, status | TG-08, TG-06 | A decision can be traced to its meeting or poll | P3c |
| TG-10 | Committee members and terms; permissions for committee tools | ID-06, ID-03 | Committee role gains only the intended screens | P3c |

## WP — Office and admin web portal

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| WP-01 | App shell: login with 2FA, role-based sidebar with count badges, header search, English/Urdu with RTL, built from DS-04 templates | FD-08, ID-04, DS-04 | Each role sees only its menus | POC |
| WP-02 | Complaint list with filters (status, department, sector, priority, date) and search | CP-02 | Filters combine; handles thousands of rows | POC |
| WP-03 | Complaint detail: timeline, media, AI suggestions with edit/accept, audit trail | AI-16, ID-08 | Every AI field editable; overrides logged | POC |
| WP-04 | Supervisor queue and routing/assignment screen | RT-03 | Assign in two clicks from the queue | POC |
| WP-05 | Office entry form for walk-in and phone complaints | CP-06 | Staff can attach photos and pick location | POC |
| WP-06 | Job create/assign and job tracking views | JB-02 | Job status visible on the complaint | POC |
| WP-07 | Evidence review: before/after side by side, sign off or return with reason | JB-06 | Supervisor signs off or returns in one screen; the advisory AI comparison (JB-09) appears in Phase 1 | POC |
| WP-08 | Estimate editor using the price list, with totals; actual cost entry and variance view | JC-02, JC-04 | Supervisor builds an estimate in a few clicks and sees estimate vs actual; overrun flags (JC-05) arrive in Phase 1 | POC |
| WP-09 | Manager approval inbox for estimates above the limit | JC-03 | Approve or reject with comment from one screen | P1 |
| WP-10 | Duplicate review screen (side-by-side, accept/reject grouping) | DP-04 | Linked complaints shown with similarity reasons | P1 |
| WP-11 | Dashboards: KPI tiles, recent-activity cards, summary and progress bars (complaints, workload, job spend); trends and role-specific views are added in Phase 1 with DB-02 and DB-08 | DB-01 | Matches API figures | POC |
| WP-12 | Configuration: organization settings, location hierarchy, landmarks | OR-01, OR-02 | Admin builds a society structure without help | P1 |
| WP-13 | Configuration: departments, teams, categories, evidence rules, checklists | OR-05 | Changes take effect on next complaint | POC |
| WP-14 | Configuration: routing, priority and SLA rules; escalation contacts; approval limit; auto-route toggle (gated) | RT-10, AI-17, OR-12 | Invalid rules shown with clear messages | P1 |
| WP-15 | Directory screens: houses, residents, vehicles, staff; search; import wizard with error report | OR-10, OR-11 | Office staff maintain the directory without developer help | P1 |
| WP-16 | Audit log viewer | ID-08 | Filter by actor, entity, date | POC |
| WP-17 | Stock and price list screens: items, receive, issue, counts, low-stock list | JC-06, JC-09 | Storekeeper completes a stock count in the app | P1 |
| WP-18 | Cost reports and chargeable repairs queue | JC-10, JC-11 | Export respects permissions; accounts can approve a charge | P1 |
| WP-19 | Notices: composer, audience, schedule, read statistics | NT-08 | Sender sees who read | P1 |
| WP-20 | Reports: export and scheduled summary settings | DB-04, DB-05 | Export downloads for the current filter | P1 |
| WP-21 | Accessibility pass (WCAG 2.1 AA) and Urdu typography review for the portal | WP-01 | Axe clean; native-speaker review done | P1 |
| WP-22 | Gate administration: gates and rules, devices, pass search, visitor and vehicle log, parcels, parking, daily help, intercom log, blacklist, guard shifts and attendance, patrols, incidents, SOS and emergency console | GT-01 to GT-24, IC-02, IC-08 | Office can run a day of gate operations without database access | P2a |
| WP-23 | Billing: charge heads, rates, late-fee rules, bill run preview and publish, per-house ledger, payments entry, receipts, defaulters, reminder settings | BL-01 to BL-18 | Accounts completes a full billing cycle in the portal | P3a |
| WP-24 | Facility management screens: facilities and rules, calendar, bookings, approvals, blocks, usage reports | FB-01 to FB-08 | Office manages a hall booking from request to payment | P2b |
| WP-25 | Request processing screens: request type builder, approver inbox, send-back, NOC issue, request history | RQ-02, RQ-03 | Approver completes a step in one screen | P2b |
| WP-26 | Accounting screens: chart of accounts, journals, periods, budgets, reports, export | AC-01 to AC-14 | Accountant runs a month-end close in the portal | P3b |
| WP-27 | Purchasing and vendor screens: vendors, purchase orders, goods receipt, vendor bills, vendor payments | AC-06 to AC-11 | A purchase goes from request to payment with approvals | P3b |
| WP-28 | Payroll screens: employees, salary structures, attendance, payroll run preview and approval, payslips | PR-01 to PR-07 | Accounts runs and approves a payroll in the portal | P3b |
| WP-29 | Tenant and governance screens: tenant register and expiry list, polls, AGM notices, minutes, decisions, committee list | TG-01 to TG-10 | Committee runs a poll and records minutes in the portal | P3c |

## RA — Mobile app: resident role

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| RA-01 | Resident role home (quick-action grid, recent complaints) and bottom tabs, English/Urdu with RTL, large touch targets | MA-01, DS-05 | Runs smoothly on a low-end Android phone and on 3G | POC |
| RA-02 | Complaint form: text, photo capture with on-device compression (≤ 500 KB), voice-note recording, map pin and landmark picker | CP-03, CP-04, MA-01 | Complaint submitted on throttled 3G; resumable upload | POC |
| RA-03 | Receipt screen with reference number | CP-03 | Shown immediately; retained offline | POC |
| RA-04 | Phone-OTP login | ID-05 | Works with local SMS delivery | P1 |
| RA-05 | My complaints and status timeline; lookup by reference | CP-09 | Updates live when status changes | P1 |
| RA-06 | Confirm or dispute completion; feedback rating | JB-07, JB-08 | Dispute reopens the job | P1 |
| RA-07 | Ask-for-missing-info replies (location, extra photo) | CP-05 | Resident answers the request in-app | P1 |
| RA-08 | Emergency prompt on life-safety keywords | RT-05 | Prompt appears before submit | P1 |
| RA-09 | Notices feed with read tracking | NT-08 | Read status reaches the sender | P1 |
| RA-10 | Visitors: pre-approve a guest and share the pass; approve or reject walk-ins; my visitors and history; revoke a pass | GT-17, GT-03 | Resident completes each action in under four taps | P2a |
| RA-11 | Vehicles and staff passes: view, request add, set allowed times | GT-17, GT-08 | Requests go to the office for approval | P2a |
| RA-12 | Parcel notifications and collection history; notification preferences | GT-09, GT-18 | Preferences saved and respected | P2a |
| RA-13 | Dues: balance, bills, history, pay online, receipts | BL-19, BL-11, BL-16 | Online payment reaches the ledger and shows a receipt | P3a |
| RA-14 | Chargeable repair view with job details and dispute | BL-20 | Dispute reaches accounts | P3a |
| RA-15 | Daily help: linked help, arrival alerts, ratings and settings | GT-23, GT-24 | Resident adjusts alerts and rates help in a few taps | P2a |
| RA-16 | Parking: my slots, visitor parking information | GT-19, GT-20 | Resident sees allotted slots and visitor parking status | P2a |
| RA-17 | Book a facility: browse, calendar, choose slot, pay or add to bill, cancel | FB-02, FB-04 | Resident books a slot in under a minute and sees conflicts at once | P2b |
| RA-18 | Requests: choose type, fill form, upload documents, track steps, answer information requests, pay fee | RQ-03, RQ-05 | Resident submits and tracks a request on a low-end phone | P2b |
| RA-19 | Polls and voting with clear eligibility and deadline; results view | TG-04, TG-06 | Resident votes once and sees a receipt | P3c |
| RA-20 | Tenant registration for owners: add tenant, upload documents, see verification status and expiry | TG-01 | Owner receives and acts on expiry reminders | P3c |
| RA-21 | AGM notices, minutes and decisions viewer | TG-07, TG-08, TG-09 | Resident reads notices and decisions in the app | P3c |

## TA — Mobile app: technician role

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| TA-01 | Technician role home (today's jobs with due-time chips) and bottom tabs, Urdu-first UI with icons, large buttons | MA-01, DS-05 | Usable by a first-time user after the 1-hour training | POC |
| TA-02 | My jobs list and job detail (instructions, location, resident photos) | JB-02 | Only own jobs visible | POC |
| TA-03 | Accept job and update progress with notes | JB-03 | Works on 3G; status reflected for supervisor | POC |
| TA-04 | Photo capture (before/after) with compression and resumable upload | CP-04, JB-05 | Stage tagged correctly; upload survives signal loss | POC |
| TA-05 | Cannot-complete with reason | JB-04 | Reason mandatory | POC |
| TA-06 | Estimate entry from the price list and actual materials used at completion | JC-02, JC-04 | Technician completes both with minimal typing | POC |
| TA-07 | Checklist completion for categories that need it | JB-05 | Required items block completion | P1 |
| TA-08 | Voice input for notes | AI-09 | Transcript editable before saving | P1 |
| TA-09 | Stock view and request for materials | JC-06, JC-07 | Request reaches the storekeeper; issued items appear on the job | P1 |
| TA-10 | Offline job list, queued uploads and GPS check-in at site (reuses the gate sync engine) | FD-17, TA-02 | Job list works offline; queued updates sync once; check-in stores coordinates | P4a |

## GA — Mobile app: guard (gate) role

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| GA-01 | Guard role home with large action buttons and always-visible SOS; device enrollment, guard PIN login, auto-lock, English/Urdu, minimal typing | MA-01, MA-03, DS-05, ID-11 | A guard signs in with a PIN; a revoked device is locked on next sync | P2a |
| GA-02 | Local database and sync using the shared engine; initial and delta cache download | FD-17, GT-02 | App starts and works with airplane mode on after first sync | P2a |
| GA-03 | Scan QR pass with the camera or enter OTP; clear allow/deny result with reason | GA-02, FD-18, GT-05 | Validation under 2 s offline; matches the server's test vectors | P2a |
| GA-04 | Walk-in visitor form (name, phone, vehicle, photo, house); send approval; waiting screen with timeout; manual decision with reason; offline fallback | GA-02, GT-06 | Guard completes a walk-in in under a minute; offline path records a manual decision | P2a |
| GA-05 | Resident and vehicle quick entry: plate search, sticker scan (QR or RFID keyboard input) | GA-02, GT-07 | A registered car passes in two taps | P2a |
| GA-06 | Staff and contractor entry with allowed-time warnings | GA-02, GT-08 | Out-of-window entry requires a reason | P2a |
| GA-07 | Parcel logging and collection record | GA-02, GT-09 | Resident is notified; collection closes the parcel | P2a |
| GA-08 | Exit logging and "who is inside now" list | GA-02, GT-10 | List matches the server after sync | P2a |
| GA-09 | Blacklist alert screen | GA-02, GT-11 | Alert is unmissable and requires acknowledgement | P2a |
| GA-10 | Shift start/end, attendance and patrol QR scans | GA-02, GT-12, GT-13 | Patrol scans recorded offline and synced | P2a |
| GA-11 | Incident report with photo and SOS button | GA-02, GT-14, GT-15 | SOS sends immediately when online and queues with a loud local alert when offline | P2a |
| GA-12 | Sync status: offline banner, pending count, failed-event handling and manual retry | GA-02 | Guard always sees whether events are safely uploaded | P2a |
| GA-13 | Kiosk-mode and device hardening guide for tablets and phones | GA-01 | Guide followed on a test tablet; app cannot be left by the guard | P2a |
| GA-14 | Field test on real tablets and a 24-hour offline soak test | GA-03 to GA-12, GA-15, GA-16 | No lost or duplicated events after reconnect | P2a |
| GA-15 | Daily help clock in/out screen with allowed-hours warning | GA-02, GT-22 | Clock in or out takes two taps and works offline | P2a |
| GA-16 | Parking: free visitor slots, assign slot at entry, overstay list | GA-02, GT-20 | Guard assigns a slot without leaving the entry screen | P2a |

## PA — Platform admin area

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| PA-01 | Organization onboarding: create tenant, seed templates, first admin | OR-01, OR-05 | New organization usable in under an hour | P1 |
| PA-02 | Feature-flag management per organization (gate, billing, auto-routing) | FD-13 | Flip a flag without deploying | P1 |
| PA-03 | AI quality dashboard: accuracy and override rate per organization, category and language | AI-16 | Matches the AI suggestion log | P1 |
| PA-04 | AI cost and usage dashboard with caps | AI-13 | Cost per complaint visible per organization | P1 |
| PA-05 | Support access (read-only, time-limited, fully audited) | ID-07 | Every support session appears in the audit log | P1 |
| PA-06 | Gate device fleet: enrolled tablets, app version, last sync, remote revoke | ID-11 | Support sees stale devices | P2a |
| PA-07 | Gate onboarding templates (default rules, categories of passes) | OR-13 | New society gets sensible gate defaults | P2a |
| PA-08 | Billing onboarding templates and payment health overview across organizations | BL-15 | Support sees failing webhooks and reconciliation gaps | P3a |

## SC — Security and compliance

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| SC-01 | Encryption in transit (TLS 1.2+) and at rest for database, backups and storage | FD-10 | Verified by scan | P1 |
| SC-02 | OWASP ASVS Level 2 checklist and remediation | | All applicable items closed or accepted | P1 |
| SC-03 | Retention policy jobs for complaint media (e.g. 24 months) | FD-05, CP-04 | Configurable per organization; deletions audited | P1 |
| SC-04 | Resident data export and deletion on request, respecting financial record-keeping | ID-03 | Request completed and logged; financial entries retained as required | P1 |
| SC-05 | Privacy notice and terms in Urdu and English; data processing agreement template | | Reviewed by legal advisor | P1 |
| SC-06 | AI provider contract check: no training on customer data; hosting region documented | | Written confirmation on file | P1 |
| SC-07 | External penetration test and fixes | SC-02 | No open critical or high findings | P1 |
| SC-08 | WAF and rate-limit configuration for public endpoints | FD-10 | Abuse test is blocked | P1 |
| SC-09 | Visitor data policy: notice for visitors and residents, retention periods, society bylaw template | | Reviewed by legal advisor; retention matches GT-16 | P2a |
| SC-10 | Gate threat review: lost tablet, PIN guessing, pass replay, forged QR, guard misuse | GA-01, FD-18 | Mitigations implemented and tested | P2a |
| SC-11 | Legal and accounting review: late fees, fines, receipts and any tax obligations | | Written guidance applied to BL rules | P3a |
| SC-12 | Payment security review: no card data stored, webhook secrets, reconciliation controls, segregation of duties | BL-13, BL-24 | Checklist signed off | P3a |
| SC-13 | Second external penetration test covering gate devices and payments | SC-10, SC-12 | No open critical or high findings | P3a |
| SC-14 | Intercom privacy review: no numbers exposed, call metadata retention, permissions | IC-06 | Review signed off | P2a |
| SC-15 | Payroll and salary privacy review: access limits, field encryption, payslip delivery | PR-08 | Review signed off | P3b |
| SC-16 | Accounting controls review with the accountant: chart of accounts, journal rules, segregation of duties, audit-readiness | AC-02, AC-10 | Accountant signs off the controls | P3b |
| SC-17 | Third external penetration test covering accounting, payroll and the mobile app | SC-15, SC-16 | No open critical or high findings | P3b |
| SC-18 | Tenant verification legal review: what may be stored, retention, access | TG-01 | Written legal guidance applied | P3c |
| SC-19 | Voting rules and integrity review: eligibility, secrecy, dispute handling | TG-04 | Rules agreed with the committee and documented | P3c |

## QA — QA and AI evaluation

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| QA-01 | Unit-test standards: 80% coverage gate on core modules, 100% on billing and ledger rules | FD-03 | Gates enforced in CI | POC |
| QA-02 | Integration tests with Testcontainers (API + DB + jobs) | FD-02 | Run on every PR | POC |
| QA-03 | Golden set: 500+ labelled complaints across the three languages and 300+ photos, built with the pilot society, with a labelling guide | | Labels double-checked by two reviewers | POC |
| QA-04 | AI evaluation harness: accuracy per language, category, priority, duplicate and photo tasks; reruns on any model or prompt change | QA-03, AI-05 | Report produced automatically; failing threshold blocks release | POC |
| QA-05 | End-to-end tests (Playwright): complaint → estimate → approve → assign → complete → actuals → verify → resident confirm → close, in English and Urdu | WP-07, WP-08, RA-02, TA-04 | Run nightly and before release | POC |
| QA-06 | Contract tests for WhatsApp, SMS gateway and AI service | WA-08 | Run on every PR | P1 |
| QA-07 | Performance tests (k6): 50 organizations, complaint bursts, dashboard queries | DB-06 | Meets p95 < 500 ms for non-AI calls | P1 |
| QA-08 | Device and network tests: low-end Android, 3G throttling | RA-02, TA-04 | Photo upload ≤ 500 KB and completes on a throttled link | P1 |
| QA-09 | Accessibility and RTL tests (axe, manual screen-reader review) | WP-21 | No blocking issues | P1 |
| QA-10 | Scripted UAT checklist with real staff, per phase | QA-05 | Signed off at the end of POC and each phase | POC |
| QA-11 | Offline and sync test suite: 24 h offline, duplicate and out-of-order events, clock drift, lost device, large backlog | GT-01, GA-02 | Runs on every gate release | P2a |
| QA-12 | Gate end-to-end tests: pre-approved visitor, walk-in approval, timeout, blacklist, SOS | GA-14 | Run before each gate release | P2a |
| QA-13 | Gate performance: shift-change bursts, 30,000 events/day, presence query speed | GT-10 | Meets validation < 2 s and fast queries | P2a |
| QA-14 | Financial reconciliation scenarios: duplicate webhooks, partial and advance payments, reversals, bounced cheques, period locks, late-fee edge cases | BL-13, BL-04 | Ledger balances to payments and bills in every scenario | P3a |
| QA-15 | Billing end-to-end tests: bill run → payment → receipt → reminder stop; chargeable repair → bill line | WP-23 | Run before each billing release | P3a |
| QA-16 | Billing performance: bill run for the largest expected society | BL-06 | Completes within the agreed window without locking other work | P3a |
| QA-17 | Intercom test matrix: Android and iOS, locked and background, poor networks, no answer, fallback chain | IC-04 | Connect rate and ring time meet targets on the device matrix | P2a |
| QA-18 | Emergency broadcast drill tests: reach, acknowledgement, channel failures | IC-11 | Drill meets the 95% in 60 s target | P2a |
| QA-19 | Booking concurrency tests: simultaneous requests, cancellations, blocks | FB-02 | Zero overlapping bookings across thousands of attempts | P2b |
| QA-20 | Request workflow end-to-end tests: each seeded request type, send-back, timeout, dues check | RQ-12 | All paths work in English and Urdu | P2b |
| QA-21 | Accounting invariants: every journal balances, trial balance zero-sum, period close, reversals, export import test | AC-02, AC-14 | Invariants hold after every scenario | P3b |
| QA-22 | Payroll test set built with the accountant: attendance, overtime, advances, deductions, edge cases | PR-04 | Results match the accountant's manual calculation | P3b |
| QA-23 | Voting integrity tests: duplicate votes, ineligible voters, secret-ballot unlinking, result audit | TG-06 | No test can link a vote to a voter or count twice | P3c |

## LN — Launch, onboarding and operations

| ID | Task | Depends on | Done when | Phase |
| --- | --- | --- | --- | --- |
| LN-01 | Discovery: interviews (including guards and accountants), baseline metrics, competitor feature-gap matrix, pricing test, test whether societies would adopt UrbanFix gate and billing or keep their current app | | Costliest problem confirmed; written decision on whether Phases 2 and 3 are in demand | POC |
| LN-02 | Pilot partner agreement and access to sample complaints | | Signed; 500+ real complaints collected | POC |
| LN-03 | Onboarding playbook (two-week) and configuration checklist | PA-01 | Rehearsed on a test organization | P1 |
| LN-04 | Training material in Urdu: manager, supervisor, office staff, technician cheat sheet, resident video and poster | | Reviewed by pilot staff | P1 |
| LN-05 | Monitoring dashboards and alerts (latency, errors, WhatsApp delivery, queue depth, failed escalations, AI latency and cost) | FD-09 | Alerts reach on-call | P1 |
| LN-06 | Incident process, severity levels, runbooks, status page | LN-05 | Tabletop exercise completed | P1 |
| LN-07 | Support line and in-app help in Urdu and English | | Response targets published | P1 |
| LN-08 | Weekly KPI review report for the pilot | DB-01, PA-03 | Covers all Phase 1 KPIs in the plan | P1 |
| LN-09 | Go/no-go decision for Phase 2 using pilot evidence and competitor review | LN-08 | Decision recorded | P2a |
| LN-10 | Gate onboarding additions: gates and devices, guard accounts, stickers, staff passes, daily help registration, parking allotment, emergency contacts, parallel run with the paper register; guard training | GA-14, PA-06 | A society runs one gate in parallel for a trial with no lost events | P2a |
| LN-11 | Alerts and runbooks for gate sync lag, SOS and device loss; extended support hours | LN-05, GT-15 | Tested with a simulated outage | P2a |
| LN-12 | Go/no-go decision for Phase 3 using gate results and the accountant's input | LN-10 | Decision recorded | P3a |
| LN-13 | Billing onboarding additions: charge heads, opening balances, preview cycle with the accountant, payment accounts, resident announcement; accounts training | BL-17, PA-08 | First live cycle matches the accountant's own calculation | P3a |
| LN-14 | Alerts and runbooks for failed billing runs, payment webhooks and reconciliation gaps | LN-05, BL-15 | Tested with simulated failures | P3a |
| LN-15 | Start payment merchant applications (Raast, JazzCash, Easypaisa or an aggregator); engage the part-time accountant and labour adviser | | Applications submitted and advisers engaged before Phase 2a ends | P2a |
| LN-16 | Phase 2b onboarding additions: facilities and fees, request types and approval chains, committee sign-off of rules | WP-24, WP-25 | A society runs one facility and two request types live | P2b |
| LN-17 | Go/no-go for Phase 3b (accounting and payroll) after one reconciled billing cycle | LN-13 | Decision recorded with the accountant | P3b |
| LN-18 | Phase 3b onboarding additions: chart of accounts, opening balances, budgets, vendors, employees and salary structures, preview payroll; accounts training | WP-26, WP-28 | A month closes and one payroll run matches the accountant's own figures | P3b |
| LN-19 | Phase 3c onboarding additions: register tenants and set reminders, record the committee, agree poll eligibility rules | WP-29 | A live poll and an AGM notice run with correct, auditable results | P3c |
| LN-20 | Before Phase 4a: verify Meta's current policy on AI assistants and the app-store rules; decide WhatsApp vs in-app assistant | LN-12 | Written decision on file | P4a |
| LN-21 | Emergency drill with each new society before go-live | IC-11, LN-10 | Drill report accepted by the society manager | P2a |

---

## Phase 4 — epics (to be broken down after Phase 3 and real data)

| Epic | Scope | Phase | Builds on |
| --- | --- | --- | --- |
| GIS maps | Complaint markers, department layers, sector filters, heat maps, location search; gate and asset layers (Leaflet + PostGIS) | P4a | OR-02, CP-01 |
| Asset register | Streetlights, pipelines, pumps, transformers, lifts, generators; QR tags; history; migrate CP-11 hints to assets | P4a | CP-11 |
| Repeat-fault analysis | Flag assets and areas repaired repeatedly in a period, with repair spend | P4a | Asset register, JC-10 |
| Preventive maintenance | Recurring inspection schedules and reminders | P4a | Asset register, JB-01 |
| Contractors and service contracts | Contractor records linked to vendors; annual maintenance contracts for lifts, generators and pumps linked to assets; renewal reminders; payment schedule linked to vendor bills; contractor ratings after jobs; performance report | P4a | Asset register, AC-06, AC-09 |
| AI insights | Summaries, trends, area and team comparisons, natural-language questions, cost anomaly flags | P4a | DB-02, JC-10 |
| AI assistant on WhatsApp | Retrieval over the document library with citations; read-only tools for the asker's own balance, bookings and facility availability; handoff to the office; evaluation set including data-leak tests (needs LN-20 and SC review first) | P4a | Document library, BL-19, FB-02, AI-11 |
| Advanced image comparison | Same-location check and visible-change highlights | P4a | AI-12 |
| Technician offline and GPS | TA-10 | P4a | FD-17 |
| Document library | Bylaws, maps, forms, circulars; categories, versions, access rules, search; build first in Phase 4 because the assistant depends on it | P4b | FD-06 |
| Utility notices | Water supply schedule, tanker requests (request type), outages, generator timings with reminders | P4b | RQ-02, NT-08 |
| Community board | Events with RSVP, lost and found, buy/sell; posting rules, reporting, moderation | P4b | ID-06, NT-01 |
| Hardware integrations | CCTV link at gates, number-plate cameras, boom barriers: adapter layer, one vendor first | P4c | GT-01 |

---

## Suggested build order

Waves are in dependency order; tasks inside a wave can run in parallel. Each wave after the pilot starts only after its go/no-go task.

1. **Wave 1 — Foundations:** FD-01 to FD-09, DS-01 to DS-03, MA-00, ID-01 to ID-04, ID-06, ID-07, ID-08, QA-01, QA-02, AI-01. Start LN-01, LN-02, QA-03 and WA-01 at the same time, because they depend on people, not code.
2. **Wave 2 — Configuration and complaint core:** OR-01 to OR-07, CP-01 to CP-07, AI-02 to AI-08, JB-01, JC-01, MA-01 to MA-03, DS-04, DS-05, DS-08.
3. **Wave 3 — POC workflow:** AI-15, AI-16, RT-01, RT-03, JB-02 to JB-06, JC-02, JC-04, NT-01, NT-05, NT-06, DB-01, WP-01 to WP-08, WP-11, WP-13, WP-16, RA-01 to RA-03, TA-01 to TA-06, QA-04, QA-05. **The POC ends here:** run QA-10 (UAT) with the pilot society.
4. **Wave 4 — Phase 1 channels and rules:** DS-06, MA-04, WA-02 to WA-08, NT-02 to NT-04, NT-07, ID-05, ID-09, CP-08 to CP-11, AI-09 to AI-14, RT-02, RT-04 to RT-10, RA-04 to RA-09, TA-07, TA-08.
5. **Wave 5 — Phase 1 jobs, costs and management:** ID-10, OR-08 to OR-12, JC-03, JC-05 to JC-11, RT-11, JB-07 to JB-10, AI-11, AI-17, DP-01 to DP-06, DB-02 to DB-08, NT-08, WP-09, WP-10, WP-12, WP-14 to WP-20, TA-09, PA-01 to PA-05, FD-15, FD-16.
6. **Wave 6 — Phase 1 hardening and launch:** FD-10 to FD-14, SC-01 to SC-08, QA-06 to QA-09, WP-21, MA-05 to MA-07, MA-10, DS-09, DS-10, LN-03 to LN-08, then the pilot. Start app-store accounts (MA-06) early.
7. **Wave 7 — Phase 2a, gate and security (only after LN-09):**
   - First: FD-17, FD-18, OR-13, ID-11, ID-12, MA-08, MA-09, MA-11, DS-07, DS-11, IC-01, GT-01, GT-02, WA-09, WA-11, LN-15.
   - Then: GT-03 to GT-08, GA-01 to GA-06, WA-10, IC-02 to IC-06.
   - Then: GT-09 to GT-24, GA-07 to GA-16, RA-10 to RA-12, RA-15, RA-16, WP-22, NT-09, DB-09, PA-06, PA-07, IC-07 to IC-11, WA-15.
   - Finally: SC-09, SC-10, SC-14, QA-11 to QA-13, QA-17, QA-18, LN-10, LN-11, LN-21.
8. **Wave 8 — Phase 2b, facility booking and requests:** DS-12, RQ-01, RQ-02, FB-01 to FB-08, RQ-03 to RQ-12, WA-13, WA-14, RA-17, RA-18, WP-24, WP-25, DB-11, QA-19, QA-20, LN-16.
9. **Wave 9 — Phase 3a, billing and payments (only after LN-12):**
   - First: BL-01 to BL-05, SC-11.
   - Then: BL-06 to BL-10, BL-24, WP-23.
   - Then: BL-11 to BL-16, BL-19, BL-26, BL-27, RA-13, WA-12, NT-10.
   - Then: BL-17, BL-18, BL-20, RA-14, DB-10, PA-08.
   - Finally: SC-12, SC-13, QA-14 to QA-16, LN-13, LN-14.
10. **Wave 10 — Phase 3b, full accounting and payroll (only after LN-17):** DS-13, AC-01, AC-02, AC-05, AC-03, AC-04, AC-06 to AC-16, PR-01 to PR-09, WP-26 to WP-28, DB-13, SC-15 to SC-17, QA-21, QA-22, LN-18.
11. **Wave 11 — Phase 3c, tenants and governance:** DS-14, TG-01 to TG-10, WA-16, RA-19 to RA-21, WP-29, NT-11, DB-12, SC-18, SC-19, QA-23, LN-19.
12. **Wave 12 — Phase 4:** break down the Phase 4 epics using pilot and live data. Do LN-20 before the assistant, build the document library first, and treat hardware (4c) as customer-led.

## Notes

- Tasks tagged POC are the minimum for the end-to-end demo. The POC uses web intake only, and WhatsApp starts in Wave 4.
- The mobile app's technology (MA-00) is decided in Wave 1, before any app screens are built; the role modules (RA, TA, GA) are technology-neutral in their acceptance criteria. The calling provider (IC-01) is decided before any intercom work.
- Auto-routing stays off for every organization until AI-17 shows 90% accuracy for a category on live data.
- Gate, intercom, booking, requests, billing, accounting, payroll, governance and the assistant are behind feature flags per society (FD-13). They must not ship to a society until its own go/no-go and parallel-run tasks are done.
- Money, accounting, payroll, gate and vote records are append-only. Corrections are reversals, never edits or deletes.
- Non-code tasks (LN-01, LN-02, LN-15, QA-03, WA-01, WA-07, WA-11, SC-05, SC-06, SC-09, SC-11, SC-18) have long lead times. Start them early.
- Payment provider onboarding (Raast, JazzCash, Easypaisa or an aggregator) can take weeks (LN-15). Start merchant applications while Phase 2a is being built.
