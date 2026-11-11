using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace UrbanFix.Infrastructure.WhatsApp;

/// <summary>
/// On startup, when inbound WhatsApp is on, makes sure the OpenWA session maps to a society (a <c>whatsapp_channel</c> row keyed by
/// the session id) so inbound messages find their organization and replies can be sent. Also tells the gateway, once, to deliver
/// messages to our webhook. Both steps are best-effort and idempotent: a failure is logged and never stops the application.
/// </summary>
public sealed class OpenWaChannelSeeder(IConfiguration config, OpenWaOptions options, IHttpClientFactory httpFactory, ILogger<OpenWaChannelSeeder> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!options.InboundEnabled) return;
        try { await SeedChannelAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "OpenWA channel could not be seeded; inbound WhatsApp will not map to an organization"); }
        try { await RegisterWebhookAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "OpenWA webhook could not be registered automatically; register it by hand (see docs/openwa-complaints.md)"); }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Uses the owner connection (bypasses row-level security) to upsert the channel, the same role migrations run as.</summary>
    private async Task SeedChannelAsync(CancellationToken ct)
    {
        var connectionString = config.GetConnectionString("Owner") ?? config.GetConnectionString("App")
            ?? throw new InvalidOperationException("No database connection string for the OpenWA channel seeder");
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);

        var orgId = options.OrganizationId;
        if (orgId is null)
        {
            // Not told which society: fall back to the only one, if there is exactly one.
            await using var find = new NpgsqlCommand("SELECT id FROM organizations LIMIT 2", db);
            await using var reader = await find.ExecuteReaderAsync(ct);
            var ids = new List<Guid>();
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
            if (ids.Count != 1)
            {
                log.LogWarning("OpenWA inbound is on but OpenWa:OrganizationId is not set and there are {Count} organizations; set it so inbound messages map to a society", ids.Count);
                return;
            }
            orgId = ids[0];
        }

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO whatsapp_channels (id, organization_id, phone_number_id, display_number, is_active)
            VALUES (@id, @org, @session, @number, true)
            ON CONFLICT (phone_number_id) DO UPDATE SET organization_id = EXCLUDED.organization_id, is_active = true
            """, db);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("org", orgId.Value);
        cmd.Parameters.AddWithValue("session", options.SessionId);
        cmd.Parameters.AddWithValue("number", string.IsNullOrEmpty(options.DisplayNumber) ? options.SessionId : options.DisplayNumber);
        await cmd.ExecuteNonQueryAsync(ct);
        log.LogInformation("OpenWA channel ready: session {Session} serves organization {Org}", options.SessionId, orgId);
    }

    private async Task RegisterWebhookAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.WebhookUrl) || string.IsNullOrEmpty(options.BaseUrl)) return;

        using var http = httpFactory.CreateClient();
        OpenWaSmsSender.Configure(http, options);
        var path = $"api/sessions/{options.SessionId}/webhooks";

        // Skip if one already points at our URL, so a restart does not pile up duplicates.
        using var existing = new HttpRequestMessage(HttpMethod.Get, path);
        existing.Headers.TryAddWithoutValidation("X-API-Key", options.ApiKey);
        using var got = await http.SendAsync(existing, ct);
        if (got.IsSuccessStatusCode)
        {
            var listed = JsonNode.Parse(await got.Content.ReadAsStringAsync(ct));
            var array = listed as JsonArray ?? listed?["data"] as JsonArray ?? listed?["webhooks"] as JsonArray;
            if (array is not null && array.Any(w => w?["url"]?.ToString() == options.WebhookUrl))
            {
                log.LogInformation("OpenWA webhook already registered for session {Session}", options.SessionId);
                return;
            }
        }

        var body = new JsonObject
        {
            ["url"] = options.WebhookUrl,
            ["events"] = new JsonArray("message.received"),
            ["secret"] = options.WebhookSecret,
        };
        using var post = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        post.Headers.TryAddWithoutValidation("X-API-Key", options.ApiKey);
        using var created = await http.SendAsync(post, ct);
        if (created.IsSuccessStatusCode) log.LogInformation("OpenWA webhook registered: {Url}", options.WebhookUrl);
        else log.LogWarning("OpenWA webhook registration answered {Status}: {Body}", (int)created.StatusCode, await created.Content.ReadAsStringAsync(ct));
    }
}
