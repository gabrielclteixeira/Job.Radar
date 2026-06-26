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

    /// <summary>Validates the token (free — no credits) and lists "LinkedIn jobs" actors from the store,
    /// so setup is mostly automatic: paste token → pick an actor from the dropdown.</summary>
    public static async Task<(bool ok, string message, List<string> actors)> ProbeAsync(string token, CancellationToken ct = default)
    {
        var actors = new List<string>();
        if (string.IsNullOrWhiteSpace(token)) return (false, "Cola primeiro o teu API token.", actors);
        token = token.Trim();
        try
        {
            // 1) Validate the token (free).
            using var me = await Http.GetAsync($"https://api.apify.com/v2/users/me?token={Uri.EscapeDataString(token)}", ct);
            if (!me.IsSuccessStatusCode)
                return (false, $"Token inválido ou sem acesso (HTTP {(int)me.StatusCode}).", actors);
            string user = "", plan = "";
            using (var d = JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct)))
                if (d.RootElement.TryGetProperty("data", out var dat))
                {
                    user = Str(dat, "username");
                    if (dat.TryGetProperty("plan", out var pl) && pl.ValueKind == JsonValueKind.Object)
                        plan = Str(pl, "id", "name");
                }

            // 2) Discover LinkedIn-jobs actors from the store (free listing).
            using var st = await Http.GetAsync($"https://api.apify.com/v2/store?search={Uri.EscapeDataString("linkedin jobs")}&limit=25&token={Uri.EscapeDataString(token)}", ct);
            if (st.IsSuccessStatusCode)
                using (var d = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)))
                    if (d.RootElement.TryGetProperty("data", out var dat) && dat.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var it in items.EnumerateArray())
                        {
                            string un = Str(it, "username"), nm = Str(it, "name");
                            if (!string.IsNullOrWhiteSpace(un) && !string.IsNullOrWhiteSpace(nm)) actors.Add($"{un}/{nm}");
                        }

            string msg = $"Ligação OK · @{user}" + (plan.Length > 0 ? $" · plano {plan}" : "") + $" · {actors.Count} actors de LinkedIn (escolhe um; confirma o preço no Apify)";
            return (true, msg, actors);
        }
        catch (Exception ex) { return (false, "Erro: " + ex.Message, actors); }
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
