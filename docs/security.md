# Security: implemented controls

What the app does to keep each person's data to themselves, and how authentication and authorization
are enforced. Last reviewed 2026-11-10. File references are the source of truth; this page summarizes them.

## 1. Shared-device data isolation (mobile)

One phone is often used by more than one resident. Receipts, the unsent complaint draft and the upload
queue are cached on the device so they can be read with no signal — under device-global keys, not per
account. Without isolation, a new resident would see (and tap into) the previous resident's receipts.

**Control** — on-device user data is bound to the person it belongs to and wiped when that changes.
See `mobile/src/offline/store.ts` and `mobile/src/session.tsx`.

| When | What happens | Code |
| --- | --- | --- |
| A person signs in / the app relaunches (after `/me`) | `reconcileUserData(userId)` wipes all cached data unless it is already recorded as **this** person's, then records ownership | `session.tsx` → `store.ts` |
| The same person signs back in (e.g. token re-auth) | Nothing is wiped — their receipts, draft and pending uploads are kept | `reconcileUserData` early return |
| A **different** person signs in | Receipts, draft, upload queue and upload sessions are wiped; the new person becomes owner | `reconcileUserData` |
| Legacy data from before ownership was tracked | Treated as not-this-person's and wiped on first sign-in | `reconcileUserData` (owner `null`) |
| Sign-out | `clearUserData()` wipes all of the above **and** forgets the owner | `session.tsx` `signOut` |

Keys covered: `uf.receipts`, `uf.draft`, `uf.uploadQueue`, `uf.uploadSessions` (owner marker: `uf.dataOwner`).

**Fail-safe:** ownership is recorded **only after every key is confirmed removed**
(`Promise.allSettled` → owner write gated on all-fulfilled). If a storage removal fails, ownership is
left unchanged so the next sign-in retries the wipe — the app never claims the device for a new person
while another person's data might still be present.

**Tested:** `mobile/src/offline/userData.test.ts` (16 cases: same/different/legacy owner, hand-off chains,
real queue/session accessors, unreadable owner, partial-wipe fail-safe, sign-out, end-to-end regression).

## 2. Authentication

- **Residents:** phone number + one-time code (OTP). `mobile/src/screens/LoginScreen.tsx` → `/auth/otp/request`, `/auth/otp/verify`.
- **Staff:** email + password + TOTP authenticator code; first-time staff must enrol an authenticator before they can continue. Same screen → `/auth/login`, `/auth/totp/enroll`, `/auth/totp/confirm`.
- **Session token** is stored in the OS secure store (Keychain / Android Keystore via `SecureStore`), **not** in AsyncStorage, and sent as `Authorization: Bearer`. On sign-out it is deleted (`setToken(undefined)`). See `mobile/src/api.ts`.

## 3. Authorization (server, defense in depth)

The server never relies on the client to scope data. Residents can only reach their own complaints.

- `CanSee(actor, complaint)` = holds `ComplaintRead` **or** is the reporter (`ReportedByUserId == UserId`). `core/src/UrbanFix.Api/Endpoints/ComplaintEndpoints.cs`.
- Reading another person's complaint by id or by reference returns **404 Not Found**, not 403 — someone else's reference "looks exactly like one that does not exist", so no existence is leaked.
- The resident complaint list is filtered to `ReportedByUserId == actor.UserId` (`ComplaintReadOwn`), and endpoints require `AuthPolicies.Verified` or a specific permission.

Net effect: even if a stale receipt id reached the client, the server refuses to serve the complaint —
the data-isolation fix removes the stale **local** copy; the server is the backstop for server data.

## 4. Transport

- API calls attach the Bearer token over `fetch`; production must use HTTPS. The server at the deployment target sits behind Cloudflare (see `docs/deploy-server.md`).

## Known gaps / hardening backlog

Not yet implemented — tracked from the 2026-11-10 review:

| Item | Risk | Note |
| --- | --- | --- |
| Push registration not revoked on sign-out | Low–Med | A `DELETE /me/push-devices/{id}` endpoint exists (`PushEndpoints.cs`); the client should call it at sign-out so a later user on the same phone cannot receive the previous user's notifications. Partially self-heals once the next user enables push (`RegisterAsync` reassigns the token). |
| `uf.lastRole` not cleared on sign-out | Info | Leaks the previous user's last-acted role; add to `clearUserData`'s scope. |
| AsyncStorage unencrypted; `android:allowBackup` not disabled | Low | Receipt descriptions may contain PII; disable backup for release builds, consider encrypted storage. |
| `usesCleartextTraffic: true` (`mobile/app.json`) | Low–Med | Needed for the local dev server; ensure release builds use HTTPS and disable cleartext. |
