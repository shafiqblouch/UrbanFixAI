# Wave 5 (Phase 1 jobs, costs and management): status

Same meaning of **Done** as in `wave-3-status.md`. Wave 4's own status is in `wave-4-status.md`.

## Slice 1: estimate approval, overruns, chargeable jobs (backend)

| Task | Status | Notes |
| --- | --- | --- |
| ID-10 approval workflow | Done | A request has an amount, an expiry and one or two approvers needed. Managers and committee members answer; supervisors do not. Nobody answers their own request. One "no" ends it; "yes" counts until enough different people have said it. Each answer is its own row, never edited. Unanswered requests expire (hourly job). Used for estimates now; reversals and waivers (Phase 3) can use it without changes to it. |
| OR-12 policy settings | Partly done | Added to the existing settings: estimate approval limit, second-approver amount, approval expiry hours, hours before a waiting estimate escalates, overrun threshold. Together with the earlier ones (unknown-number policy, auto-confirm, AI allowance) each changes behaviour in its module. **Not yet in the settings: languages, AI toggles and escalation contacts as one place** (escalation levels and the AI allowance have their own endpoints). No screen yet. |
| JC-03 approval limit | Done | An estimate above the limit is sent for approval and **work cannot start** until it is approved (clear error to the technician). A rejection needs a reason and returns the estimate; a revised estimate replaces it (the old request is withdrawn). A limit of 0 (the default) means nothing is asked for. Decisions are on the job timeline. |
| JC-05 variance and overrun | Done | Job detail and the costs reply show the overrun against the threshold (default 20%). The first time a job goes over, supervisors and managers are told once; `GET /jobs/overruns` lists flagged jobs; the flag clears if the costs are corrected. A job with no estimate is never flagged. |
| RT-11 estimate waiting too long | Done | After the configured wait (default 24 h) the existing escalation levels apply: supervisors at once, managers 12 h later. Clears when the estimate is decided. |
| JB-10 chargeable flag | Done | Supervisors and managers mark a job chargeable to a house (a house is required) or clear it; the change is in the audit log. Nothing is billed: that waits for billing (Phase 3). |

Messages added (English and Urdu): estimate waiting for approval, approved, not approved, cost over estimate, estimate waiting too long.

## Assumptions to confirm

- Only **Manager** and **Committee** may approve. A supervisor proposes work but cannot approve it. Say if supervisors should be able to approve up to some smaller amount.
- Approval is off until a manager sets a limit; it applies to the estimate total (not to actual costs).
- Expiry default 72 hours; escalation wait default 24 hours; overrun threshold default 20%. All placeholders.

## Slice 2: registry, stores and costs, duplicates, auto-route gate (backend)

| Task | Status | Notes |
| --- | --- | --- |
| OR-08 to OR-11 vehicles, daily help, CSV import, duplicate warnings | Done (backend) | See `RegistryTests`. No screens yet. |
| JC-06 to JC-11 stores, stock, cost reports, chargeable review queue | Done (backend) | See `StockTests`, `CostReportTests`. Accounts review charges with the bill permission. No screens yet. |
| DP-01 incidents | Done | A supervisor can group complaints by hand, add or remove one. The complaints stay separate records and keep their own status. An incident left with one complaint is closed. |
| DP-02 / DP-03 finding and scoring | Done | Candidates are recent (default 7 days), close (default 300 m) or in the same place. Score = wording 50%, place 25%, time 15%, category 10%. Without embeddings the score is capped at 0.85. The reason is stored in words. Runs after the text embedding, or at the end of AI enrichment when embeddings are off. Radius, window and threshold are society settings. |
| DP-04 decision | Done | Suggestions are never merged automatically. Accept puts both into one incident; reject is remembered and that pair is not suggested again. |
| DP-05 tell everyone | Done | When any complaint in an incident is assigned or resolved, reporters of the other complaints in the incident get a message under their own reference. |
| DP-06 quality | Partly done | Scoring and the threshold choice are tested on a **small synthetic labelled set** (precision at the default threshold at least 90%). **Real pairs from a real society are needed** before the 0.7 default can be trusted. |
| AI-17 auto-route gate | Done | Turning a category to automatic is refused (409 `accuracy_below_gate`) unless the AI's category suggestion was accepted at least 90% of the time over at least 30 suggestions in the last 90 days (settings `AutoRoute:MinAccuracy`, `AutoRoute:MinSamples`). Turning it off is never refused. `GET /reports/auto-route-readiness` shows each category. Categories already automatic are not switched off. |

Message added (English and Urdu): the problem you reported is fixed (for the other reporters of an incident).

## Slice 3: notices (NT-08, backend)

| Task | Status | Notes |
| --- | --- | --- |
| NT-08 notices and announcements | Done (backend) | A manager or committee member writes a notice (English, plus an optional Urdu version) for everyone, owners only or tenants only, optionally limited to the houses under one location (a street or block). It is sent now or at a chosen time (a scheduler checks every minute), and a scheduled one can be called back until it goes out. It reaches people as ordinary notifications, so channel choice, retries, WhatsApp/text fallback and delivery receipts all apply. The sender sees, per notice, how many were reached, delivered, read and failed, and who read it (`GET /notices`, `/notices/{id}`, `/notices/{id}/readers`). A resident's feed is `GET /me/notices`; opening one marks it read for the sender too. See `NoticeTests`. Portal screen built (WP-19, RA-09): see below. |

Portal: a **Notices** menu for Managers and Committee (list with reached / delivered / read / failed, a composer with an audience preview, scheduling, call-back, and who read each notice) and for Residents (their notices, marked read when opened). English and Urdu. Tested with the portal tests; **not yet looked at in a browser against the running API**, and the mobile app has no notices screen.

Rules worth knowing:

- "Owners" and "tenants" mean people with a **current** link of that kind on the day it is sent; someone who moved out is not reached. Family members are reached only by "everyone".
- "Everyone" with no location also reaches residents not linked to any house; with a location it reaches only people linked to houses under it.
- Sending now to nobody is refused (`no_recipients`). `GET /notices/audience` shows how many a choice would reach before sending.
- The audience is worked out **when the notice is sent**, not when it is written, so a scheduled notice reaches whoever lives there then.
- Delivered means at least one channel took it (the in-app inbox always does); failed means every attempt failed. Read comes from the resident opening it, or a channel that reports it.
- Once sent, a notice cannot be called back or edited. To change a scheduled one, cancel it and write a new one.
- Each person gets a notice once, however many times it is handled.

New permission `notice.manage`, given to **Manager and Committee** (assumption: say if supervisors should send notices too). Limits: title 200 characters, text 4000.

Message added: the notice template (the sender's own words). The Urdu text is used for residents whose language is Urdu; if the sender gave none, they get the English.

## Slice 4: dashboards, exports, PDF and the monthly summary (DB-02, DB-04 to DB-08, FD-15, WP-20)

| Task | Status | Notes |
| --- | --- | --- |
| DB-02 trends | Done | `GET /dashboard/trends`: complaints reported and resolved per week (Monday start) or month, in the society's time zone, for the last N periods, empty periods as zero, optionally split by category or sector. |
| DB-06 tuning | Done | Indexes on complaints and jobs (migration 0025). A test loads **20,000 complaints** and checks the summary, both trend views and the spend widget stay under 500 ms at the 95th percentile (it passes on this machine; **not measured on production hardware**). |
| DB-07 spend, overrun, low stock | Done | `GET /dashboard/spend` reads the same code as the cost report, so the figures match (tested). Also lists jobs over estimate, items at or below their alert level, and (for accounts) the number of charges waiting. |
| DB-08 role views | Done | `GET /dashboard/layout` names the widgets each role lands on (manager, committee, supervisor, accounts, office, and support). The portal builds the dashboard from that list; each widget's data is still guarded by its own permission. |
| FD-15 PDF service | Done | English and Urdu reports with an embedded Noto Naskh Arabic font, right-to-left layout and joined letters; **the Urdu page was rendered and looked at**, it reads correctly. Bills and receipts can reuse the same renderer later. |
| DB-04 export | Done | CSV and PDF of the complaint list (same filters and visibility as the screen), the management summary, and the cost report. Spreadsheet formulas in complaint text are defused; the phone number column only appears for people who triage; every export is written to the audit log. |
| DB-05 monthly summary | Done | A daily job makes last month's summary PDF on the chosen day (1 to 28), keeps it, and tells the chosen recipients (in their language). One per month, never twice. Costs are in the file only if every recipient may read costs. A manager can also make one for a past month. |
| WP-20 reports | Built | Reports screen: costs, repeated repairs, overruns, and a Summaries tab (download for any dates, past monthly summaries, and the schedule). Complaint list has Export CSV / PDF. |

New permission `cost.read` (Manager, Committee, Supervisor, Accounts): cost reports, spend and overrun widgets and the cost export. Office staff gain `dashboard.read` (counts only) so they get their own view without seeing money.

**Licence to check:** the PDF library (QuestPDF) is free under its Community licence for organisations under a revenue limit; a paid licence is needed above it. Please check this applies to you before release. The fonts are SIL Open Font licence.

## Slice 5: platform administration and feature flags (PA-01 to PA-05, FD-13)

| Task | Status | Notes |
| --- | --- | --- |
| PA-01 onboarding | Done | A society can be created already set to its structure, language and time zone. An overview lists every society with its size, and a checklist shows what is still to set up (manager, categories, places, houses, team, staff, price list, residents, routing); "ready" once the first five exist. The earlier "under an hour" claim is not measured. |
| FD-13 / PA-02 feature flags | Done | Per-society switches that take effect at once (tested: one society on, another off). `auto_routing` is on by default and, when off, stops a category being made automatic and stops automatic assignment; gate, billing and the other future features are off by default. A society reads its own flags at `GET /features`. Each change is audited in the society's log and the platform's. |
| PA-03 AI quality | Done | Acceptance and override rate per society, category and language, straight from the suggestion log. |
| PA-04 AI cost and caps | Done | Calls, tokens, cost, cost per complaint and use of the monthly allowance per society; the allowance can be changed (0 switches the AI off for that society). Cost shows only where every call had a price. |
| PA-05 support access | Done | A platform admin opens a **read-only, time-limited (1 to 120 minutes) session** into one society, with a written reason. It acts as a new `Support` role that can only read; any change is refused with `support_read_only`. Every start, end, page viewed and refused change is in the **society's own audit log** and the start and end in the platform's. Ending it, or its time running out, stops the token at once. |

Platform views read across societies on the owner database connection and return only aggregates. This is the one place that does; please review it as part of the security review.

## Slice 6: push infrastructure (FD-16)

See `docs/push-setup.md`. Device registry, push channel, FCM sender, browser (VAPID, encrypted) sender, test push, and a portal button and service worker. **Not verified against real FCM or a real browser, and the mobile app does not register yet (MA-04).**

## Portal screens built in this wave

Notices, Approvals, Store, Reports (costs, repeated repairs, overruns, summaries), Repair charges (accounts), Directory (search, houses and residents, vehicles, daily help, CSV import wizard, duplicate warnings), Configuration (society settings and limits, places, landmarks, response times, escalation, automatic routing gate, price list, plus the earlier tabs), Platform (societies, onboarding, switches, AI quality and cost, support access), duplicate review and incident grouping on the complaint, job pages (chargeable flag, AI before/after comparison, materials and requests, approval status), resident feedback, role-based dashboard with a trend chart, and the push button.

All are English and Urdu and covered by portal tests with a stubbed API. **None has been looked at in a browser against the running system**, and the trend chart's colours were checked with the palette validator but the page was not viewed.

## Assumptions to confirm

- Notices: only Manager and Committee send (`notice.manage`).
- Cost figures: `cost.read` as above; Office sees counts but not money.
- `auto_routing` defaults to on so existing societies keep working.
- Support access is limited to 120 minutes and needs a reason of at least 10 characters.

## Not done

- Mobile app screens and push registration (notices feed RA-09, MA-04), and the technician materials request on mobile.
- Real accounts for FCM, browser push keys, and the first real SMS/email gateways remain with you.
- Posting charges to bills waits for billing (Phase 3).
