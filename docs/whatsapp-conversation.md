# WhatsApp conversation design (DS-06) and script review (WA-07)

This is what residents will see in the chat, as built. **The wording is a first draft. WA-07 is still open: a native Urdu speaker and the pilot society's staff must read every line below and sign it off.** The texts live in `core/src/UrbanFix.Infrastructure/WhatsApp/WhatsAppScripts.cs`; change them there.

## How a chat goes

1. **First hello** (any greeting, or the menu word): a welcome in English and Urdu, then three buttons: *Report a problem*, *Check status*, and a language switch (*اردو* / *English*).
2. **Report a problem** has three routes, all ending in the same place:
   - Tap *Report a problem*, then write what is wrong.
   - Just type the problem (12 characters or more). That is the whole report.
   - Send a photo or a voice note first. The chat asks for a few words, or offers *Send as it is*.
3. **After the complaint is saved** the chat gives the complaint number and says what can still be added: more words, photos, a voice note, a location pin. *Done* ends it. Nothing is lost if the person stops here.
4. **Place.** A resident linked to one house needs nothing more. Otherwise the chat asks for a location pin or a typed street and house number. A pin within 150 metres of a place the society has entered is matched to it and the complaint goes on its way; a pin that matches nothing, or a typed address, is kept for the office to check.
5. **Check status.** Tap *Check status* and send the number, or just send the number (even inside a sentence) at any time. The answer is in the person's language and only goes to the phone that reported the complaint.
6. **Language** follows the script of the first message (Urdu script means Urdu). Roman Urdu cannot be told from English by script, so it is answered in English; the language button switches.
7. **Stop / Start.** *STOP* turns off messages we start; *START* turns them back on. The person can still write to us and be answered.

## Rules the chat follows

| Rule | What happens |
| --- | --- |
| Unknown phone number | Follows the society's policy (set by a manager): hold for checking (default), accept as a guest, or ask the person to register first. The reply says which. |
| Danger words (any language) | The complaint is flagged for safety, supervisors are alerted, and the reply says to call Rescue 1122. Same word list as the app and the server. |
| Long pause in the middle of a report | After 6 hours of quiet the chat asks *Continue* or *Start again* before doing anything. |
| Too many messages | More than 30 from one phone in 10 minutes: the rest are ignored, and the person is told once. |
| Same message delivered twice by WhatsApp | Stored once; handled once. |
| Something we cannot read (sticker, document, video) | A polite reply listing what we can read, then the menu. |
| A photo or voice note that cannot be fetched or is not a real image or sound | The person is told to send it again; the complaint is kept. |
| An internal fault | The person is told their message is saved and to try again; the message is marked failed for staff to see. It is never retried automatically, so it cannot create a second complaint. |

## Messages we start (proactive)

Only for people who chose WhatsApp as a way to be told, who have a phone number on file, and who have not sent STOP.

- Within 24 hours of their last message: sent as a normal message.
- After that: only an approved template may be sent (see `whatsapp-templates.md`). A notification with no template is **not sent on WhatsApp**.
- If WhatsApp cannot send it (no template, refused, or reported undeliverable later), the same text is sent as a **text message**, once. Someone who sent STOP gets neither.

## Not built yet

Buttons for *approve / reject* (WA-09), the Phase 2 menu items (DS-07), and a resident registering themselves by code over WhatsApp (the "register first" policy only tells the person to ask the office).
