using System.Text.RegularExpressions;

namespace UrbanFix.Identity;

/// <summary>Phone numbers are stored in international form (+923001234567) so the same person matches however they typed it.</summary>
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")]
    private static partial Regex E164();

    /// <summary>Null when the text is not a usable number. A leading 0 is read as Pakistan (+92), the pilot market.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        var plus = input.Trim().StartsWith('+');
        string candidate;
        if (plus) candidate = "+" + digits;
        else if (digits.StartsWith("00")) candidate = "+" + digits[2..];
        else if (digits.StartsWith('0')) candidate = "+92" + digits[1..];
        else if (digits.StartsWith("92") && digits.Length >= 12) candidate = "+" + digits;
        else return null;
        return E164().IsMatch(candidate) ? candidate : null;
    }

    public static string Mask(string phone) => phone.Length <= 6 ? "***" : phone[..4] + new string('*', phone.Length - 7) + phone[^3..];
}
