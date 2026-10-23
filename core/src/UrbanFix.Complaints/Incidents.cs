using UrbanFix.Identity;

namespace UrbanFix.Complaints;

/// <summary>Several complaints about the same problem, grouped so it is dealt with once and everyone is told (DP-01). The complaints stay separate records.</summary>
public class Incident : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "open";
    /// <summary>The complaint the work is tracked on, normally the oldest.</summary>
    public Guid? PrimaryComplaintId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class SuggestionStatus
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
}

/// <summary>"These two may be the same problem", with how sure the system is and why (DP-03). A person decides (DP-04).</summary>
public class DuplicateSuggestion : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    /// <summary>The pair is stored with the smaller id first, so (A,B) and (B,A) are the same pair and a rejected pair is never suggested again.</summary>
    public Guid ComplaintAId { get; set; }
    public Guid ComplaintBId { get; set; }
    public float Score { get; set; }
    public float? TextSimilarity { get; set; }
    public double? DistanceMetres { get; set; }
    public double HoursApart { get; set; }
    public bool SameCategory { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = SuggestionStatus.Pending;
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class Geo
{
    /// <summary>Metres between two points on the earth.</summary>
    public static double DistanceMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}

/// <summary>Scoring of how likely two complaints are the same problem, as pure functions (DP-02, DP-03, DP-06).</summary>
public static class DuplicateScoring
{
    public const double DefaultThreshold = 0.7;

    public sealed record Settings(double RadiusMetres = 300, double WindowDays = 7, double Threshold = DefaultThreshold);
    public sealed record Result(double Score, string Reason);

    /// <param name="textSimilarity">0 to 1 from the embeddings, or null when there are none.</param>
    /// <param name="distanceMetres">Null when the places are not known well enough to measure.</param>
    public static Result Score(double? textSimilarity, double? distanceMetres, double hoursApart, bool sameCategory, Settings s)
    {
        var geo = distanceMetres is { } d ? 1 - Math.Min(d / s.RadiusMetres, 1) : 0.3;
        var time = 1 - Math.Min(Math.Max(hoursApart, 0) / (s.WindowDays * 24), 1);
        var category = sameCategory ? 1.0 : 0.0;

        double score;
        if (textSimilarity is { } t)
            score = 0.5 * Math.Clamp(t, 0, 1) + 0.25 * geo + 0.15 * time + 0.10 * category;
        else // no wording to compare: only a very close place, time and category counts, and never as sure as with words
            score = Math.Min(0.85, (0.5 * geo + 0.3 * time + 0.2 * category) * 0.95);

        var parts = new List<string>();
        parts.Add(textSimilarity is { } ts ? $"wording {ts:0.00} alike" : "no wording to compare");
        parts.Add(distanceMetres is { } dm ? $"{dm:0} m apart" : "place not measured");
        parts.Add($"{hoursApart:0.#} h apart");
        parts.Add(sameCategory ? "same category" : "different category");
        return new Result(Math.Round(score, 3), string.Join(", ", parts));
    }

    /// <summary>Is a complaint a candidate at all (DP-02): inside the time window and either inside the radius or in the same place.</summary>
    public static bool IsCandidate(double hoursApart, double? distanceMetres, bool samePlace, Settings s) =>
        hoursApart <= s.WindowDays * 24 && (samePlace || (distanceMetres is { } d && d <= s.RadiusMetres));

    public sealed record Labelled(double Score, bool IsDuplicate);

    public sealed record Quality(double Threshold, double Precision, double Recall, int TruePositives, int FalsePositives, int FalseNegatives);

    public static Quality Evaluate(IReadOnlyList<Labelled> pairs, double threshold)
    {
        var tp = pairs.Count(p => p.Score >= threshold && p.IsDuplicate);
        var fp = pairs.Count(p => p.Score >= threshold && !p.IsDuplicate);
        var fn = pairs.Count(p => p.Score < threshold && p.IsDuplicate);
        return new Quality(threshold, tp + fp == 0 ? 1 : (double)tp / (tp + fp), tp + fn == 0 ? 1 : (double)tp / (tp + fn), tp, fp, fn);
    }

    /// <summary>The lowest threshold that keeps precision at or above the target, so as many real duplicates as possible are suggested (DP-06). Null when no threshold reaches it.</summary>
    public static Quality? Tune(IReadOnlyList<Labelled> pairs, double targetPrecision = 0.9)
    {
        var candidates = pairs.Select(p => Math.Round(p.Score, 3)).Distinct().OrderBy(x => x);
        return candidates.Select(t => Evaluate(pairs, t)).Where(q => q.Precision >= targetPrecision && q.TruePositives > 0).OrderBy(q => q.Threshold).FirstOrDefault();
    }

    /// <summary>The normal order of a pair, smaller id first.</summary>
    public static (Guid A, Guid B) Pair(Guid x, Guid y) =>
        x.ToByteArray(bigEndian: true).AsSpan().SequenceCompareTo(y.ToByteArray(bigEndian: true)) <= 0 ? (x, y) : (y, x); // the same order PostgreSQL uses for uuid
}
