using System.Text;
using UrbanFix.Infrastructure.Pdf;

namespace UrbanFix.Tests;

public class PdfTests
{
    private static PdfReport Sample(string locale) => locale == "ur"
        ? new PdfReport("ماہانہ خلاصہ", "اکتوبر 2026", "ur", "گلشن سوسائٹی", new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero),
        [
            new PdfSection("شکایات", ["اس ماہ 42 شکایات درج ہوئیں اور 37 حل ہوئیں۔"], [("کل شکایات", "42"), ("اوسط وقت", "18.5 گھنٹے")]),
            new PdfSection("زمرہ وار", null, null, new PdfTable(["زمرہ", "تعداد", "لاگت"], [["پانی کی فراہمی", "20", "Rs 15,000.00"], ["سڑکیں", "12", "Rs 9,500.00"]], [false, true, true])),
        ])
        : new PdfReport("Monthly summary", "October 2026", "en", "Gulshan Society", new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero),
        [
            new PdfSection("Complaints", ["42 complaints were reported this month and 37 were resolved."], [("All complaints", "42"), ("Average time", "18.5 h")]),
            new PdfSection("By category", null, null, new PdfTable(["Category", "Count", "Cost"], [["Water supply", "20", "Rs 15,000.00"], ["Roads", "12", "Rs 9,500.00"]], [false, true, true])),
        ]);

    [Theory]
    [InlineData("en")]
    [InlineData("ur")]
    public void A_report_renders_to_a_real_pdf_in_either_language(string locale)
    {
        var pdf = new PdfRenderer().Render(Sample(locale));
        Assert.True(pdf.Length > 5_000, $"{pdf.Length} bytes");
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(pdf, pdf.Length - 16, 16));
    }

    [Fact]
    public void Urdu_text_is_drawn_with_the_embedded_arabic_script_font_so_it_does_not_come_out_as_boxes()
    {
        var renderer = new PdfRenderer();
        var urdu = Encoding.Latin1.GetString(renderer.Render(Sample("ur")));
        var english = Encoding.Latin1.GetString(renderer.Render(Sample("en")));
        Assert.Contains("NotoNaskhArabic", urdu);
        Assert.DoesNotContain("NotoNaskhArabic", english);
        Assert.Contains("NotoSans", english);
    }

    [Fact]
    public void Pages_can_be_rendered_as_images_for_a_look_at_the_layout()
    {
        var pages = new PdfRenderer().RenderImages(Sample("ur"));
        var page = Assert.Single(pages);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, page[..4]); // a PNG
        var dir = Environment.GetEnvironmentVariable("URBANFIX_PDF_PREVIEW");
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "report-ur.png"), page); File.WriteAllBytes(Path.Combine(dir, "report-en.png"), new PdfRenderer().RenderImages(Sample("en"))[0]); }
    }
}
