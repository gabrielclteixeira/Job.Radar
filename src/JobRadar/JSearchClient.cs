using System.Text.Json;

namespace JobRadar;

/// <summary>
/// Optional JSearch source via RapidAPI (https://jsearch.p.rapidapi.com). Aggregates Google-for-Jobs
/// listings (LinkedIn/Indeed/Glassdoor/…). Keyed and quota-limited (free tier), so it's opt-in and
/// gated by the same cost confirmation as Apify. Returns RawJob list; empty on any failure.
/// </summary>
public static class JSearchClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    /// <summary>RapidAPI rate-limit from the most recent /search response (-1 = unknown). For the Usage view.</summary>
    public static (int Remaining, int Limit) LastQuota { get; private set; } = (-1, -1);

    private static int Header(HttpResponseMessage r, string name)
        => r.Headers.TryGetValues(name, out var vals) && int.TryParse(System.Linq.Enumerable.FirstOrDefault(vals), out var n) ? n : -1;

    public static async Task<List<RawJob>> FetchJobsAsync(
        JSearchConfig cfg, IEnumerable<string> queries, string location,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var jobs = new List<RawJob>();
        if (cfg is null || !cfg.Enabled || string.IsNullOrWhiteSpace(cfg.ApiKey)) return jobs;

        try
        {
            // One query keeps quota use down: top job title (+ location for relevance).
            string q = string.Join(" ", queries.Where(s => !string.IsNullOrWhiteSpace(s)).Take(1));
            if (string.IsNullOrWhiteSpace(q)) q = "software developer";
            if (!string.IsNullOrWhiteSpace(location)) q += " in " + location;

            string host = string.IsNullOrWhiteSpace(cfg.ApiHost) ? "jsearch.p.rapidapi.com" : cfg.ApiHost.Trim();
            string url = $"https://{host}/search?query={Uri.EscapeDataString(q)}&page=1&num_pages=1";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-RapidAPI-Key", cfg.ApiKey.Trim());
            req.Headers.TryAddWithoutValidation("X-RapidAPI-Host", host);

            log?.Report(Loc.Instance.T("jsearch.fetching"));
            using var resp = await Http.SendAsync(req, ct);
            // Capture the monthly request quota from RapidAPI's rate-limit headers.
            int remaining = Header(resp, "x-ratelimit-requests-remaining"), limit = Header(resp, "x-ratelimit-requests-limit");
            if (remaining >= 0 || limit >= 0) LastQuota = (remaining, limit);
            if (!resp.IsSuccessStatusCode)
            {
                log?.Report(Loc.Instance.F("jsearch.failed", (int)resp.StatusCode));
                return jobs;
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return jobs;

            int max = cfg.MaxItems > 0 ? cfg.MaxItems : 20;
            foreach (var it in data.EnumerateArray())
            {
                if (jobs.Count >= max) break;
                string title = Str(it, "job_title");
                string link = Str(it, "job_apply_link", "job_offer_expiration_datetime_utc");
                if (string.IsNullOrWhiteSpace(title)) continue;

                string city = Str(it, "job_city");
                string country = Str(it, "job_country");
                string loc = string.Join(", ", new[] { city, country }.Where(s => !string.IsNullOrWhiteSpace(s)));
                bool remote = it.TryGetProperty("job_is_remote", out var rem) && rem.ValueKind == JsonValueKind.True;

                jobs.Add(new RawJob(
                    title,
                    Str(it, "employer_name"),
                    loc,
                    remote ? "remote" : "",
                    Str(it, "job_apply_link"),
                    Str(it, "job_description"),
                    "jsearch",
                    Str(it, "job_posting_date", "job_posted_at_datetime_utc"),
                    Num(it, "job_min_salary"),
                    Num(it, "job_max_salary"),
                    Str(it, "job_salary_currency") is { Length: > 0 } cur ? cur : (HasSalary(it) ? "USD" : "")));
            }
            log?.Report(Loc.Instance.F("jsearch.count", jobs.Count));
        }
        catch (Exception ex) { log?.Report(Loc.Instance.F("jsearch.error", ex.Message)); }
        return jobs;
    }

    private static bool HasSalary(JsonElement e) => Num(e, "job_min_salary") > 0 || Num(e, "job_max_salary") > 0;

    private static double Num(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Str(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        return "";
    }
}
