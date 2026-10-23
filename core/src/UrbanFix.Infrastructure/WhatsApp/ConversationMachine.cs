using System.Text.Json;
using System.Text.RegularExpressions;

namespace UrbanFix.Infrastructure.WhatsApp;

public enum InputKind { Text, Button, Image, Audio, Location, Unsupported }

/// <summary>One thing a person sent, reduced to what the conversation cares about.</summary>
public sealed record ChatInput(InputKind Kind, string? Text = null, string? ButtonId = null, string? MediaId = null, double? Latitude = null, double? Longitude = null, string? PlaceName = null);

public sealed record PendingMedia(string MediaId, string Kind);

/// <summary>What is remembered between messages. Stored as JSON on the conversation row.</summary>
public sealed class ChatData
{
    public Guid? ComplaintId { get; set; }
    public string? Reference { get; set; }
    /// <summary>Photos and voice notes sent before the person described the problem; attached once the complaint exists.</summary>
    public List<PendingMedia> Pending { get; set; } = [];
    public double? PinLatitude { get; set; }
    public double? PinLongitude { get; set; }
    public string? PinName { get; set; }
    /// <summary>The complaint has no place yet and the next typed line is taken as the address.</summary>
    public bool AwaitingAddress { get; set; }
    /// <summary>Where to go back to if the person chooses to continue after a long pause.</summary>
    public string? ResumeState { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static ChatData FromJson(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new ChatData() : JsonSerializer.Deserialize<ChatData>(json, Json) ?? new ChatData(); }
        catch (JsonException) { return new ChatData(); }
    }
}

public static class ChatStates
{
    public const string Idle = "idle";
    public const string AwaitDescription = "await_description";
    public const string Collecting = "collecting";
    public const string AwaitStatusReference = "await_status_reference";
    public const string ConfirmResume = "confirm_resume";
}

/// <summary>What the service must do for a step. Replies that depend on a result (a new reference, a failed download) are made by the service.</summary>
public abstract record ChatAction;
public sealed record Say(string Key, params (string Name, string? Value)[] Args) : ChatAction;
public sealed record SayWithButtons(string Key, IReadOnlyList<(string Id, string TitleKey)> Buttons, params (string Name, string? Value)[] Args) : ChatAction;
public sealed record OpenComplaint(string Description, bool DescriptionIsPlaceholder, IReadOnlyList<PendingMedia> Media, double? PinLatitude, double? PinLongitude, string? PinName) : ChatAction;
public sealed record AppendDescription(string Text) : ChatAction;
public sealed record AttachMedia(PendingMedia Media) : ChatAction;
public sealed record SetPin(double Latitude, double Longitude, string? Name) : ChatAction;
public sealed record SetAddressText(string Text) : ChatAction;
public sealed record LookupStatus(string Reference) : ChatAction;
public sealed record FinishComplaint : ChatAction;
public sealed record OptOut : ChatAction;
public sealed record OptIn : ChatAction;

public sealed record ChatStep(string State, ChatData Data, string Language, IReadOnlyList<ChatAction> Actions);

public sealed record ChatSnapshot(string State, string? Language, ChatData Data, TimeSpan SinceLastMessage);

/// <summary>
/// The complaint conversation (WA-04, WA-05, DS-06) as pure rules: given where the chat is and what was just sent, say where it
/// goes next and what to do. No database, no network, so every path is tested by playing a script through it.
/// </summary>
public static partial class ConversationMachine
{
    public const string Urdu = "ur";
    public const string English = "en";
    public const int MinFreeTextLength = 12;

    [GeneratedRegex(@"\bUF-\d{4}-\d{5}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReferencePattern();

    private static readonly HashSet<string> Greetings = ["hi", "hello", "hey", "salam", "salaam", "assalam o alaikum", "assalamualaikum", "assalamu alaikum", "menu", "help", "سلام", "السلام علیکم", "ہیلو", "مدد", "مینو"];
    private static readonly HashSet<string> OptOutWords = ["stop", "unsubscribe", "روکیں"];
    private static readonly HashSet<string> OptInWords = ["start", "subscribe", "شروع"];
    private static readonly HashSet<string> ReportWords = ["report", "complaint", "problem", "شکایت"];
    private static readonly HashSet<string> DoneWords = ["done", "finished", "مکمل", "ختم"];
    private static readonly HashSet<string> CancelWords = ["cancel", "منسوخ"];
    private static readonly HashSet<string> UrduWords = ["urdu", "اردو"];
    private static readonly HashSet<string> EnglishWords = ["english", "انگریزی"];

    public static string DetectLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return English;
        var letters = text.Count(char.IsLetter);
        var arabic = text.Count(c => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ');
        return letters > 0 && arabic * 2 >= letters ? Urdu : English;
    }

    public static string? FindReference(string? text) => text is null ? null : ReferencePattern().Match(text) is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    private static string Normal(string? text) => (text ?? "").Trim().Trim('.', '!', '؟', '?').ToLowerInvariant();

    private static readonly (string Id, string TitleKey)[] MenuButtons = [("report", "btn.report"), ("status", "btn.status"), ("lang", "btn.language")];

    private static ChatStep Menu(string language, bool first, ChatData? data = null) =>
        new(ChatStates.Idle, data ?? new ChatData(), language, [first ? new SayWithButtons("welcome", MenuButtons) : new SayWithButtons("menu", MenuButtons)]);

    public static ChatStep Advance(ChatSnapshot cur, ChatInput input, int resumeAfterHours = 6)
    {
        var first = cur.Language is null;
        var language = cur.Language ?? (input.Kind == InputKind.Text ? DetectLanguage(input.Text) : English);
        var text = Normal(input.Text);
        var data = cur.Data;
        var state = cur.State;

        // Commands that work anywhere.
        if (input.Kind == InputKind.Text)
        {
            if (OptOutWords.Contains(text)) return new ChatStep(ChatStates.Idle, new ChatData(), language, [new OptOut(), new Say("optout")]);
            if (OptInWords.Contains(text)) return Menu(language, false) with { Actions = [new OptIn(), new Say("optin"), new SayWithButtons("menu", MenuButtons)] };
            if (UrduWords.Contains(text)) return Menu(Urdu, false);
            if (EnglishWords.Contains(text)) return Menu(English, false);
        }
        if (input.Kind == InputKind.Button)
        {
            if (input.ButtonId == "lang") return Menu(language == Urdu ? English : Urdu, false, state == ChatStates.Idle ? data : null);
        }

        // Quiet for a long time in the middle of something: ask before carrying on.
        var midway = state is ChatStates.AwaitDescription or ChatStates.Collecting or ChatStates.AwaitStatusReference;
        if (midway && cur.SinceLastMessage > TimeSpan.FromHours(resumeAfterHours))
        {
            data.ResumeState = state;
            return new ChatStep(ChatStates.ConfirmResume, data, language,
                [new SayWithButtons("resume", [("continue", "btn.continue"), ("startover", "btn.startover")])]);
        }

        if (state == ChatStates.ConfirmResume)
        {
            if (input.Kind == InputKind.Button && input.ButtonId == "continue" && data.ResumeState is { } back)
            {
                data.ResumeState = null;
                return back switch
                {
                    ChatStates.AwaitDescription => new ChatStep(back, data, language, [new SayWithButtons("ask_description", [("cancel", "btn.cancel")])]),
                    ChatStates.Collecting => new ChatStep(back, data, language, [new SayWithButtons("already_saved", [("done", "btn.done")], ("reference", data.Reference))]),
                    _ => new ChatStep(ChatStates.AwaitStatusReference, data, language, [new Say("ask_reference")]),
                };
            }
            // "Start again", or anything else: the half-finished report is not lost (a saved complaint stays saved).
            return Menu(language, false);
        }

        // A greeting or "menu" always brings the menu, without losing a complaint already saved.
        if (input.Kind == InputKind.Text && Greetings.Contains(text)) return Menu(language, first);
        if (input.Kind == InputKind.Button && input.ButtonId == "menu") return Menu(language, false);

        return state switch
        {
            ChatStates.AwaitDescription => FromAwaitDescription(data, language, input, text),
            ChatStates.Collecting => FromCollecting(data, language, input, text),
            ChatStates.AwaitStatusReference => FromAwaitStatus(data, language, input, text),
            _ => FromIdle(data, language, input, text, first),
        };
    }

    private static ChatStep FromIdle(ChatData data, string language, ChatInput input, string text, bool first)
    {
        switch (input.Kind)
        {
            case InputKind.Button when input.ButtonId == "report":
                return AskDescription(new ChatData(), language);
            case InputKind.Button when input.ButtonId == "status":
                return new ChatStep(ChatStates.AwaitStatusReference, new ChatData(), language, [new Say("ask_reference")]);
            case InputKind.Text:
                if (ReportWords.Contains(text)) return AskDescription(new ChatData(), language);
                if (FindReference(input.Text) is { } reference) return new ChatStep(ChatStates.Idle, new ChatData(), language, [new LookupStatus(reference)]);
                // People often just type the problem: that is the whole report.
                if (text.Length >= MinFreeTextLength)
                    return new ChatStep(ChatStates.Collecting, new ChatData(), language, [new OpenComplaint(input.Text!.Trim(), false, [], null, null, null)]);
                return Menu(language, first);
            case InputKind.Image or InputKind.Audio when input.MediaId is not null:
            {
                var pending = new ChatData { Pending = [new PendingMedia(input.MediaId, input.Kind == InputKind.Image ? "image" : "audio")] };
                return new ChatStep(ChatStates.AwaitDescription, pending, language,
                    [new SayWithButtons("got_media", [("sendnow", "btn.sendnow"), ("cancel", "btn.cancel")])]);
            }
            case InputKind.Unsupported or InputKind.Location or InputKind.Image or InputKind.Audio:
                return new ChatStep(ChatStates.Idle, data, language, [new Say("unsupported"), new SayWithButtons("menu", MenuButtons)]);
            default:
                return Menu(language, first);
        }
    }

    private static ChatStep AskDescription(ChatData data, string language) =>
        new(ChatStates.AwaitDescription, data, language, [new SayWithButtons("ask_description", [("cancel", "btn.cancel")])]);

    private static ChatStep FromAwaitDescription(ChatData data, string language, ChatInput input, string text)
    {
        if (input.Kind == InputKind.Button && input.ButtonId == "cancel" || input.Kind == InputKind.Text && CancelWords.Contains(text))
            return new ChatStep(ChatStates.Idle, new ChatData(), language, [new Say("cancelled")]);

        switch (input.Kind)
        {
            case InputKind.Text when text.Length > 0:
                return new ChatStep(ChatStates.Collecting, data, language,
                    [new OpenComplaint(input.Text!.Trim(), false, data.Pending, data.PinLatitude, data.PinLongitude, data.PinName)]);
            case InputKind.Image or InputKind.Audio when input.MediaId is not null:
                data.Pending.Add(new PendingMedia(input.MediaId, input.Kind == InputKind.Image ? "image" : "audio"));
                return new ChatStep(ChatStates.AwaitDescription, data, language, [new SayWithButtons("got_media", [("sendnow", "btn.sendnow"), ("cancel", "btn.cancel")])]);
            case InputKind.Location when input.Latitude is { } lat && input.Longitude is { } lon:
                data.PinLatitude = lat; data.PinLongitude = lon; data.PinName = input.PlaceName;
                return new ChatStep(ChatStates.AwaitDescription, data, language, [new Say("location_pin"), new Say("ask_description")]);
            case InputKind.Button when input.ButtonId == "sendnow" && (data.Pending.Count > 0 || data.PinLatitude is not null):
                return new ChatStep(ChatStates.Collecting, data, language,
                    [new OpenComplaint("", true, data.Pending, data.PinLatitude, data.PinLongitude, data.PinName)]);
            case InputKind.Unsupported:
                return new ChatStep(ChatStates.AwaitDescription, data, language, [new Say("unsupported")]);
            default:
                return new ChatStep(ChatStates.AwaitDescription, data, language, [new SayWithButtons("ask_description", [("cancel", "btn.cancel")])]);
        }
    }

    private static ChatStep FromCollecting(ChatData data, string language, ChatInput input, string text)
    {
        if (input.Kind == InputKind.Button && input.ButtonId == "done" || input.Kind == InputKind.Text && DoneWords.Contains(text))
            return new ChatStep(ChatStates.Idle, new ChatData(), language, [new FinishComplaint()]);

        switch (input.Kind)
        {
            case InputKind.Text when CancelWords.Contains(text):
                return new ChatStep(ChatStates.Collecting, data, language, [new SayWithButtons("already_saved", [("done", "btn.done")], ("reference", data.Reference))]);
            // Someone who types a complaint number in the middle of a report is asking about it, not adding to this one.
            case InputKind.Text when ConversationMachine.FindReference(input.Text) is { } reference:
                return new ChatStep(ChatStates.Collecting, data, language, [new LookupStatus(reference)]);
            case InputKind.Text when text.Length > 0 && data.AwaitingAddress:
                data.AwaitingAddress = false;
                return new ChatStep(ChatStates.Collecting, data, language, [new SetAddressText(input.Text!.Trim())]);
            case InputKind.Text when text.Length > 0:
                return new ChatStep(ChatStates.Collecting, data, language, [new AppendDescription(input.Text!.Trim())]);
            case InputKind.Image or InputKind.Audio when input.MediaId is not null:
                return new ChatStep(ChatStates.Collecting, data, language, [new AttachMedia(new PendingMedia(input.MediaId, input.Kind == InputKind.Image ? "image" : "audio"))]);
            case InputKind.Location when input.Latitude is { } lat && input.Longitude is { } lon:
                data.AwaitingAddress = false;
                return new ChatStep(ChatStates.Collecting, data, language, [new SetPin(lat, lon, input.PlaceName)]);
            case InputKind.Unsupported:
                return new ChatStep(ChatStates.Collecting, data, language, [new Say("unsupported")]);
            default:
                return new ChatStep(ChatStates.Collecting, data, language, [new SayWithButtons("already_saved", [("done", "btn.done")], ("reference", data.Reference))]);
        }
    }

    private static ChatStep FromAwaitStatus(ChatData data, string language, ChatInput input, string text)
    {
        if (input.Kind == InputKind.Button && input.ButtonId == "cancel" || input.Kind == InputKind.Text && CancelWords.Contains(text))
            return new ChatStep(ChatStates.Idle, new ChatData(), language, [new Say("cancelled")]);
        if (input.Kind == InputKind.Text && FindReference(input.Text) is { } reference)
            return new ChatStep(ChatStates.Idle, new ChatData(), language, [new LookupStatus(reference)]);
        return new ChatStep(ChatStates.AwaitStatusReference, data, language, [new Say("ask_reference")]);
    }
}
