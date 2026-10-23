namespace UrbanFix.Complaints;

/// <summary>
/// The rules-only mode (AI-14): when the AI service is down, switched off, or the organization has used up its monthly
/// allowance, complaints still reach the supervisor queue with a plain keyword guess at the category. It is a hint, never
/// an instruction: confidence is low, so nothing is ever routed automatically from it.
/// </summary>
public static class RulesOnlyClassifier
{
    public const string Model = "rules-only";
    public const string Version = "v1";
    public const float Confidence = 0.3f;

    public sealed record Candidate(string Key, string NameEn);

    public sealed record Guess(string Key, int Hits);

    // Words people use in English, Urdu and Roman Urdu, by the key of the seeded category templates. An organization's own
    // categories are matched on their key and English name as well.
    private static readonly Dictionary<string, string[]> Words = new()
    {
        ["water"] = ["water", "tap", "pipe", "leak", "pressure", "tanker", "pani", "paani", "پانی", "نل", "پائپ", "لیک"],
        ["roads"] = ["road", "pothole", "street", "asphalt", "speed breaker", "sarak", "gaddha", "سڑک", "گڑھا", "گڑھے"],
        ["streetlights"] = ["street light", "streetlight", "lamp", "bulb", "pole light", "light is out", "batti", "بتی", "لائٹ", "بلب"],
        ["sewerage"] = ["sewer", "sewage", "drain", "manhole", "gutter", "overflow", "nali", "naali", "سیوریج", "نالی", "گٹر", "مین ہول"],
        ["sanitation"] = ["garbage", "trash", "waste", "rubbish", "dump", "sweeper", "kachra", "کچرا", "گندگی", "صفائی"],
        ["parks"] = ["park", "playground", "swing", "bench", "grass", "garden", "tree", "پارک", "جھولا", "درخت"],
        ["building-maintenance"] = ["lift", "elevator", "stairs", "roof", "paint", "seepage", "corridor", "لفٹ", "سیڑھی", "چھت"],
        ["parking"] = ["parking", "parked", "blocking", "car", "vehicle", "گاڑی", "پارکنگ"],
    };

    public static Guess? Classify(string text, IEnumerable<Candidate> categories)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();
        Guess? best = null;
        foreach (var c in categories)
        {
            var words = new List<string>();
            if (Words.TryGetValue(c.Key, out var known)) words.AddRange(known);
            words.AddRange(c.Key.Split('-', '_').Where(w => w.Length >= 4));
            words.AddRange(c.NameEn.ToLowerInvariant().Split(' ', ',', '/').Where(w => w.Length >= 4 && w is not ("supply" or "and")));
            var hits = words.Distinct().Count(w => t.Contains(w, StringComparison.Ordinal));
            if (hits > 0 && (best is null || hits > best.Hits)) best = new Guess(c.Key, hits);
        }
        return best;
    }
}
