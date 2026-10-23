using System.Text.RegularExpressions;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.WhatsApp;

namespace UrbanFix.Tests;

/// <summary>Plays a chat through the conversation rules, one message at a time.</summary>
internal sealed class Chat(string? language = null)
{
    public string State = ChatStates.Idle;
    public string? Language = language;
    public ChatData Data = new();
    public TimeSpan Gap = TimeSpan.FromMinutes(1);
    public IReadOnlyList<ChatAction> Last = [];

    public IReadOnlyList<ChatAction> Send(ChatInput input)
    {
        var step = ConversationMachine.Advance(new ChatSnapshot(State, Language, Data, Gap), input);
        State = step.State; Data = step.Data; Language = step.Language;
        return Last = step.Actions;
    }

    public IReadOnlyList<ChatAction> Text(string text) => Send(new ChatInput(InputKind.Text, text));
    public IReadOnlyList<ChatAction> Button(string id) => Send(new ChatInput(InputKind.Button, ButtonId: id));
    public IReadOnlyList<ChatAction> Image(string id) => Send(new ChatInput(InputKind.Image, MediaId: id));
    public IReadOnlyList<ChatAction> Audio(string id) => Send(new ChatInput(InputKind.Audio, MediaId: id));
    public IReadOnlyList<ChatAction> Pin(double lat, double lon) => Send(new ChatInput(InputKind.Location, Latitude: lat, Longitude: lon));
}

public class WhatsAppConversationTests
{
    private static T Only<T>(IReadOnlyList<ChatAction> actions) where T : ChatAction => Assert.IsType<T>(Assert.Single(actions));

    [Fact]
    public void A_first_hello_gets_the_welcome_and_the_menu_in_the_language_written()
    {
        var english = new Chat();
        var welcome = Only<SayWithButtons>(english.Text("Hello"));
        Assert.Equal(("welcome", "en"), (welcome.Key, english.Language));
        Assert.Equal(["report", "status", "lang"], welcome.Buttons.Select(b => b.Id));

        var urdu = new Chat();
        Only<SayWithButtons>(urdu.Text("السلام علیکم"));
        Assert.Equal("ur", urdu.Language);
    }

    [Fact]
    public void Typing_the_problem_straight_away_is_a_complete_report()
    {
        var chat = new Chat();
        var create = Only<OpenComplaint>(chat.Text("There is no water in block A since this morning"));
        Assert.Equal(("There is no water in block A since this morning", false, ChatStates.Collecting), (create.Description, create.DescriptionIsPlaceholder, chat.State));
    }

    [Fact]
    public void Short_unknown_words_get_the_menu_not_a_complaint()
    {
        var chat = new Chat("en");
        Assert.Equal("menu", Only<SayWithButtons>(chat.Text("ok")).Key);
        Assert.Equal(ChatStates.Idle, chat.State);
    }

    [Fact]
    public void The_report_button_asks_for_the_problem_and_the_next_message_becomes_the_complaint()
    {
        var chat = new Chat("en");
        Assert.Equal("ask_description", Only<SayWithButtons>(chat.Button("report")).Key);
        Assert.Equal(ChatStates.AwaitDescription, chat.State);
        Only<OpenComplaint>(chat.Text("Street light broken"));
        Assert.Equal(ChatStates.Collecting, chat.State);
    }

    [Fact]
    public void Photos_and_voice_notes_sent_first_wait_for_a_description_and_go_with_the_complaint()
    {
        var chat = new Chat("en");
        Assert.Equal("got_media", Only<SayWithButtons>(chat.Image("img-1")).Key);
        chat.Audio("aud-1");
        chat.Pin(24.86, 67.01);
        var create = Only<OpenComplaint>(chat.Text("Pothole outside the gate"));
        Assert.Equal(["img-1", "aud-1"], create.Media.Select(m => m.MediaId));
        Assert.Equal((24.86, 67.01), (create.PinLatitude, create.PinLongitude));
    }

    [Fact]
    public void A_photo_can_be_sent_as_it_is_with_no_words()
    {
        var chat = new Chat("en");
        chat.Image("img-1");
        var create = Only<OpenComplaint>(chat.Button("sendnow"));
        Assert.True(create.DescriptionIsPlaceholder);
        Assert.Single(create.Media);
        Assert.Equal(ChatStates.Collecting, chat.State);
    }

    [Fact]
    public void Sending_as_it_is_with_nothing_attached_just_asks_again()
    {
        var chat = new Chat("en");
        chat.Button("report");
        Assert.Equal("ask_description", Only<SayWithButtons>(chat.Button("sendnow")).Key);
        Assert.Equal(ChatStates.AwaitDescription, chat.State);
    }

    [Fact]
    public void After_the_complaint_exists_more_text_photos_voice_and_a_pin_are_added_to_it()
    {
        var chat = new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-2026-00007", ComplaintId = Guid.NewGuid() } };
        Assert.Equal("more water now", Only<AppendDescription>(chat.Text("more water now")).Text);
        Assert.Equal("i-9", Only<AttachMedia>(chat.Image("i-9")).Media.MediaId);
        Assert.Equal("audio", Only<AttachMedia>(chat.Audio("a-9")).Media.Kind);
        var pin = Only<SetPin>(chat.Pin(1.5, 2.5));
        Assert.Equal((1.5, 2.5), (pin.Latitude, pin.Longitude));
        Assert.Equal(ChatStates.Collecting, chat.State);
    }

    [Fact]
    public void When_the_place_is_missing_the_next_typed_line_is_the_address()
    {
        var chat = new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-1", AwaitingAddress = true } };
        Assert.Equal("Street 4, house 12", Only<SetAddressText>(chat.Text("Street 4, house 12")).Text);
        Assert.False(chat.Data.AwaitingAddress);
        Only<AppendDescription>(chat.Text("it is getting worse"));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("Done.")]
    [InlineData("مکمل")]
    public void Done_finishes_the_report_by_word_or_button(string word)
    {
        var chat = new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-1" } };
        Only<FinishComplaint>(chat.Text(word));
        Assert.Equal(ChatStates.Idle, chat.State);

        var again = new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-1" } };
        Only<FinishComplaint>(again.Button("done"));
    }

    [Fact]
    public void A_complaint_number_typed_mid_report_is_a_status_question_and_the_report_carries_on()
    {
        var chat = new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-2026-00007" } };
        Assert.Equal("UF-2026-00042", Only<LookupStatus>(chat.Text("what about uf-2026-00042")).Reference);
        Assert.Equal(ChatStates.Collecting, chat.State);
    }

    [Fact]
    public void Cancel_after_the_complaint_is_saved_does_not_pretend_to_unsend_it()
    {
        var chat = new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-2026-00003" } };
        var reply = Only<SayWithButtons>(chat.Text("cancel"));
        Assert.Equal("already_saved", reply.Key);
        Assert.Equal(ChatStates.Collecting, chat.State);
    }

    [Fact]
    public void Cancel_before_sending_goes_back_to_the_start()
    {
        var chat = new Chat("en");
        chat.Button("report");
        Assert.Equal("cancelled", Only<Say>(chat.Button("cancel")).Key);
        Assert.Equal(ChatStates.Idle, chat.State);
    }

    // WA-05 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Checking_a_status_by_button_then_number_or_by_just_sending_the_number()
    {
        var chat = new Chat("en");
        Assert.Equal("ask_reference", Only<Say>(chat.Button("status")).Key);
        Assert.Equal("ask_reference", Only<Say>(chat.Text("my complaint")).Key); // not a number: ask again
        Assert.Equal("UF-2026-00042", Only<LookupStatus>(chat.Text("uf-2026-00042")).Reference);
        Assert.Equal(ChatStates.Idle, chat.State);

        var direct = new Chat("ur");
        Assert.Equal("UF-2026-00009", Only<LookupStatus>(direct.Text("میری شکایت UF-2026-00009 کا کیا بنا؟")).Reference);
    }

    // WA-04: a long pause --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ChatStates.AwaitDescription)]
    [InlineData(ChatStates.Collecting)]
    [InlineData(ChatStates.AwaitStatusReference)]
    public void After_a_long_pause_a_half_finished_chat_asks_whether_to_carry_on(string state)
    {
        var chat = new Chat("en") { State = state, Data = new ChatData { Reference = "UF-2026-00005" }, Gap = TimeSpan.FromHours(7) };
        var ask = Only<SayWithButtons>(chat.Text("hello again, so the water is still off"));
        Assert.Equal(("resume", ChatStates.ConfirmResume), (ask.Key, chat.State));
        Assert.Equal(["continue", "startover"], ask.Buttons.Select(b => b.Id));

        chat.Gap = TimeSpan.FromMinutes(1);
        chat.Button("continue");
        Assert.Equal(state, chat.State);
    }

    [Fact]
    public void Starting_again_after_a_pause_returns_to_the_menu_and_a_short_pause_does_not_interrupt()
    {
        var chat = new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-1" }, Gap = TimeSpan.FromHours(7) };
        chat.Text("anything");
        Assert.Equal("menu", Only<SayWithButtons>(chat.Button("startover")).Key);
        Assert.Equal(ChatStates.Idle, chat.State);

        var quick = new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-1" }, Gap = TimeSpan.FromHours(5) };
        Only<AppendDescription>(quick.Text("one more thing"));
    }

    [Fact]
    public void An_idle_chat_never_asks_about_resuming()
    {
        var chat = new Chat("en") { Gap = TimeSpan.FromDays(30) };
        Assert.Equal("menu", Only<SayWithButtons>(chat.Text("hello")).Key);
    }

    // Consent and language (WA-06) -----------------------------------------------------------------------------------

    [Fact]
    public void Stop_and_start_turn_messages_off_and_on_from_anywhere()
    {
        var chat = new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-1" } };
        var off = chat.Text("STOP");
        Assert.IsType<OptOut>(off[0]);
        Assert.Equal(("optout", ChatStates.Idle), (((Say)off[1]).Key, chat.State));

        var on = chat.Text("start");
        Assert.IsType<OptIn>(on[0]);
    }

    [Fact]
    public void Language_can_be_switched_by_word_or_by_the_menu_button()
    {
        var chat = new Chat("en");
        chat.Text("اردو");
        Assert.Equal("ur", chat.Language);
        chat.Button("lang");
        Assert.Equal("en", chat.Language);
        chat.Text("urdu");
        Assert.Equal("ur", chat.Language);
        chat.Text("english");
        Assert.Equal("en", chat.Language);
    }

    [Fact]
    public void Things_that_cannot_be_read_get_a_polite_answer_in_every_state()
    {
        foreach (var state in new[] { ChatStates.Idle, ChatStates.AwaitDescription, ChatStates.Collecting })
        {
            var chat = new Chat("en") { State = state, Data = new ChatData { Reference = "UF-1" } };
            Assert.Contains(chat.Send(new ChatInput(InputKind.Unsupported)), a => a is Say { Key: "unsupported" });
        }
    }

    [Theory]
    [InlineData("There is no water in block A", "en")]
    [InlineData("پانی نہیں آ رہا", "ur")]
    [InlineData("pani nahi aa raha", "en")]   // Roman Urdu cannot be told from English by script, so it is answered in English
    [InlineData("", "en")]
    [InlineData("123 456", "en")]
    public void The_language_of_a_message_is_found_from_its_script(string text, string expected) => Assert.Equal(expected, ConversationMachine.DetectLanguage(text));

    [Fact]
    public void Complaint_numbers_are_found_in_a_sentence_in_any_case()
    {
        Assert.Equal("UF-2026-00042", ConversationMachine.FindReference("status of uf-2026-00042 please"));
        Assert.Null(ConversationMachine.FindReference("UF-26-1"));
        Assert.Null(ConversationMachine.FindReference(null));
    }

    // DS-06 / WA-07: the scripts themselves --------------------------------------------------------------------------

    [Fact]
    public void Every_script_has_both_languages_with_the_same_placeholders_and_fits_WhatsApps_limits()
    {
        var holes = new Regex(@"\{(\w+)\}");
        foreach (var key in WhatsAppScripts.Keys)
        {
            var en = WhatsAppScripts.Get(key, "en");
            var ur = WhatsAppScripts.Get(key, "ur");
            Assert.NotEmpty(en);
            if (key != "btn.language") Assert.Matches("[؀-ۿ]", ur); // the language switch is labelled "English" on purpose
            Assert.Equal(holes.Matches(en).Select(m => m.Value).Distinct().Order(), holes.Matches(ur).Select(m => m.Value).Distinct().Order());
            Assert.True(ur.Length <= WhatsAppScripts.MaxBody && en.Length <= WhatsAppScripts.MaxBody, key);
            if (key.StartsWith("btn.")) Assert.True(en.Length <= WhatsAppScripts.MaxButtonTitle && ur.Length <= WhatsAppScripts.MaxButtonTitle, $"{key} is too long for a button");
        }
    }

    [Fact]
    public void Every_button_the_machine_can_offer_has_a_title_script()
    {
        var chat = new Chat();
        var actions = new List<ChatAction>();
        actions.AddRange(chat.Text("hi"));
        actions.AddRange(chat.Button("report"));
        actions.AddRange(chat.Image("x"));
        actions.AddRange(new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-1" }, Gap = TimeSpan.FromHours(9) }.Text("x"));
        actions.AddRange(new Chat("en") { State = ChatStates.Collecting, Data = new ChatData { Reference = "UF-1" } }.Text("cancel"));
        var keys = WhatsAppScripts.Keys.ToHashSet();
        foreach (var button in actions.OfType<SayWithButtons>().SelectMany(a => a.Buttons)) Assert.Contains(button.TitleKey, keys);
        foreach (var say in actions.OfType<SayWithButtons>()) Assert.Contains(say.Key, keys);
    }

    [Fact]
    public void Every_status_has_words_in_both_languages()
    {
        foreach (var status in Enum.GetValues<ComplaintStatus>())
        {
            Assert.NotEmpty(WhatsAppScripts.StatusText(status, "en"));
            Assert.Matches("[؀-ۿ]", WhatsAppScripts.StatusText(status, "ur"));
        }
    }
}
