# UrbanFix mobile app

React Native with Expo (decision and reasons: [`docs/MA-00-mobile-framework.md`](../docs/MA-00-mobile-framework.md)). Residents report problems and technicians work their jobs, in English and Urdu (right to left).

```powershell
cd mobile
pnpm install        # the shared packages in ../web/packages are linked, so run `pnpm install` in ../web first
pnpm test           # logic tests (uploads, queue, receipts, costs, roles, translations)
pnpm lint           # tsc
pnpm bundle         # Metro bundles the app for Android: proves everything, shared packages included, compiles
$env:EXPO_PUBLIC_API_URL = "http://192.168.1.20:5181"; pnpm start   # run on a phone with Expo Go or a dev build
```

`EXPO_PUBLIC_API_URL` defaults to `http://10.0.2.2:5181`, which is the host machine as seen from the Android emulator.

## What is here

| Area | Where |
| --- | --- |
| Sign-in: residents by phone code, staff by email, password and authenticator | `src/screens/LoginScreen.tsx` |
| Resident extras: find by number, feedback, answer requests for information, emergency prompt | `src/screens/residentExtras.tsx` |
| Resident: home with quick actions, complaint form (text, photo, voice note, landmark, location), receipts kept offline, complaint status | `src/screens/resident.tsx` |
| Technician: today's jobs with due-time chips, job detail, accept, start, notes, before/after photos, checklist, cannot-complete, estimate and actual costs | `src/screens/technician.tsx` |
| Role switcher, language, upload status, sign-out | `src/screens/SettingsScreen.tsx` |
| Resumable chunked uploads and the persistent upload queue | `src/upload/` |
| Receipts, draft complaint, queue storage | `src/offline/` |
| Native modules behind small adapters (camera, compression, voice, files, location, network, secure storage) | `src/platform/` |
| Native versions of the shared components, driven by the design tokens | `src/ui/` |

## Not verified

Built without a phone. Checked: TypeScript, the logic tests, and a Metro bundle for Android. **Not** checked: running on a device, the camera, microphone and location permission flows, behaviour on throttled 3G, speed on a low-end phone, and Urdu rendering at small sizes. See the last section of the MA-00 document.
