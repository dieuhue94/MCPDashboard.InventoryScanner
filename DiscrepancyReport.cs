using System.Data;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MCPDashboard.InventoryScanner;

/// <summary>
/// After a run, emails a summary of the open discrepancies (dbo.InventoryDiscrepancy) with a link to the dashboard's
/// Discrepancies page, so nobody has to remember to look. Settings: the "DiscrepancyReport" section of appsettings.json.
///
///   * Counts come from the database, so they match the Discrepancies page: open items per category, how many still
///     need a look (not reviewed) and how many this run found for the first time.
///   * By default it's only sent when something still needs a look (OnlyWhenToReview). Reviewed items stay on the
///     page until they're resolved, but don't trigger the email on their own.
///   * Sent anonymously through the SMTP relay on port 25. A failed email is logged; it never fails the run.
/// </summary>
public static class DiscrepancyReport
{
    private sealed record Settings(string? SmtpServer, int SmtpPort, string? From, List<string> To, string? DashboardUrl, bool OnlyWhenToReview);

    private sealed record CategoryCount(string Category, int Open, int ToReview, int NewThisRun);

    /// <summary>Same order as the dashboard's cards.</summary>
    private static readonly string[] CategoryOrder = ["New", "Not in list", "Retired but in list", "Not responding"];

    private const int MaxListed = 25;

    public static async Task SendAsync(string connectionString, DateTime runStartedUtc)
    {
        var settings = Load();
        if (settings is null)
        {
            Console.WriteLine("Discrepancy email not sent: no DiscrepancyReport section in appsettings.json.");
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.SmtpServer) || string.IsNullOrWhiteSpace(settings.From) || settings.To.Count == 0)
        {
            Console.WriteLine("Discrepancy email not sent: set DiscrepancyReport SmtpServer, From and To in appsettings.json.");
            return;
        }

        try
        {
            await using var db = new SqlConnection(connectionString);
            await db.OpenAsync();
            var counts = await ReadCountsAsync(db, runStartedUtc);
            var toReview = counts.Sum(c => c.ToReview);
            if (settings.OnlyWhenToReview && toReview == 0)
            {
                Console.WriteLine("Discrepancy email not sent: nothing needs review.");
                return;
            }
            var items = await ReadToReviewAsync(db);

            var link = string.IsNullOrWhiteSpace(settings.DashboardUrl) ? null : settings.DashboardUrl.TrimEnd('/') + "/discrepancies";
            using var message = new MailMessage
            {
                From = new MailAddress(settings.From),
                Subject = toReview == 0
                    ? "MCP Dashboard: no discrepancies to review"
                    : $"MCP Dashboard: {toReview} discrepanc{(toReview == 1 ? "y" : "ies")} to review",
                Body = Html(counts, items, toReview, link),
                IsBodyHtml = true
            };
            foreach (var to in settings.To)
            {
                message.To.Add(to);
            }

            // Anonymous, like the team's other jobs (Send-MailMessage without -Credential): the relay accepts internal mail on 25.
            using var smtp = new SmtpClient(settings.SmtpServer, settings.SmtpPort)
            {
                UseDefaultCredentials = false,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };
            await smtp.SendMailAsync(message);
            Console.WriteLine($"Discrepancy email sent to {string.Join(", ", settings.To)} ({toReview} to review).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Discrepancy email FAILED: {ex.Message}");
        }
    }

    private static Settings? Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return null;
        }
        using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        if (!json.RootElement.TryGetProperty("DiscrepancyReport", out var s))
        {
            return null;
        }
        string? Text(string name) => s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var to = s.TryGetProperty("To", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(a => a.GetString()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()).ToList()
            : [];
        var port = s.TryGetProperty("SmtpPort", out var p) && p.TryGetInt32(out var n) ? n : 25;
        var onlyWhenToReview = !s.TryGetProperty("OnlyWhenToReview", out var o) || o.ValueKind != JsonValueKind.False;
        return new Settings(Text("SmtpServer"), port, Text("From"), to, Text("DashboardUrl"), onlyWhenToReview);
    }

    private static async Task<List<CategoryCount>> ReadCountsAsync(SqlConnection db, DateTime runStartedUtc)
    {
        await using var cmd = new SqlCommand("""
            SELECT Category,
                   COUNT(*),
                   SUM(CASE WHEN ReviewedUtc IS NULL THEN 1 ELSE 0 END),
                   SUM(CASE WHEN FirstSeenUtc >= @runStarted THEN 1 ELSE 0 END)
            FROM dbo.InventoryDiscrepancy
            WHERE ResolvedUtc IS NULL
            GROUP BY Category;
            """, db);
        // The database clock may be a little behind this server's.
        cmd.Parameters.Add("@runStarted", SqlDbType.DateTime2).Value = runStartedUtc.AddMinutes(-5);
        var found = new Dictionary<string, CategoryCount>(StringComparer.OrdinalIgnoreCase);
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                found[r.GetString(0)] = new CategoryCount(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
            }
        }
        // Every category, even at zero, in the dashboard's order; any other category the job wrote goes last.
        return CategoryOrder.Select(c => found.GetValueOrDefault(c) ?? new CategoryCount(c, 0, 0, 0))
            .Concat(found.Values.Where(c => !CategoryOrder.Contains(c.Category, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    private static async Task<List<(string Category, string Server)>> ReadToReviewAsync(SqlConnection db)
    {
        await using var cmd = new SqlCommand("""
            SELECT TOP (@max) Category, ServerName
            FROM dbo.InventoryDiscrepancy
            WHERE ResolvedUtc IS NULL AND ReviewedUtc IS NULL
            ORDER BY FirstSeenUtc DESC, ServerName;
            """, db);
        cmd.Parameters.Add("@max", SqlDbType.Int).Value = MaxListed + 1;
        var items = new List<(string, string)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            items.Add((r.GetString(0), r.GetString(1)));
        }
        return items;
    }

    private static string Html(List<CategoryCount> counts, List<(string Category, string Server)> items, int toReview, string? link)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        const string cell = "padding:4px 12px;border-bottom:1px solid #ddd;";

        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#222;\">");
        html.Append(toReview == 0
            ? "<p>Last night's inventory scan found nothing new to review.</p>"
            : $"<p>Last night's inventory scan left <b>{toReview}</b> discrepanc{(toReview == 1 ? "y" : "ies")} to review.</p>");

        html.Append("<table style=\"border-collapse:collapse;\">");
        html.Append($"<tr><th style=\"{cell}text-align:left;\">Category</th><th style=\"{cell}\">To review</th>" +
                    $"<th style=\"{cell}\">Found this run</th><th style=\"{cell}\">Open (incl. reviewed)</th></tr>");
        foreach (var c in counts)
        {
            var weight = c.ToReview > 0 ? "font-weight:bold;" : "";
            html.Append($"<tr><td style=\"{cell}\">{E(c.Category)}</td>" +
                        $"<td style=\"{cell}{weight}text-align:center;\">{c.ToReview}</td>" +
                        $"<td style=\"{cell}text-align:center;\">{c.NewThisRun}</td>" +
                        $"<td style=\"{cell}text-align:center;\">{c.Open}</td></tr>");
        }
        html.Append("</table>");

        if (items.Count > 0)
        {
            html.Append("<p style=\"margin-top:16px;\">To review:</p><ul>");
            foreach (var (category, server) in items.Take(MaxListed))
            {
                html.Append($"<li>{E(server)} &ndash; {E(category)}</li>");
            }
            if (items.Count > MaxListed)
            {
                html.Append("<li>&hellip; and more on the dashboard</li>");
            }
            html.Append("</ul>");
        }

        if (link is not null)
        {
            html.Append($"<p><a href=\"{E(link)}\">Open the Discrepancies page</a></p>");
        }
        html.Append("<p style=\"color:#777;font-size:12px;\">Sent by the MCP Dashboard inventory job. Reviewed items don't trigger this email; " +
                    "they stay on the page until the scan no longer finds them.</p></div>");
        return html.ToString();
    }
}
