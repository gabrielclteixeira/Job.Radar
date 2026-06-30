using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobRadar;

/// <summary>
/// Jobicy remote-jobs source (https://jobicy.com/api/v2/remote-jobs) — KEYLESS and free, JSON.
/// Remote jobs only, filterable by region (geo=europe / portugal / …). Per Jobicy's ToS we credit Jobicy
/// (Source = "jobicy") and keep the job's own <c>url</c> as the apply link. Returns RawJob list; empty on failure.
/// </summary>
public static class JobicyClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<List<RawJob>> FetchJobsAsync(
        JobicyConfig cfg, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var jobs = new List<RawJob>();
        if (cfg is null || !cfg.Enabled) return jobs;

        try
        {
            int count = Math.Clamp(cfg.MaxItems > 0 ? cfg.MaxItems : 50, 1, 100);
            string geo = string.IsNullOrWhiteSpace(cfg.Geo) ? "" : $"&geo={Uri.EscapeDataString(cfg.Geo.Trim())}";
            string url = $"https://jobicy.com/api/v2/remote-jobs?count={count}{geo}";

            log?.Report(Loc.Instance.T("jobicy.fetching"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(25_000);
            using var resp = await Http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) { log?.Report(Loc.Instance.F("jobicy.failed", (int)resp.StatusCode)); return jobs; }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (!doc.RootElement.TryGetProperty("jobs", out var arr) || arr.ValueKind != JsonValueKind.Array) return jobs;

            foreach (var it in arr.EnumerateArray())
            {
                string title = Str(it, "jobTitle");
                string link = Str(it, "url");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;

                // Jobicy salaries are per-period; only trust them as annual bands when salaryPeriod == yearly.
                bool yearly = string.Equals(Str(it, "salaryPeriod"), "yearly", StringComparison.OrdinalIgnoreCase);
                string body = Str(it, "jobExcerpt") is { Length: > 0 } ex ? ex : Str(it, "jobDescription");

                jobs.Add(new RawJob(
                    title,
                    Str(it, "companyName"),
                    Str(it, "jobGeo"),            // e.g. "EMEA" / "Europe"
                    "remote",
                    link,
                    StripTags(body),
                    "jobicy",
                    Str(it, "pubDate"),
                    yearly ? Num(it, "annualSalaryMin") : 0,
                    yearly ? Num(it, "annualSalaryMax") : 0,
                    Str(it, "salaryCurrency")));
            }
            log?.Report(Loc.Instance.F("jobicy.count", jobs.Count));
        }
        catch (Exception ex) { log?.Report(Loc.Instance.F("jobicy.error", ex.Message)); Diag.Warn("jobicy fetch failed | " + ex.Message); }
        return jobs;
    }

    private static double Num(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Str(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        return "";
    }

    private static string StripTags(string s)
        => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, "<[^>]+>", " ").Replace("&hellip;", "…").Replace("&amp;", "&").Trim();
}
