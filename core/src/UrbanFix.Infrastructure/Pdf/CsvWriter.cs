using System.Globalization;
using System.Text;

namespace UrbanFix.Infrastructure.Pdf;

/// <summary>
/// CSV for spreadsheets (DB-04). UTF-8 with a byte-order mark, so Excel reads Urdu correctly. Cells that a spreadsheet would run as a
/// formula (starting with = + - @) are neutralised, so a complaint typed as "=HYPERLINK(...)" cannot do anything when exported.
/// </summary>
public static class CsvWriter
{
    public static byte[] Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', headers.Select(Cell))).Append("\r\n");
        foreach (var row in rows) sb.Append(string.Join(',', row.Select(Cell))).Append("\r\n");
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && IsFormula(text)) text = "'" + text;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static bool IsFormula(string text) => text[0] switch
    {
        '=' or '@' or '\t' or '\r' => true,
        // A plain signed number such as -5 or +3.5 is a number, not a formula.
        '+' or '-' => !decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
        _ => false,
    };
}
