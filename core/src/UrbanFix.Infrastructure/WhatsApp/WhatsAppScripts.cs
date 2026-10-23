using UrbanFix.Complaints;

namespace UrbanFix.Infrastructure.WhatsApp;

/// <summary>
/// What the chat says, in English and Urdu (DS-06, WA-07). Short on purpose: people read these on small screens, often in a hurry.
/// FIRST DRAFT: the wording has not been reviewed by a native speaker or pilot staff yet; that sign-off (WA-07) is still to do.
/// Button titles must stay within WhatsApp's 20-character limit and message bodies within 1024; a test enforces both.
/// </summary>
public static class WhatsAppScripts
{
    public const int MaxButtonTitle = 20;
    public const int MaxBody = 1024;

    private static readonly Dictionary<string, (string En, string Ur)> Texts = new()
    {
        // Buttons
        ["btn.report"] = ("Report a problem", "شکایت درج کریں"),
        ["btn.status"] = ("Check status", "حالت دیکھیں"),
        ["btn.language"] = ("اردو", "English"),
        ["btn.done"] = ("Done", "مکمل"),
        ["btn.cancel"] = ("Cancel", "منسوخ کریں"),
        ["btn.continue"] = ("Continue", "جاری رکھیں"),
        ["btn.startover"] = ("Start again", "نئے سرے سے"),
        ["btn.sendnow"] = ("Send as it is", "ایسے ہی بھیجیں"),

        // Messages
        ["welcome"] = (
            "Assalam o Alaikum! Welcome to {society}. I can help you report a problem or check a complaint.\n\nالسلام علیکم! {society} میں خوش آمدید۔ میں شکایت درج کرنے یا شکایت کی حالت بتانے میں مدد کر سکتا ہوں۔",
            "السلام علیکم! {society} میں خوش آمدید۔ میں شکایت درج کرنے یا شکایت کی حالت بتانے میں مدد کر سکتا ہوں۔"),
        ["menu"] = ("How can I help?", "میں کیسے مدد کر سکتا ہوں؟"),
        ["ask_description"] = (
            "Please tell me what the problem is, in a message. You can also send a photo or a voice note.",
            "براہ کرم پیغام میں بتائیں کہ مسئلہ کیا ہے۔ آپ تصویر یا آواز کا پیغام بھی بھیج سکتے ہیں۔"),
        ["got_media"] = (
            "Got it. Please also write a short description of the problem, or send it as it is.",
            "مل گیا۔ براہ کرم مسئلے کی مختصر تفصیل بھی لکھیں، یا ایسے ہی بھیج دیں۔"),
        ["created"] = (
            "Thank you. Your complaint number is {reference}.\n\nYou can add more now: photos, a voice note, or your location pin. Tap Done when you have finished.",
            "شکریہ۔ آپ کی شکایت کا نمبر {reference} ہے۔\n\nآپ ابھی مزید تصاویر، آواز کا پیغام یا اپنی لوکیشن بھیج سکتے ہیں۔ مکمل ہونے پر \"مکمل\" دبائیں۔"),
        ["needs_location"] = (
            "We still need to know where this is. Please share your location pin, or type your street and house number.",
            "ہمیں یہ جاننا ہے کہ یہ کہاں ہے۔ براہ کرم اپنی لوکیشن بھیجیں، یا گلی اور مکان نمبر لکھیں۔"),
        ["sender_check"] = (
            "Our office will check your number before the complaint is reviewed.",
            "شکایت کا جائزہ لینے سے پہلے ہمارا دفتر آپ کا نمبر جانچے گا۔"),
        ["emergency"] = (
            "If anyone is in danger, call Rescue 1122 now and keep away from the hazard.",
            "اگر کسی کی جان خطرے میں ہو تو ابھی ریسکیو 1122 پر کال کریں اور خطرے والی جگہ سے دور رہیں۔"),
        ["detail_added"] = ("Added to {reference}.", "{reference} میں شامل کر دیا گیا۔"),
        ["media_added"] = ("Added to {reference}.", "{reference} میں شامل کر دیا گیا۔"),
        ["media_failed"] = ("I could not read that file. Please send it again.", "میں یہ فائل نہیں پڑھ سکا۔ براہ کرم دوبارہ بھیجیں۔"),
        ["location_pin"] = ("Location noted. Thank you.", "لوکیشن درج کر لی گئی۔ شکریہ۔"),
        ["location_text"] = ("Thank you. The office will confirm the place.", "شکریہ۔ دفتر جگہ کی تصدیق کر لے گا۔"),
        ["done"] = (
            "All saved. Your complaint is {reference}. We will message you when something changes. To check it later, send the number {reference}.",
            "سب محفوظ ہو گیا۔ آپ کی شکایت {reference} ہے۔ کوئی تبدیلی ہوئی تو آپ کو پیغام دیں گے۔ بعد میں دیکھنے کے لیے نمبر {reference} بھیجیں۔"),
        ["ask_reference"] = (
            "Please send the complaint number, for example UF-2026-00042.",
            "براہ کرم شکایت کا نمبر بھیجیں، مثلاً UF-2026-00042۔"),
        ["status"] = ("{reference}: {status}.{reason}", "{reference}: {status}۔{reason}"),
        ["status_reason"] = (" Note: {reason}", " نوٹ: {reason}"),
        ["status_not_found"] = (
            "I could not find a complaint with that number for this phone. Please check the number, or message from the phone that reported it.",
            "اس فون کے لیے اس نمبر کی کوئی شکایت نہیں ملی۔ براہ کرم نمبر دیکھ لیں، یا اسی فون سے پیغام بھیجیں جس سے شکایت درج کی تھی۔"),
        ["unsupported"] = (
            "Sorry, I can only read text, photos, voice notes and a location pin.",
            "معذرت، میں صرف تحریر، تصاویر، آواز کے پیغامات اور لوکیشن پڑھ سکتا ہوں۔"),
        ["not_registered"] = (
            "This number is not registered with {society}. Please ask the office to register your number, then try again.",
            "یہ نمبر {society} میں رجسٹر نہیں ہے۔ براہ کرم دفتر سے اپنا نمبر رجسٹر کروائیں، پھر دوبارہ کوشش کریں۔"),
        ["resume"] = (
            "Welcome back. You were in the middle of something. Continue, or start again?",
            "خوش آمدید۔ آپ ایک کام کے درمیان تھے۔ جاری رکھیں یا نئے سرے سے شروع کریں؟"),
        ["cancelled"] = ("Cancelled. Send a message any time to start again.", "منسوخ کر دیا گیا۔ دوبارہ شروع کرنے کے لیے کبھی بھی پیغام بھیجیں۔"),
        ["already_saved"] = (
            "Your complaint {reference} is already saved. Tap Done to finish.",
            "آپ کی شکایت {reference} پہلے ہی محفوظ ہے۔ ختم کرنے کے لیے \"مکمل\" دبائیں۔"),
        ["optout"] = (
            "You will not get messages from us any more. Send START to turn them back on.",
            "اب آپ کو ہماری طرف سے پیغامات نہیں ملیں گے۔ دوبارہ چالو کرنے کے لیے START بھیجیں۔"),
        ["optin"] = ("Messages are back on.", "پیغامات دوبارہ چالو کر دیے گئے۔"),
        ["slow_down"] = (
            "You are sending messages very quickly. Please wait a few minutes.",
            "آپ بہت تیزی سے پیغامات بھیج رہے ہیں۔ براہ کرم چند منٹ انتظار کریں۔"),
        ["error"] = (
            "Sorry, something went wrong on our side. Your message is saved. Please try again in a minute.",
            "معذرت، ہماری طرف سے کچھ خرابی ہو گئی۔ آپ کا پیغام محفوظ ہے۔ براہ کرم ایک منٹ بعد دوبارہ کوشش کریں۔"),
    };

    private static readonly Dictionary<ComplaintStatus, (string En, string Ur)> Statuses = new()
    {
        [ComplaintStatus.New] = ("received", "موصول ہو گئی"),
        [ComplaintStatus.OnHold] = ("on hold until we have the details we need", "ضروری تفصیل ملنے تک روکی گئی ہے"),
        [ComplaintStatus.UnderReview] = ("being reviewed", "جائزے میں ہے"),
        [ComplaintStatus.Assigned] = ("assigned to our team", "ہماری ٹیم کو سونپ دی گئی"),
        [ComplaintStatus.InProgress] = ("work is under way", "کام جاری ہے"),
        [ComplaintStatus.Resolved] = ("work is finished; please confirm it is fixed in the app", "کام مکمل ہو گیا؛ براہ کرم ایپ میں تصدیق کریں کہ مسئلہ حل ہو گیا"),
        [ComplaintStatus.Closed] = ("closed", "بند کر دی گئی"),
        [ComplaintStatus.Rejected] = ("not accepted", "قبول نہیں کی گئی"),
        [ComplaintStatus.Merged] = ("joined to another complaint about the same problem", "اسی مسئلے کی دوسری شکایت میں شامل کر دی گئی"),
    };

    public static IEnumerable<string> Keys => Texts.Keys;

    public static string Get(string key, string language, params (string Name, string? Value)[] args)
    {
        var (en, ur) = Texts[key];
        var text = language == "ur" ? ur : en;
        foreach (var (name, value) in args) text = text.Replace("{" + name + "}", value ?? "", StringComparison.Ordinal);
        return text;
    }

    public static string StatusText(ComplaintStatus status, string language) => language == "ur" ? Statuses[status].Ur : Statuses[status].En;

    public static IEnumerable<(string En, string Ur)> AllTexts => Texts.Values;
}
