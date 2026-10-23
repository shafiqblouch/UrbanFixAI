# WhatsApp setup and message templates (WA-01, NT-02)

WhatsApp only lets a business start a conversation, or write more than 24 hours after the person's last message, with a **template that Meta has approved**. These are the ones this system can use. Nothing is sent outside the 24-hour window unless the template for that message exists and is listed in the configuration.

**This part needs people, not code:** a Meta business account, a verified business, a phone number for the society, and the templates below submitted for approval in English and Urdu. Approval takes days and can be refused; start early (WA-01).

## Configuration

```json
"WhatsApp": {
  "Enabled": true,
  "AppSecret": "<from the Meta app: signs every webhook>",
  "VerifyToken": "<any long random string; also typed into Meta when registering the webhook>",
  "AccessToken": "<a system user token allowed to send messages and read media>",
  "Templates": {
    "complaint_received": { "Name": "uf_complaint_received", "Parameters": ["reference"] }
  }
}
```

Register the webhook in Meta as `https://<your-api>/webhooks/whatsapp`, subscribed to the `messages` field. Give each society its number with a row in `whatsapp_channels` (`phone_number_id` is Meta's id for the number, not the number itself); there is no screen for this yet.

Keys under `Templates` are the notification names below; `Name` is the template's name in Meta; `Parameters` are the values to put in `{{1}}`, `{{2}}`… in order. The language sent is `en` or `ur` from the person's language setting, so **each template must be approved in both**.

## Templates to submit

Category: *Utility*. Variables are shown as `{{1}}`.

| Notification | Name in config | Variables | English | Urdu |
| --- | --- | --- | --- | --- |
| complaint_received | uf_complaint_received | reference | Your complaint {{1}} has been received. We will review it shortly. | آپ کی شکایت {{1}} موصول ہو گئی ہے۔ ہم جلد اس کا جائزہ لیں گے۔ |
| complaint_assigned | uf_complaint_assigned | reference | Your complaint {{1}} has been assigned to our team. | آپ کی شکایت {{1}} ہماری ٹیم کو سونپ دی گئی ہے۔ |
| complaint_rejected | uf_complaint_rejected | reference, reason | Your complaint {{1}} was not accepted. Reason: {{2}} | آپ کی شکایت {{1}} قبول نہیں کی گئی۔ وجہ: {{2}} |
| confirmation_request | uf_confirmation_request | reference | Work on {{1}} is complete. Please confirm that the problem is fixed. | {{1}} پر کام مکمل ہو گیا ہے۔ براہ کرم تصدیق کریں کہ مسئلہ حل ہو گیا ہے۔ |
| safety_prompt | uf_safety_prompt | reference | We have flagged {{1}} as urgent for safety. If anyone is in danger, call Rescue 1122 now. Keep away from the hazard. | ہم نے {{1}} کو حفاظت کے لیے فوری قرار دیا ہے۔ اگر کسی کی جان خطرے میں ہو تو ابھی ریسکیو 1122 پر کال کریں۔ خطرے والی جگہ سے دور رہیں۔ |

Staff messages (job assigned, escalations) go to staff by the app and the portal; they are not planned for WhatsApp templates.

Only `complaint_received` is wired in the sample configuration above; add the others once Meta approves them. Until then those messages go by text message when the person is outside the window.

## Checked and not checked

Checked with tests: signature verification, replay safety, reading the documented webhook shapes, the whole chat, the 24-hour rule, the template path, delivery receipts, the text-message fallback.

**Not checked: anything against Meta itself.** No real account was available. The webhook samples in `core/tests/UrbanFix.Tests/Fixtures/whatsapp` are written by hand from Meta's documented shapes, not captured from a live account; replace or add real captures when the pilot number is connected. The request shapes for sending (text, buttons, template, media download) follow the documentation but have never been sent to the real API, and button titles over 20 characters or more than three buttons would be refused by it (the scripts are tested against both limits).
