using System.Text;
using System.Text.Json;

namespace JobRadar;

/// <summary>
/// Optional LinkedIn connector via the Apify platform. PAID — every run consumes the user's Apify
/// credits, so the UI confirms cost before each search that uses it. Runs an Apify "LinkedIn jobs"
/// actor synchronously and maps the dataset items to <see cref="RawJob"/>. Best-effort and tolerant
/// of different actors' output shapes (tries several common field names).
/// </summary>
public static class ApifyClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<List<RawJob>> FetchLinkedInJobsAsync(
        ApifyConfig cfg, IEnumerable<string> queries, string location,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var jobs = new List<RawJob>();
        if (cfg is null || !cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Token) || string.IsNullOrWhiteSpace(cfg.ActorId))
            return jobs;

        try
        {
            // Apify actor ids use "username~actor-name" in the URL path.
            string actor = cfg.ActorId.Trim().Replace("/", "~");
            int max = cfg.MaxItems > 0 ? cfg.MaxItems : 50;
            string q = string.Join(" ", queries.Where(s => !string.IsNullOrWhiteSpace(s)).Take(3));

            // Run synchronously and get the dataset items in one call (no polling).
            string url = $"https://api.apify.com/v2/acts/{actor}/run-sync-get-dataset-items"
                       + $"?token={Uri.EscapeDataString(cfg.Token)}&clean=true";

            // Cover common input field names across LinkedIn-jobs actors; extra keys are usually ignored.
            var input = new Dictionary<string, object?>
            {
                ["title"] = q, ["keyword"] = q, ["searchQuery"] = q, ["queries"] = new[] { q },
                ["location"] = location, ["maxItems"] = max, ["rows"] = max, ["count"] = max,
            };

            log?.Report("A obter vagas do LinkedIn via Apify (consome créditos)…");
            using var content = new StringContent(JsonSerializer.Serialize(input), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log?.Report($"Apify falhou (HTTP {(int)resp.StatusCode}) — verifica o token/actor.");
                return jobs;
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return jobs;

            foreach (var it in doc.RootElement.EnumerateArray())
            {
                string title = Str(it, "title", "jobTitle", "position");
                string link = Str(it, "url", "link", "jobUrl", "applyUrl");
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(link)) continue;
                jobs.Add(new RawJob(
                    title,
                    Str(it, "companyName", "company", "company_name"),
                    Str(it, "location", "place", "jobLocation"),
                    "", link,
                    Str(it, "description", "descriptionText", "jobDescription"),
                    "linkedin",
                    Str(it, "postedAt", "postedTime", "publishedAt", "listedAt")));
            }
            log?.Report($"Apify: {jobs.Count} vagas do LinkedIn.");
        }
        catch (Exception ex) { log?.Report("Apify erro: " + ex.Message); }
        return jobs;
    }

    private static string Str(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    return n.GetString() ?? "";
            }
        return "";
    }
}
