using UrbanFix.OrgSetup;

namespace UrbanFix.Infrastructure.WhatsApp;

public enum GateDecisionKind { None, Allow, Deny }

/// <summary>What a resident's WhatsApp reply means for a gate request: allow or deny, and which one (by its code) if they said so.</summary>
public sealed record GateReplyIntent(GateDecisionKind Kind, string? Code);

/// <summary>
/// Reads a resident's free-text WhatsApp reply to a "someone is at the gate for you" message as allow/deny (GT-04),
/// optionally naming the visitor pass by its code. Pure, so every wording is tested without a database. Deliberately
/// strict: only clear words count, so a reply is never mistaken for a gate decision when it is part of a complaint.
/// </summary>
public static class GateReply
{
    private static readonly string[] AllowWords = ["allow", "approve", "accept", "اجازت"];
    private static readonly string[] DenyWords = ["deny", "reject", "refuse", "منع", "روکیں", "روکو"];

    public static GateReplyIntent Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(GateDecisionKind.None, null);
        var lower = text.Trim().ToLowerInvariant();
        foreach (var w in AllowWords) if (Matches(lower, w, out var rest)) return new(GateDecisionKind.Allow, Code(rest));
        foreach (var w in DenyWords) if (Matches(lower, w, out var rest)) return new(GateDecisionKind.Deny, Code(rest));
        return new(GateDecisionKind.None, null);
    }

    /// <summary>The whole message is the word, or the word then the rest (a pass code), e.g. "allow PX7QK2".</summary>
    private static bool Matches(string lower, string word, out string rest)
    {
        rest = "";
        if (lower == word) return true;
        if (lower.StartsWith(word + " ", StringComparison.Ordinal)) { rest = lower[(word.Length + 1)..]; return true; }
        return false;
    }

    /// <summary>A pass code from the rest of the message, or null when there is nothing code-like there.</summary>
    private static string? Code(string rest)
    {
        var code = GateRules.NormalizeCode(rest);
        return code.Length >= 4 ? code : null;
    }
}
