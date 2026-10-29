# Push notifications: what is built and what is needed (FD-16)

Push sends a notification to a person's phone or browser even when the app is closed. It is one more notification channel (`push`), so a complaint event can reach someone by the inbox, WhatsApp, text, email and push, according to their own choice.

## What is built

- A person's devices are registered at `POST /me/push-devices` (phone: an FCM token; browser: the address and two keys the browser gives). Registering turns the push channel on for that person. A device that changes hands goes to the new person.
- `POST /me/push-test` sends a test to all the person's devices and says what happened on each.
- Phones are reached through **Firebase Cloud Messaging** (HTTP v1). FCM also reaches iPhones once an Apple key is added in the Firebase console, so there is no separate Apple sender.
- Browsers are reached through the browser's own push service, signed with a server key (VAPID) and **encrypted** so the push service cannot read the message. The encryption is checked against the worked example in RFC 8291.
- The portal has a "Turn on notifications" button in the notifications panel and a service worker (`web/apps/portal/public/sw.js`) that shows the notification and opens the right screen when it is clicked.
- A device the service says is gone is dropped; one that has trouble is kept, counted, and tried again by the existing retry of the notification outbox. A person with push on but no device is recorded as "no push device" and not retried.

## What is not done, and why

- **Nothing has been sent to the real FCM or to a real browser.** The FCM sender is tested against a stand-in that checks the sign-in and the message it receives; the browser sender is tested for the signature, the encryption and how it reads each answer. The acceptance test "a test push reaches the mobile app and the portal" needs the accounts and keys below, and a phone.
- **The mobile app registers for push** (MA-04): Settings has a "Notifications on this phone" card that asks permission, gets the FCM token and posts it to `/me/push-devices`; tapping a notification opens its complaint or job (`mobile/src/pushLink.ts`). It has only been type-checked, unit-tested and bundled, never run on a phone. A real build needs the Firebase `google-services.json` for package `pk.urbanfix.app` set as `expo.android.googleServicesFile` in `mobile/app.json`, and a development or EAS build (Expo Go cannot receive FCM tokens).
- The portal button has not been tried in a real browser, only built.

## What you need to do

1. **Firebase project** for the app (also gives you the Apple route): create it, add the Android and iOS apps, upload an APNs key for iOS, and create a service-account key with the "Firebase Cloud Messaging API (V1)" permission.
2. **Browser key pair:** call `POST /admin/push/vapid-keys` as a platform admin once. Keep the private key in the vault.
3. Set in configuration (secrets from the vault, never the repository):

```
Push:Fcm:Enabled=true
Push:Fcm:ProjectId=<firebase project id>
Push:Fcm:ServiceAccountJson=<the whole service-account JSON>
Push:Web:Enabled=true
Push:Web:Subject=mailto:<an ops address>
Push:Web:PublicKey=<from step 2>
Push:Web:PrivateKey=<from step 2>
```

For development without accounts, set `Push:LogOnly=true`: pushes are written to the log instead of sent.

4. The portal must be served over **https** (browsers refuse push on plain http except on localhost).
