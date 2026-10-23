using System.Text;
using UrbanFix.Identity;

namespace UrbanFix.OrgSetup;

public class Vehicle : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    /// <summary>As entered, e.g. "ABC-123".</summary>
    public string Plate { get; set; } = "";
    /// <summary>Letters and digits only, upper case: "ABC123". Unique per organization, so "abc 123" and "ABC-123" are the same car (OR-08).</summary>
    public string PlateKey { get; set; } = "";
    public string Type { get; set; } = "car";
    public Guid? HouseId { get; set; }
    public string? StickerCode { get; set; }
    public string Status { get; set; } = "active";
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A maid, driver, cook or other person who works in houses (OR-09). The full ID number is never kept, only a masked form.</summary>
public class DailyHelp : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? IdNumberMasked { get; set; }
    public string? PhotoKey { get; set; }
    public string Role { get; set; } = "other";
    public string Status { get; set; } = "active";
    /// <summary>Days of the week they may come, lower-case three letters; empty means every day.</summary>
    public string[] AllowedDays { get; set; } = [];
    public TimeOnly? AllowedFrom { get; set; }
    public TimeOnly? AllowedTo { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One person can work in several houses.</summary>
public class DailyHelpHouse : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid HelpId { get; set; }
    public Guid HouseId { get; set; }
}

/// <summary>Staff looked at a possible duplicate and decided it is fine, so it is not shown again (OR-11).</summary>
public class DirectoryDismissal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public Guid? DismissedBy { get; set; }
    public DateTimeOffset DismissedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Small rules for the directory, as pure functions.</summary>
public static class RegistryRules
{
    public static readonly string[] VehicleTypes = ["car", "motorbike", "rickshaw", "van", "other"];
    public static readonly string[] HelpRoles = ["maid", "driver", "cook", "gardener", "guard", "nanny", "other"];
    public static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    public static readonly string[] Statuses = ["active", "inactive"];

    /// <summary>"abc 123" and "ABC-123" give the same key.</summary>
    public static string PlateKey(string? plate) => new((plate ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Keeps the last four letters or digits of an ID number and hides the rest: "35202-1234567-1" becomes "*****-*******-1" shortened to its last four.</summary>
    public static string? MaskId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var chars = id.Where(char.IsLetterOrDigit).ToArray();
        if (chars.Length == 0) return null;
        var keep = Math.Min(4, chars.Length);
        return new string('•', chars.Length - keep) + new string(chars[^keep..]);
    }

    /// <summary>Reads "mon,tue" or "Mon; Tue" or "weekdays" into day codes; null when something is not a day.</summary>
    public static string[]? ParseDays(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var lower = text.Trim().ToLowerInvariant();
        if (lower is "all" or "daily" or "every day" or "everyday") return [];
        if (lower is "weekdays") return Days[..5];
        var parts = lower.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Length >= 3 ? p[..3] : p).ToArray();
        return parts.All(Days.Contains) ? parts.Distinct().OrderBy(p => Array.IndexOf(Days, p)).ToArray() : null;
    }

    public static bool TryParseTime(string? text, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (TimeOnly.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var t)) { time = t; return true; }
        return false;
    }

    /// <summary>Is a visit on this day and time within what was allowed? Used by the gate later; here so the rule lives with the record (OR-09).</summary>
    public static bool IsAllowed(DailyHelp help, DayOfWeek day, TimeOnly time)
    {
        if (help.Status != "active") return false;
        if (help.AllowedDays.Length > 0 && !help.AllowedDays.Contains(Days[((int)day + 6) % 7])) return false;
        if (help.AllowedFrom is { } from && time < from) return false;
        if (help.AllowedTo is { } to && time > to) return false;
        return true;
    }
}

/// <summary>A small, strict CSV reader (RFC 4180): quotes, doubled quotes, commas and line breaks inside quotes, a leading byte-order mark.</summary>
public static class Csv
{
    public sealed record Row(int Line, IReadOnlyDictionary<string, string> Values)
    {
        public string Get(string name) => Values.TryGetValue(name, out var v) ? v.Trim() : "";
    }

    public sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<Row> Rows);

    /// <exception cref="FormatException">An unclosed quote, or an empty file.</exception>
    public static Table Parse(string text)
    {
        var records = new List<(int Line, List<string> Fields)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        void EndField() { fields.Add(field.ToString()); field.Clear(); }
        void EndRecord()
        {
            EndField();
            if (!fields.All(string.IsNullOrWhiteSpace)) records.Add((recordLine, fields)); // skip blank lines
            fields = [];
        }

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else { field.Append(c); if (c == '\n') line++; }
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0: inQuotes = true; break;
                case ',': EndField(); break;
                case '\r': break;
                case '\n': EndRecord(); line++; recordLine = line; break;
                default: field.Append(c); break;
            }
        }
        if (inQuotes) throw new FormatException("A quoted value is never closed");
        if (field.Length > 0 || fields.Count > 0) EndRecord();
        if (records.Count == 0) throw new FormatException("The file is empty");

        var header = records[0].Fields.Select(h => h.Trim().ToLowerInvariant()).ToList();
        var rows = records.Skip(1).Select(r =>
            new Row(r.Line, header.Select((h, idx) => (h, v: idx < r.Fields.Count ? r.Fields[idx] : "")).Where(p => p.h.Length > 0).GroupBy(p => p.h).ToDictionary(g => g.Key, g => g.First().v))).ToList();
        return new Table(header, rows);
    }
}
