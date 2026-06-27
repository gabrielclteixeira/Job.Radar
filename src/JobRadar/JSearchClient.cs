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
            // JSearch needs BOTH a country code AND the location (incl. country NAME) in the query text —
            // "backend developer in Porto" alone returns nothing; "backend developer Porto Portugal" + country=pt works.
            string code = (string.IsNullOrWhiteSpace(cfg.Country) ? "pt" : cfg.Country.Trim()).ToLowerInvariant();
            string countryName = "";
            try { countryName = new System.Globalization.RegionInfo(code.ToUpperInvariant()).EnglishName; } catch { /* unknown code */ }

            // One query keeps quota use down: top job title + location + country name.
            string topTitle = string.Join(" ", queries.Where(s => !string.IsNullOrWhiteSpace(s)).Take(1));
            if (string.IsNullOrWhiteSpace(topTitle)) topTitle = "software developer";
            string q = string.Join(" ", new[] { topTitle, location, countryName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            string common = $"query={Uri.EscapeDataString(q)}&page=1&num_pages=1&country={code}";

            // Two distributions of the same JSearch API (identical "data[]" response shape):
            //   • OpenWeb Ninja (direct, the publisher's own platform) — api.openwebninja.com, x-api-key header.
            //   • RapidAPI — jsearch.p.rapidapi.com, X-RapidAPI-Key/Host headers.
            bool rapid = string.Equals(cfg.Provider, "rapidapi", StringComparison.OrdinalIgnoreCase);
            using var req = new HttpRequestMessage(HttpMethod.Get, rapid
                ? $"https://{(string.IsNullOrWhiteSpace(cfg.ApiHost) ? "jsearch.p.rapidapi.com" : cfg.ApiHost.Trim())}/search?{common}"
                : $"https://api.openwebninja.com/jsearch/search?{common}");
            if (rapid)
            {
                string host = string.IsNullOrWhiteSpace(cfg.ApiHost) ? "jsearch.p.rapidapi.com" : cfg.ApiHost.Trim();
                req.Headers.TryAddWithoutValidation("X-RapidAPI-Key", cfg.ApiKey.Trim());
                req.Headers.TryAddWithoutValidation("X-RapidAPI-Host", host);
            }
            else
            {
                req.Headers.TryAddWithoutValidation("x-api-key", cfg.ApiKey.Trim());
            }

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
