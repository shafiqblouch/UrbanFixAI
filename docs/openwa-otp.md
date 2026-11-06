# OTP over WhatsApp in development (OpenWA)

In development the OTP sender is `LoggingSmsSender`, so login codes only appear in the API log and never
reach a phone. For a real end-to-end test of resident phone login you can deliver the code over WhatsApp
through a self-hosted [OpenWA](https://github.com/rmyndharis/OpenWA) gateway — an unofficial WhatsApp API
that needs no paid SMS provider and no approved Cloud API template.

> OpenWA drives an **unofficial** WhatsApp client, so WhatsApp may restrict the number used. Use a spare/dev
> number, never a resident's. This is a development and demo aid only; production uses a real SMS gateway or
> the WhatsApp Cloud API.

## 1. Run the OpenWA gateway

```bash
git clone https://github.com/rmyndharis/OpenWA.git
cd OpenWA
docker compose -f docker-compose.dev.yml up -d
```

It listens on `http://localhost:2785` (API at `/api`, dashboard at the same host). Read the admin API key
that it generates on first boot:

```bash
docker exec openwa-api cat /app/data/.api-key
```

## 2. Link a WhatsApp number

Open the dashboard at `http://localhost:2785`, create a session (the id used below is `default`), start it,
and scan the QR code with WhatsApp on the phone that will *send* the codes. The session must show "connected"
before codes can go out.

## 3. Point UrbanFix at it

In `core/src/UrbanFix.Api/appsettings.Development.json`, fill in the `OpenWa` section and turn it on:

```jsonc
"OpenWa": {
  "Enabled": true,
  "BaseUrl": "http://localhost:2785",
  "ApiKey": "<the key from step 1>",
  "SessionId": "default",
  "ChatSuffix": "@c.us"
}
```

To exercise a *real* random code (not the fixed demo code), also remove the `"Otp": { "FixedCode": "123456" }`
line — while `FixedCode` is set, every code is `123456` and nothing is sent. With it removed, requesting a code
for an eligible resident sends a message like
`Your UrbanFix code is 481920. It expires in 5 minutes. Do not share it.` to that resident's WhatsApp.

## How it fits

When `OpenWa:Enabled` is true, `OpenWaSmsSender` is registered as the `ISmsSender` (it is registered last, so it
wins over the logging sender and any configured SMS gateway). The OTP flow is unchanged: `OtpService` still hands
the plain message to `ISmsSender`; only the delivery mechanism changes. The sender turns the stored E.164 number
(`+923001234567`) into an OpenWA chat id (`923001234567@c.us`) and calls
`POST /api/sessions/{SessionId}/messages/send-text` with the `X-API-Key` header.

Leaving `OpenWa:Enabled` false (the default) keeps the normal development behaviour, so this costs nothing when
unused and is never active in production, which does not configure the section.
