using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Pdf;
using UrbanFix.Infrastructure.Persistence;

namespace UrbanFix.Api.Reports;

/// <summary>
/// The management summary as a PDF (DB-04) and as the monthly summary (DB-05). It reads the same queries as the dashboard and the cost
/// report, so the figures on paper are the figures on screen. The cost section is included only when the reader may see costs.
/// </summary>
public static class SummaryReportBuilder
{
    private static readonly Dictionary<string, (string En, string Ur)> Text = new()
    {
        ["title"] = ("Management summary", "انتظامی خلاصہ"),
        ["overview"] = ("Overview", "جائزہ"),
        ["total"] = ("Complaints reported", "درج شدہ شکایات"),
        ["resolved"] = ("Resolved or closed", "حل یا بند"),
        ["avg"] = ("Average time to resolve (hours)", "حل ہونے کا اوسط وقت (گھنٹے)"),
        ["reopened"] = ("Complaints reopened", "دوبارہ کھلی شکایات"),
        ["rating"] = ("Average rating (out of 5)", "اوسط درجہ بندی (5 میں سے)"),
        ["byStatus"] = ("By status", "حالت کے مطابق"),
        ["byDepartment"] = ("By department", "شعبے کے مطابق"),
        ["bySector"] = ("By sector", "سیکٹر کے مطابق"),
        ["status"] = ("Status", "حالت"),
        ["department"] = ("Department", "شعبہ"),
        ["sector"] = ("Sector", "سیکٹر"),
        ["count"] = ("Count", "تعداد"),
        ["workload"] = ("Team workload", "ٹیموں کا کام"),
        ["team"] = ("Team", "ٹیم"),
        ["notStarted"] = ("Not started", "شروع نہیں"),
        ["inProgress"] = ("In progress", "جاری"),
        ["awaiting"] = ("Awaiting sign-off", "منظوری کے منتظر"),
        ["overdue"] = ("Overdue jobs", "تاخیر شدہ کام"),
        ["job"] = ("Job", "کام"),
        ["reference"] = ("Reference", "حوالہ"),
        ["priority"] = ("Priority", "ترجیح"),
        ["hoursOver"] = ("Hours over", "تاخیر (گھنٹے)"),
        ["costs"] = ("Costs", "اخراجات"),
        ["estimated"] = ("Estimated", "تخمینہ"),
        ["actual"] = ("Actual", "اصل"),
        ["category"] = ("Category", "زمرہ"),
        ["jobs"] = ("Jobs", "کام"),
        ["overruns"] = ("Jobs over their estimate", "تخمینے سے زیادہ لاگت کے کام"),
        ["period"] = ("Period", "مدت"),
        ["allTime"] = ("All time", "تمام وقت"),
        ["none"] = ("Nothing to report.", "رپورٹ کرنے کو کچھ نہیں۔"),
    };

    private static readonly Dictionary<string, string> StatusUr = new()
    {
        ["New"] = "نئی", ["OnHold"] = "روکی ہوئی", ["UnderReview"] = "زیر جائزہ", ["Assigned"] = "سونپی گئی", ["InProgress"] = "جاری", ["Resolved"] = "حل شدہ",
        ["Closed"] = "بند", ["Rejected"] = "مسترد", ["Merged"] = "ضم",
    };

    private static readonly Dictionary<string, string> PriorityUr = new() { ["Low"] = "کم", ["Normal"] = "عام", ["High"] = "اعلیٰ", ["Urgent"] = "فوری" };

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string Money(decimal value) => "Rs " + value.ToString("N2", CultureInfo.InvariantCulture);

    public static async Task<PdfReport> BuildAsync(
        AppDbContext db, string organizationName, string locale, DateTimeOffset? from, DateTimeOffset? to, bool includeCosts, DateTimeOffset now, CancellationToken ct)
    {
        var ur = locale == "ur";
        string L(string key) => ur ? Text[key].Ur : Text[key].En;
        string Status(string s) => ur && StatusUr.TryGetValue(s, out var u) ? u : s;

        var summary = await DashboardEndpoints.BuildSummaryAsync(db, from, to, ct);
        var sections = new List<PdfSection>();

        var facts = new List<(string, string)>
        {
            (L("total"), N(summary.Total)),
            (L("resolved"), N(summary.ResolvedCount)),
            (L("avg"), summary.AverageResolutionHours is { } h ? h.ToString("0.0", CultureInfo.InvariantCulture) : "—"),
            (L("reopened"), N(summary.ReopenedComplaints)),
        };
        if (summary.AverageRating is { } r) facts.Add((L("rating"), $"{r.ToString("0.0", CultureInfo.InvariantCulture)} ({N(summary.RatingCount)})"));
        sections.Add(new PdfSection(L("overview"), null, facts));

        PdfTable Counts(string header, IEnumerable<DashboardEndpoints.CountRow> rows, Func<string, string>? name = null) =>
            new([header, L("count")], rows.Where(x => x.Count > 0).Select(x => (IReadOnlyList<string>)[name?.Invoke(x.Name) ?? x.Name, N(x.Count)]).ToList(), [false, true]);

        sections.Add(new PdfSection(L("byStatus"), Table: Counts(L("status"), summary.ByStatus, Status)));
        sections.Add(new PdfSection(L("byDepartment"), Table: Counts(L("department"), summary.ByDepartment)));
        sections.Add(new PdfSection(L("bySector"), Table: Counts(L("sector"), summary.BySector)));

        if (summary.TeamWorkload.Count > 0)
            sections.Add(new PdfSection(L("workload"), Table: new PdfTable([L("team"), L("notStarted"), L("inProgress"), L("awaiting")],
                summary.TeamWorkload.Select(w => (IReadOnlyList<string>)[w.Team, N(w.Open), N(w.InProgress), N(w.PendingVerification)]).ToList(), [false, true, true, true])));

        if (summary.OverdueJobs.Count > 0)
            sections.Add(new PdfSection(L("overdue"), Table: new PdfTable([L("job"), L("reference"), L("priority"), L("hoursOver")],
                summary.OverdueJobs.Take(25).Select(j => (IReadOnlyList<string>)[j.Number, j.Reference, ur && PriorityUr.TryGetValue(j.Priority, out var p) ? p : j.Priority, j.HoursOverdue.ToString("0.0", CultureInfo.InvariantCulture)]).ToList(),
                [false, false, false, true])));

        if (includeCosts)
        {
            var costs = await CostReportEndpoints.BuildAsync(db, "category", from, to, ct);
            var overruns = await JobEndpoints.OverrunListAsync(db, ct);
            sections.Add(new PdfSection(L("costs"), null,
                [(L("jobs"), N(costs.Jobs)), (L("estimated"), Money(costs.Estimated)), (L("actual"), Money(costs.Actual)), (L("overruns"), N(overruns.Count))],
                costs.Rows.Count == 0 ? null : new PdfTable([L("category"), L("jobs"), L("estimated"), L("actual")],
                    costs.Rows.Select(c => (IReadOnlyList<string>)[c.Name, N(c.Jobs), Money(c.Estimated), Money(c.Actual)]).ToList(), [false, true, true, true])));
        }

        var period = from is null && to is null ? L("allTime")
            : $"{(from is { } f ? f.ToString("yyyy-MM-dd") : "…")} – {(to is { } t ? t.ToString("yyyy-MM-dd") : now.ToString("yyyy-MM-dd"))}";
        return new PdfReport(L("title"), $"{L("period")}: {period}", locale, organizationName, now, sections);
    }
}
