# MA-00: mobile framework

**Decision: React Native with Expo (TypeScript).** Status: proposed by the build, **please confirm or veto before the pilot**. Nothing else in the repo depends on it except `mobile/`.

## Why

| Need | How React Native + Expo meets it |
| --- | --- |
| One team, one language | The portal is TypeScript and React. The mobile app reuses the design tokens (`@urbanfix/tokens`), the framework-free i18n and RTL core (`@urbanfix/ui/core`), the generated API client (`@urbanfix/api-client`) and its error handling. A contract change breaks the app at compile time, the same as the portal. |
| Urdu and right-to-left | Layout is mirrored in JavaScript (`useRtl`), so switching language needs no app restart. Noto Naskh Arabic is bundled, because low-end phones often lack an Urdu font. |
| Low-end Android | Hermes, plain `View`/`Text` screens, no maps SDK, images compressed to at most 500 KB before upload. This is the part that must be measured on a real phone (see "Not verified"). |
| Spotty networks | Chunked, resumable uploads against the existing `/uploads` API, a persistent upload queue, receipts and drafts kept on the device. FD-17 (the shared SQLite sync engine) is Phase 2a and replaces the simple queue. |
| Updates without a store release | Expo's over-the-air updates for JavaScript changes. |

## Considered

- **Flutter.** Better raw rendering and one binary behaviour, but a second language and no reuse of the tokens, i18n, API client or tests. Rejected for the POC team size.
- **Native Kotlin.** Best performance, Android only, double the work for any later iOS. Rejected.
- **Progressive web app.** Cannot reliably record voice notes, capture and compress photos, or queue uploads when the browser is killed. Rejected for field staff; the portal already works in a mobile browser for office use.

## How the app is organised

```
mobile/src/
  platform/   thin adapters over the native modules (camera, compression, voice, files, location, network, secure storage)
  upload/     resumable chunk uploader and the persistent queue: plain TypeScript, unit-tested with a fake server
  offline/    receipts, drafts, queue persistence
  ui/         native versions of the shared components (DS-02), driven by the same tokens
  screens/    resident, technician, shared
```

Everything that decides behaviour (uploads, compression targets, the queue, receipts, role menus) lives outside the native modules and is tested in Node. The adapters stay small so that swapping an Expo module version touches one file each.

## Not verified here

This was built on a machine with no phone, tablet or emulator. What was checked: TypeScript, unit tests of the logic, and that Metro bundles the app for Android. What was **not**: running on a device, scroll and start-up speed on a low-end phone, the camera, microphone and location permission flows, behaviour on throttled 3G, and the Urdu font rendering at small sizes outdoors (DS-03). MA-01's done-when ("builds and runs on a test phone and tablet") and RA-01, RA-02, TA-01 and TA-04's performance and field criteria need a person with a device.

## Before a store release

- Move to the Expo SDK current at that time and the target Android API level the store requires (MA-06).
- Replace the simple upload queue with the shared sync engine (FD-17).
- Push notifications (MA-04, FD-16) are Phase 1; the in-app inbox works today over the same API as the portal.
