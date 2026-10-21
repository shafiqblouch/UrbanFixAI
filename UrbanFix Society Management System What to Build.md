# UrbanFix Society Management System: What to Build

Oct 20, 2026 · @shafiq

UrbanFix becomes one platform for a housing society: complaints and repairs, gate security, maintenance charges and community services, all sharing one resident and house directory.

## Apps and users

| App | Used by | Main jobs |
| --- | --- | --- |
| WhatsApp bot + resident mobile app (Android, iOS) | Residents, tenants | Report complaints, approve visitors, pay dues, book facilities, submit requests, vote, get notices |
| Gate app (tablet/phone at each gate) | Security guards | Log entry/exit of visitors, vehicles, daily help, deliveries; call residents by app intercom |
| Technician app | Plumbers, electricians, civil, sanitation workers | Accept jobs, upload photos, enter materials used |
| Office and admin web portal | Society office, supervisors, accounts | Manage complaints, jobs, billing, residents, settings |
| Management dashboard | Society manager, committee | Reports on complaints, security, collections and expenses |

## 1. Complaints

- Report by WhatsApp (text, photo, voice), app, or at the office; Urdu, Roman Urdu, English.
- AI suggests category, location, department and priority; staff can correct it.
- Reference number, status tracking: New → Under review → Assigned → In progress → Resolved → Closed (plus reject, reopen).
- Duplicate detection groups reports of the same problem into one job.
- Deadlines per category; late jobs escalate to the supervisor automatically.
- Closure needs before/after photos, supervisor sign-off and resident confirmation.

## 2. Maintenance jobs, materials and expenses

- Every approved complaint becomes a job with team, technician, due date and notes.
- **Estimate before work:** technician or supervisor lists materials (e.g. cement bags, pipe, fittings, wire, bulbs) with quantity and rate, plus labour hours and transport.
- Estimates above a set amount need manager approval before work starts.
- **Actual after work:** materials really used and final cost recorded at completion, compared with the estimate.
- Material price list and store stock: items issued per job, low-stock alerts.
- Repairs inside a house can be marked chargeable and added to that resident’s bill (links to section 4).
- **Contractors and service contracts:** annual maintenance contracts for lifts, generators and water pumps, with renewal reminders and contractor ratings.
- Reports: spend per job, category, street, asset; repeated repairs flagged.

## 3. Gate and security

- Guard app on a tablet or phone at every gate; works offline and syncs when signal returns.
- **Visitors:** resident pre-approves a guest and a QR/OTP pass is sent; walk-in visitors are logged (name, phone, vehicle number, photo, house visited) and the resident approves or rejects on WhatsApp.
- **App intercom:** guard calls the resident through the app, keeping phone numbers private.
- **Residents and vehicles:** QR or RFID sticker for registered cars and bikes; quick entry log.
- **Parking:** allotted slots per house, visitor parking, vehicle stickers.
- **Daily help:** maids, drivers and cooks clock in and out at the gate; residents get alerts and can rate them.
- **Contractors and workers:** passes with allowed days and times.
- **Deliveries and parcels:** logged at gate, resident notified, collection recorded.
- Entry and exit time for every person and vehicle; who is inside right now; blacklist alerts.
- Guard shifts, attendance, patrol check-ins, incident reports and an SOS/panic alert to supervisors.
- **Emergencies:** fire or medical broadcast to all residents and an emergency contact list.
- Later: CCTV link at gates, number-plate cameras and boom-barrier integration.

## 4. Maintenance charges, billing and accounts

- Charge heads set by the society: monthly maintenance, security, water, garbage, one-off levies; rates by plot/flat size or category.
- Monthly bills generated automatically per house; late fees and fines by rule.
- Payments by Raast, JazzCash, Easypaisa, bank transfer, or cash/cheque recorded at the office; receipts sent on WhatsApp.
- Ledger per house: paid, pending, overdue, advance; defaulter list and automatic reminders.
- Residents check their balance and history on WhatsApp or the app.
- **Full accounting:** budgets, vendor payments, purchase orders, income vs expense (including job costs from section 2), audit-ready reports, export to accounting software.
- **Staff payroll:** attendance and salaries for guards, sweepers and maintenance workers.

## 5. Admin, dashboards and platform

- One directory of sectors, streets, houses, owners, tenants, vehicles and staff, shared by all modules.
- Settings per society: departments, categories, deadlines, gates, charge rules, approval limits.
- Dashboard: open and overdue complaints, team workload, gate traffic, collections vs dues, expenses, hotspots on a map.
- Native Android and iOS apps with push notifications, alongside WhatsApp and the web portal.
- **AI assistant on WhatsApp:** answers questions such as “how much do I owe?”, “what are the construction rules?” or “is the hall free on Friday?” from the society’s own data and bylaws.
- Notifications and notices by WhatsApp, SMS and app; broadcast announcements with read tracking.
- Roles and permissions (resident, guard, technician, office, accounts, committee, manager); full audit log.
- Security: encrypted data, phone OTP login, separate data per society, daily backups; visitor photos and ID details kept only as long as the society’s policy allows.

## 6. Community, requests and governance

- **Facility booking:** community hall, gym, ground, marquee; calendar, fees, no double bookings.
- **Polls and committee:** resident polls and voting, AGM notices, meeting minutes, record of decisions.
- **Requests and NOCs:** construction or renovation approval, house transfer NOC, move-in/move-out, gate pass for moving goods; online form, fee and approval steps.
- **Tenant registration:** tenant details and police verification status, with expiry reminders to owners (confirm the local legal requirement).
- **Utility notices:** water supply schedule, water tanker requests, planned power outages and generator timings.
- **Document library:** bylaws, society maps, forms and circulars.
- **Community board:** events, lost and found, buy/sell between residents.

## Build order

| Phase | Delivers |
| --- | --- |
| 1 | Directory, resident mobile app, complaints, jobs, material estimates and actual costs, dashboard |
| 2 | Gate and security (visitors, vehicles, parking, daily help, intercom, deliveries, emergencies), facility booking, requests and NOCs |
| 3 | Maintenance charges, online payments, ledgers, reminders, full accounting, payroll, tenant registration, polls and committee tools |
| 4 | GIS maps, asset records, repeat-fault analysis, preventive maintenance, contractor contracts, AI assistant and insights, document library, community board, utility notices, CCTV and boom barriers |

Gate and billing put UrbanFix in direct competition with Hamari Society and Manzhil, which already offer both. The edge must stay in AI-assisted maintenance, verified repairs and job cost control.
