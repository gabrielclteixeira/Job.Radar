using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobRadar;

/// <summary>
/// Himalayas remote-jobs source (https://himalayas.app/jobs/api) — KEYLESS and free, JSON. Requires a
/// browser User-Agent (the API 404s without one). Remote jobs only; we keep only listings open to
/// Portugal / Europe / worldwide (the API restricts by candidate residence, not city). Paginated in pages
/// of 20 (the API cap). Per Himalayas' ToS we attribute (Source = "himalayas") and don't resubmit jobs to
/// other boards — fine for a personal radar. Returns RawJob list; empty on failure.
/// </summary>
public static class HimalayasClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
    private const int PageSize = 20;   // API hard cap per request

    // EU/EEA countries (+ UK/Switzerland) plus "open to anyone" markers. A remote job is kept if it has no
    // residence restriction, or allows at least one of these.
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "portugal","spain","france","germany","italy","netherlands","ireland","belgium","luxembourg","austria",
        "poland","czechia","czech republic","slovakia","slovenia","hungary","romania","bulgaria","greece",
        "croatia","denmark","sweden","finland","estonia","latvia","lithuania","cyprus","malta",
        "norway","iceland","liechtenstein","united kingdom","switzerland",
        "europe","european union","eu","eea","emea","worldwide","anywhere",
    };

    public static async Task<List<RawJob>> FetchJobsAsync(
        HimalayasConfig cfg, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var jobs = new List<RawJob>();
        if (cfg is null || !cfg.Enabled) return jobs;

        try
        {
            int target = Math.Clamp(cfg.MaxItems > 0 ? cfg.MaxItems : 40, 1, 200);
            log?.Report(Loc.Instance.T("himalayas.fetching"));

            for (int offset = 0; jobs.Count < target && offset < 200; offset += PageSize)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://himalayas.app/jobs/api?limit={PageSize}&offset={offset}");
                req.Headers.TryAddWithoutValidation("User-Agent", Ua);   // required — 404 without it
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(25_000);
                using var resp = await Http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode) { log?.Report(Loc.Instance.F("himalayas.failed", (int)resp.StatusCode)); break; }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                if (!doc.RootElement.TryGetProperty("jobs", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                    break;

                foreach (var it in arr.EnumerateArray())
                {
                    string title = Str(it, "title");
                    string link = Str(it, "applicationLink") is { Length: > 0 } a ? a : Str(it, "guid");
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;

                    var (relevant, locText) = Location(it);
                    if (!relevant) continue;   // skip US-only / other-region remote roles

                    bool yearly = string.Equals(Str(it, "salaryPeriod"), "yearly", StringComparison.OrdinalIgnoreCase);
                    string body = Str(it, "excerpt") is { Length: > 0 } ex ? ex : Str(it, "description");

                    jobs.Add(new RawJob(
                        title,
                        Str(it, "companyName"),
                        locText,
                        "remote",
                        link,
                        StripTags(body),
                        "himalayas",
                        EpochToIso(Str(it, "pubDate")),
                        yearly ? Num(it, "minSalary") : 0,
                        yearly ? Num(it, "maxSalary") : 0,
                        Str(it, "currency")));
                    if (jobs.Count >= target) break;
                }
            }
            log?.Report(Loc.Instance.F("himalayas.count", jobs.Count));
        }
        catch (Exception ex) { log?.Report(Loc.Instance.F("himalayas.error", ex.Message)); Diag.Warn("himalayas fetch failed | " + ex.Message); }
        return jobs;
    }

    /// <summary>(keep?, location text). Keeps jobs with no residence restriction or one allowing PT/EU/worldwide.</summary>
    private static (bool, string) Location(JsonElement it)
    {
        if (!it.TryGetProperty("locationRestrictions", out var r) || r.ValueKind != JsonValueKind.Array || r.GetArrayLength() == 0)
            return (true, "Remote");   // unrestricted → open to anyone (incl. PT/EU)

        var names = new List<string>();
        bool keep = false;
        foreach (var c in r.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.String) continue;
            string name = c.GetString() ?? "";
            if (name.Length == 0) continue;
            names.Add(name);
            if (Allowed.Contains(name) || name.Contains("Europe", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Worldwide", StringComparison.OrdinalIgnoreCase) || name.Contains("Anywhere", StringComparison.OrdinalIgnoreCase))
                keep = true;
        }
        return (keep, names.Count > 0 ? "Remote · " + string.Join(", ", names) : "Remote");
    }

    private static string EpochToIso(string s)
        => long.TryParse(s, out var sec) && sec > 0
            ? DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime.ToString("yyyy-MM-dd")
            : s;

    private static double Num(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble()
             : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var n) ? n : 0;
    }

    private static string Str(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        return "";
    }

    private static string StripTags(string s)
        => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, "<[^>]+>", " ").Replace("&amp;", "&").Trim();
}
