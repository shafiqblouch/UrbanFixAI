# Wave 4 (Phase 1 channels and rules): status

Task IDs are from `UrbanFix — Task Breakdown.md`. Same meaning of **Done** as in `wave-3-status.md`.

## Slice 1: priority, safety, SLA and auto-routing (backend)

| Task | Status | Notes |
| --- | --- | --- |
| RT-04 priority engine | Done | Category default, raised by location risk (0 to 2, capped at urgent), and a safety flag. New level `Safety` above `Urgent`. Priority is only ever raised by the engine, never lowered. |
| RT-05 safety handling | Done, one gap | Safety terms (English and Urdu) are flagged when the complaint is stored, so it works with the AI down. Supervisors get an alert and the resident gets a "call Rescue 1122" message. **The resident prompt before submit (RA-08) and image-based flags (AI-10) are not built.** |
| RT-06 SLA policies | Done | Per category and priority; respond-by and resolve-by stored on the complaint; optional working-hours counting (09:00 to 18:00, Sunday off) in the organization's timezone. Built-in defaults apply where no policy exists. |
| RT-02 auto-route | Done | Per category switch and threshold (default 0.9). Assigns only when: switch on, AI confidence at or above the threshold, the category was not chosen by a person, a routing rule matches, and no safety flag. The history shows both steps with no person as actor. Every other case stays in the review queue. |
| RT-10 rule administration | Done | SLA policy CRUD, auto-route and location-risk settings (managers only), validation, and ambiguous routing rules (same scope and order, different teams) are rejected. Changes are audit-logged by the existing interceptor. |

## Slice 2: escalation (backend)

| Task | Status | Notes |
| --- | --- | --- |
| RT-07 escalation scheduler | Done | Five rules: not assigned in time, job not accepted (a quarter of the resolve time, at least 1 h), past target, finished work waiting for sign-off over 24 h, resident dispute (reopened with no new job). Each rule fires once per level per complaint; a condition that clears and comes back fires again. Runs every 5 minutes. |
| RT-08 levels and contacts | Done | Per organization, 1 to 5 levels, each with a delay and a role to notify (default: supervisors at once, managers after 12 h). Managers edit them. An open escalation closes itself when the condition clears (`condition_cleared`) or the complaint ends (`complaint_closed`). `GET /escalations?open=true` lists them. |
| RT-09 failure alert | Done, one gap | A failure in any organization's scan is reported to the job-failure notifier immediately, and the run then fails. **The notifier only logs today; paging the on-call person needs the monitoring set-up (FD-09).** |

Changes you may notice: the escalation message now has one text per rule in English and Urdu; the old fixed 24 h / 72 h / 7 d / 14 d limits no longer drive escalation (they still drive the dashboard's overdue list and the job list's due time, which should move to the stored deadline).

Interpretation to confirm: the task list says "complete without evidence". Evidence is already enforced when a job is completed (JB-05), so I implemented "finished work waiting for sign-off for over 24 hours" instead.

## Slice 3: resident sign-in, sender policy, confirmation (backend)

| Task | Status | Notes |
| --- | --- | --- |
| ID-05 phone-OTP login | Done, one gap | 6-digit code, 5-minute expiry, single use, 5 wrong tries kill it, one code a minute and 5 an hour per number. Residents only: anyone who also holds a staff role must still use password and authenticator. The answer is identical for known and unknown numbers. **No real SMS gateway yet (NT-03): codes go to the log in development, so nothing reaches a resident's phone until NT-03 is done.** |
| ID-09 rate limiting | Done | Public endpoints (login, OTP) are limited per client address, 30 a minute by default (`RateLimits:PublicPerMinute`); per-phone OTP limits are separate. Answers 429 with a retry time. **If the API sits behind a proxy, it must pass the real client address through, or all callers share one budget.** |
| CP-08 unknown-number policy | Done for phone-in and walk-in; WhatsApp waits for WA-02 | A manager picks per organization: hold in an unverified queue (default), accept as guest, or require registration. A known resident number is matched automatically. Staff can verify and link a caller, then release the complaint. `reporter_verified` is stored. The same resolver will serve WhatsApp. |
| CP-09 status lookup | Done | A resident looks up their own complaint by reference; someone else's reference looks the same as one that does not exist. A phone-verified lookup exists for WhatsApp (needs WA-05 to expose it). |
| CP-10 resident confirmation | Done | By default only the resident closes a resolved complaint (staff get "resident_confirmation_required"); a complaint with no resident on it can still be closed by staff. Auto-confirm is off by default; when on, an hourly job reminds once after N days and closes after M days, marked auto-confirmed with a history entry. |
| CP-11 asset reference | Done | Free text up to 100 characters; found by the complaint search. |

New settings endpoints: `GET/PUT /organization/policies` (managers change them). Residents get a phone number when staff add them (`phone` on `POST /members`); there is no screen for it yet.

## Slice 4: AI tasks AI-09 to AI-14 (AI service and backend)

| Task | Status | Notes |
| --- | --- | --- |
| AI-09 voice transcription | Built and tested with fakes; **not run against a real speech model** | `POST /v1/speech/transcribe`; a background job transcribes each voice note and the transcript, language and confidence show beside the audio. The confidence is the model's own estimate. AI-01 never compared speech models, so the provider sends audio to a chat model that accepts audio input, and the model is a setting you must choose and test on real Urdu recordings. The phone records AAC in an MP4 container; whether the chosen model accepts that is unchecked. |
| AI-10 safety flag | Done | Text: 17 danger phrases in English and Urdu are always flagged, ordinary complaints are not (tested). Voice: the transcript is checked too. Photos: a hazard label (open manhole, exposed wire, sewage overflow, fallen tree) flags the complaint. A flag makes it top priority, alerts supervisors and prompts the resident. Photo labelling needs the vision model to work; the AI-08 photo test (50 photos) still does not exist. |
| AI-11 embeddings in pgvector | Built and tested with fake vectors; **no embedding model chosen** | Similar-complaint search (`GET /complaints/{id}/similar`) answers in well under a second with 5,000 stored vectors. Search is within one organization, only between vectors of the same model. Photos are embedded through their text description, not as images, until an image-embedding model is chosen. Off until `Ai:EmbeddingsEnabled` is set and the AI service has `AI_EMBEDDING_PROVIDER` and model. Tests now run on the pgvector image. |
| AI-12 before/after comparison | Built and tested with fakes | `POST /jobs/{id}/comparison` compares the latest before and after evidence photos: same-place likelihood and a cautious change note, always marked advisory. Same wording rules as photo interpretation (no hidden faults). Repeat requests for the same pair are free. |
| AI-13 usage metering and caps | Done | Every AI call records tokens (counting retries) per organization and complaint. `GET /reports/ai-usage` gives totals by task and model, cost per complaint and use against the monthly allowance. Default allowance 2,000,000 tokens a month, set per organization in the policy; 0 switches the AI off. **Cost needs prices: none are shipped. Set `Ai:Pricing:<model>` (per million tokens), otherwise cost shows as unknown while tokens are still counted.** |
| AI-14 rules-only fallback | Done | When the AI is off, over its allowance, or still failing after the last retry, a keyword guess (English, Urdu, Roman Urdu) fills the category as a low-confidence suggestion, so the complaint still reaches the queue with something filled in. It never routes anything by itself and never replaces a person's choice. A later re-run with the AI back replaces it. |

Other jobs (transcription, photos, embeddings, comparison) are skipped quietly when the AI is unavailable for an organization; the recording or photo is kept.

Not done in this group: the 50-photo evaluation and the Urdu speech test (need real data and a funded key), and image embeddings.

## Slice 5: resident and technician app screens (RA-04 to RA-08, TA-07, TA-08) and JB-07/JB-08

**Checked:** TypeScript, 81 app logic tests, an Android Metro bundle, and the backend tests. **Not checked: anything on a phone** (no device was used), so none of these screens has been tapped through for real.

| Task | Status | Notes |
| --- | --- | --- |
| RA-04 phone-OTP login | Built | Residents sign in with a number and the code from the text message, with a resend countdown; staff keep email, password and authenticator. **No text message is sent until the SMS gateway exists (NT-03)**, so this cannot be tried end to end yet. |
| RA-05 my complaints, timeline, find by number | Built | Find a complaint by its number (someone else's number looks like a missing one). The detail screen reloads itself every 20 seconds while it is open. **This is polling, not a live push**; real push needs MA-04, which needs the push accounts (FD-16). |
| RA-06 confirm or dispute, feedback rating | Built | Confirm and dispute already existed. Added a 1-to-5 rating, "fixed or not", and a comment (JB-08, with the average rating and the number of "fixed" answers on the dashboard summary). Dispute reopens the complaint and escalates through the Wave 4 rules (JB-07). |
| RA-07 answer a request for more information | Built | A complaint held for a missing place can be answered by walking down the society's places (sector, street, house); another photo can be added to any open complaint. Free text alone is not accepted as a place, as before. |
| RA-08 emergency prompt | Built | If the words contain a danger term, the app shows "call Rescue 1122" with a dial button before sending; the complaint can still be sent. The word list is one shared file, `contracts/safety-terms.json`, read by both the server and the app; Roman Urdu terms were added. Only typed text is checked, not voice notes. |
| TA-07 checklist completion | Built | Progress shown ("2 of 5 done"); the "send for approval" button is disabled and says what is missing, using the same rules as the server (photos, checklist, note, costs). |
| TA-08 voice input for notes | Built, **needs a speech model** | Record, get text back, correct it, save. Needs the speech model from AI-09; nothing is stored from the recording. |
| RA-09 notices feed | **Not started** | Needs the notices backend (NT-08), which belongs to Wave 5. |
| MA-04 push | **Not started** | Needs FCM/APNs accounts (FD-16). |

Also fixed: the web portal did not compile after the Wave 4 API changes (my mistake: I regenerated the client but did not run the portal checks that time). The office entry form now has the caller's phone number and the pole/asset number. Portal checks pass again; the browser end-to-end test was not re-run.

Backend added for these: resident feedback (`POST /complaints/{id}/feedback`) and `POST /voice/transcribe`.

## Slice 6: WhatsApp, text messages and email (WA-02 to WA-08, DS-06, NT-02 to NT-04, NT-07)

**Nothing here has talked to Meta, a text-message gateway or a mail server.** It is built and tested against fakes and hand-written samples of Meta's documented formats. See `docs/whatsapp-templates.md` for what to set up and `docs/whatsapp-conversation.md` for the chat as built.

| Task | Status | Notes |
| --- | --- | --- |
| WA-02 webhook receiver | Done (against fakes) | Signature checked on the exact bytes received (a forged or altered body gets 401 and stores nothing); each message stored once, so a replay changes nothing; unknown business numbers and unknown types are acknowledged and ignored; quick answer, work queued. |
| WA-03 photos and voice notes | Done (against fakes) | Fetched from WhatsApp and attached through the same checks as an upload (type sniffed, hashed, thumbnail, duplicates skipped); a failure tells the person and keeps the complaint. Voice notes then go to transcription; photos to interpretation. |
| WA-04 complaint conversation | Done | Guided or just type it; photo first; send as it is; add more after saving; location pin matched to a known place within 150 m; resumes after a 6-hour pause. A complaint sent as only a voice note takes its words from the transcript. Complaint creation now lives in one shared service used by the app, the portal and WhatsApp. |
| WA-05 status by number | Done | By button or by sending the number inside any message; answers only the reporting phone; Urdu and English, with the reason if there is one. |
| WA-06 opt-in, rate limit, 24-hour window | Done | Writing to us counts as consent for replies; STOP/START; more than 30 messages in 10 minutes from one phone are ignored (told once); outside 24 hours only approved templates are sent, and a message with no template is not sent on WhatsApp. |
| WA-07 scripts reviewed by a native speaker | **Not done: needs people** | Every text is a first draft in `WhatsAppScripts.cs`. Tests only check structure (both languages, button and message length limits). |
| WA-08 contract tests with recorded payloads | Done with hand-written samples | 13 samples of the documented shapes (text, photo, voice, pin, buttons, lists, receipts, failures, unknown fields). **Not captured from a live account.** |
| DS-06 conversation design | Done | `docs/whatsapp-conversation.md`. |
| NT-02 WhatsApp adapter, templates | Done (against fakes) | Sent / delivered / read / failed are kept on the message and on the notification, out-of-order receipts handled. Template names and parameters are configuration; the five templates to submit (English and Urdu) are listed. **Templates are not approved by Meta; nothing was sent to Meta.** |
| NT-03 text-message gateway | Done as a configurable adapter | A JSON-over-HTTP sender whose request shape is configuration (no provider chosen yet), used for codes and as the fallback when WhatsApp cannot deliver. Fallback tested; **the adapter has not met a real gateway.** Someone who sent STOP gets no text either. |
| NT-04 email | Built, **not run against a mail server** | SMTP sender and a channel for people who chose email. |
| NT-07 delivery receipts and rates | Done | Receipts applied from the webhook; `GET /reports/delivery` gives counts and failure rate per channel (no portal screen yet). |

Needs from you before the pilot: a Meta business account and number (WA-01), templates approved, the access token and app secret, a text-message provider, an SMTP account, and a row in `whatsapp_channels` for each society.

## Assumptions to confirm

- Working hours 09:00 to 18:00, Sunday off, when a policy is set to count working hours only. These are fixed in code, not yet per organization.
- Location risk, auto-route and SLA settings have API endpoints but **no portal screens yet** (WP-12).
- The 0.5 floor for the auto-route threshold.
- Numbers starting with 0 are read as Pakistan (+92).
- The reminder wording reuses the existing "please confirm" message.
- The monthly AI allowance of 2,000,000 tokens is a placeholder.

## Not started

MA-04 push messages (needs FCM/APNs accounts, FD-16), RA-09 notices feed (needs the notices backend, NT-08), WA-07 sign-off (people), and Wave 4 follow-ups listed above (portal screens for the new settings, reports and queues; WP-12).

Wave 5 onward is not started.
