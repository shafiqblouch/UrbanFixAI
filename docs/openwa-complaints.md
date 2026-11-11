# Registering complaints over WhatsApp (OpenWA)

Residents can report a problem by sending a WhatsApp message — text, photo or voice note — to the society's
WhatsApp number. The [OpenWA](https://github.com/rmyndharis/OpenWA) gateway receives it, POSTs it to UrbanFix,
and the message enters the **same** complaint flow the WhatsApp Cloud API uses. The resident gets a reply with
the reference, e.g. *"Your complaint is registered. Reference: UF-2026-00125."*

```
Resident ─▶ WhatsApp number ─▶ OpenWA gateway ─▶ POST /webhooks/openwa (HMAC-signed)
          ◀── reply with reference ◀── OpenWA send-text ◀── UrbanFix (inbox → conversation → complaint)
```

This reuses the existing conversation engine, so you get its behaviour for free:

- **Text** of a few words or more becomes a complaint; a short greeting ("hi", "menu", "salam") gets the menu.
- Sending a **reference** (`UF-2026-00125`) back returns that complaint's status — but only to the number that
  reported it.
- **Photos and voice notes** are attached to the complaint (voice notes are transcribed). OpenWA delivers the
  media inline, so no extra download step is needed.
- **Urdu / English** is detected from the message; replies follow.
- Unknown numbers are handled by the society's sender policy (guest / hold-for-staff / ask-to-register), and
  a flood of messages from one number is rate-limited — all as on the Cloud API path.

**Buttons become text.** Unofficial WhatsApp cannot render tappable buttons, so prompts that had buttons are sent
as plain text options the resident types back ("done", "report", or a reference). The state machine already
accepts all of these typed, so nothing is lost.

> OpenWA drives an **unofficial** WhatsApp client — WhatsApp may restrict the number. Use a dedicated number for
> the society line, not a personal one. This is for the pilot/test deployment; a production rollout should move to
> the WhatsApp Cloud API (set the `WhatsApp` section instead, and this OpenWA path stays off).

## How it is wired

- `OpenWaWebhook.Parse` reads OpenWA's `message.received` envelope into the same `ParsedWebhook` the Cloud API
  produces, so one `WhatsAppInbox` → `WhatsAppConversationService` → `ComplaintIntake` serves both.
- When `OpenWa:InboundEnabled` is on, `OpenWaWhatsAppClient` becomes the WhatsApp client (sending replies and
  reading inline media); the Cloud API client is not registered.
- A `whatsapp_channel` row maps the **OpenWA session id** to a society; it is seeded on startup from
  `OpenWa:OrganizationId`. That row is what lets inbound messages find their organization and replies go out.
- The webhook is verified with `X-OpenWA-Signature: sha256=<hmac>` over the raw body, keyed by the shared secret.

## Turn it on (server)

The `openwa` service and a linked WhatsApp number are already set up for OTP (see
[openwa-otp.md](openwa-otp.md)). To also accept complaints, add to the server `.env`:

```bash
OPENWA_INBOUND=true
OPENWA_WEBHOOK_SECRET=<a random string, 16+ chars>     # openssl rand -hex 24
OPENWA_ORG_ID=<the society's organization id (uuid)>   # which society this number serves
OPENWA_WEBHOOK_URL=https://urbanfix.vitonta.com/webhooks/openwa
OPENWA_DISPLAY_NUMBER=923105207595                     # optional, for display only
```

Find the organization id with:
```bash
docker compose exec -T postgres psql -U urbanfix -tAc "SELECT id, name FROM organizations;" < /dev/null
```
Then recreate core:
```bash
cd ~/UrbanFixAI/deploy/server && docker compose up -d core
```

On startup core seeds the channel and, because `OPENWA_WEBHOOK_URL` is set, tells the gateway to deliver here.
If you prefer to register the webhook by hand (the gateway's port is not published, so run it from the box):

```bash
K=$OPENWA_API_KEY; S=$OPENWA_SESSION_ID
docker compose exec -T openwa curl -s -X POST "http://localhost:2785/api/sessions/$S/webhooks" \
  -H "X-API-Key: $K" -H 'Content-Type: application/json' \
  -d "{\"url\":\"https://urbanfix.vitonta.com/webhooks/openwa\",\"events\":[\"message.received\"],\"secret\":\"$OPENWA_WEBHOOK_SECRET\"}" < /dev/null
```

## Test it

1. From a WhatsApp number that is registered as a **resident** of that society, message the number:
   *"The lift on block C is not working."*
2. You should receive a reply with a reference like `UF-2026-00125`, and the complaint should appear in the portal.
3. Send a photo — it attaches to the complaint. Send the reference back — you get the status.

Every inbound message is stored in `whatsapp_messages` (deduplicated by message id) with how it was handled, so a
replayed webhook changes nothing and failures are visible there.
