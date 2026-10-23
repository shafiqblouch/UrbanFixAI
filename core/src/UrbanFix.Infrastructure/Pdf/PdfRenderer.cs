using System.Reflection;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace UrbanFix.Infrastructure.Pdf;

/// <summary>One block of a report: a heading, some paragraphs, facts as label/value pairs, and/or a table.</summary>
public sealed record PdfSection(
    string? Heading = null, IReadOnlyList<string>? Paragraphs = null, IReadOnlyList<(string Label, string Value)>? Facts = null, PdfTable? Table = null);

/// <summary>Columns marked numeric are aligned to the end of the line, so they stay lined up in both directions.</summary>
public sealed record PdfTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<bool>? Numeric = null);

public sealed record PdfReport(string Title, string? Subtitle, string Locale, string OrganizationName, DateTimeOffset GeneratedAt, IReadOnlyList<PdfSection> Sections);

public interface IPdfRenderer
{
    byte[] Render(PdfReport report);
    /// <summary>Each page as a PNG, for checking the layout (tests, previews).</summary>
    IReadOnlyList<byte[]> RenderImages(PdfReport report);
}

/// <summary>
/// Builds report PDFs in English or Urdu (FD-15). Urdu is laid out right to left with properly joined Arabic-script letters, which
/// needs a font that has them: Noto Naskh Arabic is embedded, so nothing depends on fonts installed on the server.
/// The same renderer will serve bills and receipts later; they only differ in the <see cref="PdfReport"/> they hand it.
/// </summary>
public sealed class PdfRenderer : IPdfRenderer
{
    public const string Latin = "Noto Sans";
    public const string Arabic = "Noto Naskh Arabic";

    private static readonly object Gate = new();
    private static bool _ready;

    private static void EnsureReady()
    {
        lock (Gate)
        {
            if (_ready) return;
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.EnableDebugging = false;
            var assembly = typeof(PdfRenderer).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                QuestPDF.Drawing.FontManager.RegisterFont(stream);
            }
            _ready = true;
        }
    }

    public static IReadOnlyDictionary<string, string> Labels(string locale) => locale == "ur" ? Ur : En;

    private static readonly Dictionary<string, string> En = new()
    {
        ["page"] = "Page", ["of"] = "of", ["generated"] = "Prepared on",
    };

    private static readonly Dictionary<string, string> Ur = new()
    {
        ["page"] = "صفحہ", ["of"] = "از", ["generated"] = "تیار کردہ بتاریخ",
    };

    public byte[] Render(PdfReport report)
    {
        EnsureReady();
        return Build(report).GeneratePdf();
    }

    public IReadOnlyList<byte[]> RenderImages(PdfReport report)
    {
        EnsureReady();
        return Build(report).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 110 }).ToList();
    }

    private static Document Build(PdfReport report)
    {
        var urdu = report.Locale == "ur";
        var labels = Labels(report.Locale);
        // Latin first, Arabic as the fallback for the letters Noto Sans does not have (and the reverse for Urdu text).
        var families = urdu ? new[] { Arabic, Latin } : new[] { Latin, Arabic };
        var ink = Colors.Grey.Darken4;
        var muted = Colors.Grey.Darken1;
        var line = Colors.Grey.Lighten2;

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(x => x.FontFamily(families).FontSize(urdu ? 11 : 10).FontColor(ink));
            if (urdu) page.ContentFromRightToLeft();

            page.Header().PaddingBottom(10).BorderBottom(1).BorderColor(line).Column(col =>
            {
                col.Item().Text(report.OrganizationName).FontSize(9).FontColor(muted);
                col.Item().Text(report.Title).FontSize(urdu ? 20 : 18).Bold();
                if (report.Subtitle is { } sub) col.Item().Text(sub).FontColor(muted);
            });

            page.Content().PaddingVertical(12).Column(col =>
            {
                col.Spacing(12);
                foreach (var section in report.Sections) col.Item().Element(c => Section(c, section, urdu));
            });

            page.Footer().BorderTop(1).BorderColor(line).PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text($"{labels["generated"]} {report.GeneratedAt:yyyy-MM-dd HH:mm}").FontSize(8).FontColor(muted);
                row.AutoItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(muted));
                    text.Span($"{labels["page"]} ");
                    text.CurrentPageNumber();
                    text.Span($" {labels["of"]} ");
                    text.TotalPages();
                });
            });
        }));
    }

    private static void Section(IContainer container, PdfSection section, bool urdu)
    {
        container.Column(col =>
        {
            col.Spacing(5);
            if (section.Heading is { } heading) col.Item().Text(heading).FontSize(urdu ? 14 : 12).Bold();
            foreach (var p in section.Paragraphs ?? []) col.Item().Text(p);
            if (section.Facts is { Count: > 0 } facts)
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(3); });
                    foreach (var (label, value) in facts)
                    {
                        t.Cell().PaddingVertical(2).Text(label).FontColor(Colors.Grey.Darken1);
                        t.Cell().PaddingVertical(2).Text(value);
                    }
                });
            if (section.Table is { } table) col.Item().Element(c => Grid(c, table));
        });
    }

    private static void Grid(IContainer container, PdfTable table)
    {
        var n = table.Headers.Count;
        bool Numeric(int i) => table.Numeric is { } flags && i < flags.Count && flags[i];
        container.Table(t =>
        {
            t.ColumnsDefinition(c => { for (var i = 0; i < n; i++) c.RelativeColumn(Numeric(i) ? 1 : 2); });
            t.Header(h =>
            {
                for (var i = 0; i < n; i++)
                {
                    var cell = h.Cell().Background(Colors.Grey.Lighten3).BorderBottom(1).BorderColor(Colors.Grey.Medium).Padding(4);
                    (Numeric(i) ? cell.AlignRight() : cell).Text(table.Headers[i]).Bold();
                }
            });
            foreach (var row in table.Rows)
                for (var i = 0; i < n; i++)
                {
                    var cell = t.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(4);
                    (Numeric(i) ? cell.AlignRight() : cell).Text(i < row.Count ? row[i] : "");
                }
        });
    }
}
