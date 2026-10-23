# Wave 3 (POC workflow): status

Task IDs are from `UrbanFix — Task Breakdown.md`. **Done** means built, tested, and the "done when" is met as far as it can be checked without people or devices. Where it cannot, the gap is named.

## Backend

| Task | Status | Notes |
| --- | --- | --- |
| AI-15 enrichment job, retries, every suggestion stored with confidence and model version | Done | `ai_suggestions` keeps each attempt, including failures; the complaint is never lost. Only the "understanding" task exists; photo interpretation has no job yet. |
| AI-16 acceptance and override | Done | One row per complaint and field; `/reports/ai-accuracy` by category and language. |
| RT-01 routing rules | Done | Most specific rule wins; ties by order. A job created without a team uses it. |
| RT-03 supervisor review queue | Done | AI suggestions pre-filled; every edit is audit-logged. |
| JB-02 to JB-06 job workflow | Done | Assign, accept, progress, cannot-complete, evidence rules (photo / checklist / note), sign-off or return. |
| JC-02 estimate, JC-04 actual costs | Done | Versioned estimates priced from the price list; actuals required when the category says so; variance reported. |
| NT-01 notification module, NT-05 live push, NT-06 lifecycle triggers | Done, one gap | English and Urdu templates, delivery log, retries, SignalR. **No estimate-approval message**: the approval step (JC-03) is Phase 1. |
| DB-01 aggregation queries | Done | Checked against a hand-calculated sample. Overdue limits by priority (24 h, 72 h, 7 d, 14 d) are an assumption: please confirm. |

## Portal (`web/apps/portal`)

| Task | Status | Notes |
| --- | --- | --- |
| WP-01 shell, 2FA login, role menus, count badges, header search, English/Urdu | Done | |
| WP-02 complaint list with filters and search | Done | Server-side filtering and paging. Not load-tested with thousands of rows. |
| WP-03 complaint detail with AI suggestions | Done | |
| WP-04 review queue and assignment | Done | One click to accept the AI's category and assign. |
| WP-05 office entry form | Done | Photos and a voice note attach after the complaint is saved. Location is chosen from the house or location list, not a map. |
| WP-06 job views, WP-07 evidence review and sign-off, WP-08 estimate and actual costs | Done | |
| WP-11 dashboard | Done | |
| WP-13 configuration | Done | Departments, teams, categories with evidence rules and checklists, routing rules. |
| WP-16 audit log viewer | Done | |

## Mobile (`mobile/`)

| Task | Status | Notes |
| --- | --- | --- |
| MA-00 framework | **Proposed, needs your confirmation** | React Native with Expo; see `MA-00-mobile-framework.md`. |
| MA-01 to MA-03 setup, navigation, theming, i18n and RTL, API client, role switcher | Built, not run on a device | |
| RA-01 resident home, RA-02 complaint form, RA-03 receipts | Built, not run on a device | Voice note, compressed photos (at most 500 KB), resumable upload, landmark picker, "use my location". There is no map pin. Not tested on throttled 3G. |
| TA-01 to TA-06 technician screens | Built, not run on a device | |

Checked for mobile: TypeScript, 33 logic tests (uploads, queue, receipts, costs, roles, translations), and a Metro bundle for Android. "Runs on a test phone and tablet", "smooth on a low-end phone and 3G" and "usable after the 1-hour training" need people and devices.

## Quality

| Task | Status | Notes |
| --- | --- | --- |
| QA-04 AI evaluation harness | Done | Replays recorded answers in CI; a model, prompt or case change forces a live re-run; thresholds and a baseline gate the build. Priority, duplicate and photo tasks have **no labelled cases**, so they are reported as not evaluated. The labelled set is synthetic until QA-03. |
| QA-05 end-to-end test | Done, one gap | Complaint to closure in English and Urdu through the real portal, API, PostgreSQL and MinIO. **No estimate approval step** (JC-03), and the mobile app is not part of it. |

## Found along the way

- Seeded Urdu category names were corrupted (double-encoded); fixed.
- The recurring escalation job was registered through Hangfire's static API before storage existed, so the API would not start with background services on; fixed (found only by the end-to-end run).
- Search wildcards were not escaped in complaint search; fixed with an explicit escape character.

## Not started (Waves 4 to 7)

Everything after the POC: WhatsApp (WA-*), mobile push (MA-04, FD-16), duplicate detection and the other Phase 1 AI tasks (AI-09 to AI-14), escalation rules and the remaining routing tasks (RT-02, RT-04 to RT-10), estimate approval and cost-overrun flags (JC-03, JC-05 to JC-11), reports and dashboards by role (DB-02 to DB-08), the Phase 1 portal screens (WP-09, WP-10, WP-12, WP-14 to WP-21), billing and accounts (DP-*, PA-*), security hardening and launch tasks (FD-10 to FD-14, SC-*, QA-06 to QA-09, LN-*), and everything in Phases 2 to 4 (gate, visitors, parcels, bookings, bills and payments, and so on).

Tasks that are about people, not code, also remain: QA-03 (real labelled complaints), DS-03 (Urdu typeface test on phones), DS-08 (prototype tests with users), QA-10 (UAT with the pilot society), LN-* (pilot and legal).
