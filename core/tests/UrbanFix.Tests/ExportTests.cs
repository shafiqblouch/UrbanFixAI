using System.Net;
using System.Text;
using UrbanFix.Api.Endpoints;
using UrbanFix.Infrastructure.Pdf;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;

namespace UrbanFix.Tests;

public class CsvWriterTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-cmd|' /C calc'!A0", "'-cmd|' /C calc'!A0")]
    [InlineData("-5", "-5")]       // a number is not a formula
    [InlineData("+3.5", "+3.5")]
    [InlineData("", "")]
    public void Cells_are_escaped_and_formulas_are_defused(string input, string expected) => Assert.Equal(expected, CsvWriter.Cell(input));

    [Fact]
    public void The_file_starts_with_a_byte_order_mark_so_spreadsheets_read_urdu_correctly()
    {
        var bytes = CsvWriter.Write(["نام", "Count"], [["پانی", "3"]]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("نام,Count\r\nپانی,3\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }
}

[Collection("db")]
public class ExportTests(TestApp app)
{
    private static async Task<string> Text(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadAsStringAsync()).TrimStart('﻿');
    }

    private static async Task<CreatedDto> Complain(Society s, string text, ApiSession? who = null)
        => (await (await (who ?? s.Resident).Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;

    [Fact]
    public async Task The_complaint_export_has_what_the_list_has_and_defuses_spreadsheet_formulas()
    {
        var s = await Society.CreateAsync(app);
        await Complain(s, "No water in the street");
        await Complain(s, "=HYPERLINK(\"http://evil.test\",\"click\")");

        var res = await s.Manager.Client.GetAsync("/exports/complaints?format=csv");
        Assert.Equal("text/csv", res.Content.Headers.ContentType!.MediaType);
        Assert.Contains("complaints-", res.Content.Headers.ContentDisposition!.FileName);
        var csv = await Text(res);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Reference,Status,Priority,Category,Channel,Location,Created,Resolved,Description", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("No water in the street", csv);
        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.Contains("'=HYPERLINK", csv);
    }

    [Fact]
    public async Task An_export_respects_the_filters_and_what_the_person_may_see()
    {
        var s = await Society.CreateAsync(app);
        await Complain(s, "Mine");
        await Complain(s, "The neighbour's", s.Neighbour);

        var neighbours = await Text(await s.Resident.Client.GetAsync("/exports/complaints"));
        Assert.Contains("Mine", neighbours);
        Assert.DoesNotContain("The neighbour's", neighbours); // a resident exports only their own

        var all = await Text(await s.Manager.Client.GetAsync("/exports/complaints"));
        Assert.Contains("The neighbour's", all);
        var filtered = await Text(await s.Manager.Client.GetAsync("/exports/complaints?q=neighbour"));
        Assert.DoesNotContain("Mine", filtered);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.GetAsync("/exports/complaints?format=xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.GetAsync($"/exports/complaints?locationId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Phone_numbers_leave_the_system_only_with_people_who_triage()
    {
        var s = await Society.CreateAsync(app);
        await s.Office.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Walk-in complaint", "phone", s.CategoryId, s.HouseId, null, null, s.ResidentId, "0300 1234567"));

        Assert.Contains("Reporter phone", await Text(await s.Office.Client.GetAsync("/exports/complaints")));
        Assert.DoesNotContain("Reporter phone", await Text(await s.Resident.Client.GetAsync("/exports/complaints")));
    }

    [Fact]
    public async Task A_complaint_list_can_be_downloaded_as_a_pdf_in_either_language()
    {
        var s = await Society.CreateAsync(app);
        await Complain(s, "No water in the street");

        var en = await s.Manager.Client.GetAsync("/exports/complaints?format=pdf&lang=en");
        Assert.Equal("application/pdf", en.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString((await en.Content.ReadAsByteArrayAsync())[..5]));

        var ur = await s.Manager.Client.GetAsync("/exports/complaints?format=pdf&lang=ur");
        var bytes = await ur.Content.ReadAsByteArrayAsync();
        Assert.Contains("NotoNaskhArabic", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public async Task The_summary_exports_as_pdf_and_csv_and_costs_appear_only_for_those_who_may_see_them()
    {
        var s = await Society.CreateAsync(app);
        await Complain(s, "No water");
        var accounts = await app.AddMemberAsync(s.Manager, s.Org, Role.Accounts);

        var pdf = await s.Manager.Client.GetAsync("/exports/summary");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);

        var managerCsv = await Text(await s.Manager.Client.GetAsync("/exports/summary?format=csv"));
        Assert.Contains("Complaints reported,1", managerCsv);
        Assert.Contains("Costs", managerCsv);
        var officeCsv = await Text(await s.Office.Client.GetAsync("/exports/summary?format=csv"));
        Assert.Contains("Complaints reported,1", officeCsv);
        Assert.DoesNotContain("Costs", officeCsv);

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.GetAsync("/exports/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/exports/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accounts.Client.GetAsync("/exports/summary")).StatusCode); // accounts do not read complaint dashboards
    }

    [Fact]
    public async Task The_cost_export_needs_the_cost_permission()
    {
        var s = await Society.CreateAsync(app);
        Assert.Contains("category,Jobs,Estimated,Actual,Over / under", await Text(await s.Manager.Client.GetAsync("/exports/costs")));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Office.Client.GetAsync("/exports/costs")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.GetAsync("/exports/costs?groupBy=colour")).StatusCode);
    }

    [Fact]
    public async Task Every_export_is_written_to_the_audit_log()
    {
        var s = await Society.CreateAsync(app);
        await Complain(s, "No water");
        await s.Manager.Client.GetAsync("/exports/complaints?format=csv");
        await s.Manager.Client.GetAsync("/exports/summary?format=pdf");

        var entries = (await s.Manager.Client.GetFromJsonAsync<List<AdminEndpoints.AuditDto>>("/audit?entityType=Export"))!;
        Assert.Contains(entries, e => e.EntityId == "complaints" && e.Action == "exported");
        Assert.Contains(entries, e => e.EntityId == "summary");
    }
}
