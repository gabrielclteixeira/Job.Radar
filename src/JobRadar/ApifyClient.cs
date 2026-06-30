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
            // Search ONE specific role (the top job title), not several titles mushed into one space-separated
            // string — that fuzzy-matched and returned a wide variety. Mirrors JSearch's single-role query.
            string q = queries.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
                       ?? "software developer";

            // Run synchronously and get the dataset items in one call (no polling).
            string url = $"https://api.apify.com/v2/acts/{actor}/run-sync-get-dataset-items"
                       + $"?token={Uri.EscapeDataString(cfg.Token)}&clean=true";

            // Most LinkedIn-jobs actors are URL-based: they require a LinkedIn job-search URL list (e.g.
            // curious_coder/linkedin-jobs-scraper errors with "Field input.urls is required"). Build that URL
            // from the role + location, and ALSO pass the keyword fields for the keyword-based actors. Extra
            // keys are ignored by these actors' schemas.
            string liUrl = "https://www.linkedin.com/jobs/search/?keywords=" + Uri.EscapeDataString(q)
                         + (string.IsNullOrWhiteSpace(location) ? "" : "&location=" + Uri.EscapeDataString(location));
            var input = new Dictionary<string, object?>
            {
                ["urls"] = new[] { liUrl },
                ["startUrls"] = new[] { new { url = liUrl } },
                ["searchUrl"] = liUrl,
                ["title"] = q, ["keyword"] = q, ["searchQuery"] = q, ["queries"] = new[] { q },
                // count >= 10: some actors (curious_coder/linkedin-jobs-scraper) reject smaller counts.
                ["location"] = location, ["maxItems"] = max, ["rows"] = max, ["count"] = Math.Max(max, 10), ["scrapeCompany"] = false,
            };

            log?.Report("A obter vagas do LinkedIn via Apify (consome créditos)…");
            using var content = new StringContent(JsonSerializer.Serialize(input), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Surface the actual reason (Apify returns {"error":{"message":...}}) — e.g. a pay-per-event actor
                // that the free plan can't run, an exceeded usage limit, or a bad token/actor.
                string reason = "";
                try { reason = ApifyError(await resp.Content.ReadAsStringAsync(ct)); } catch { }
                string msg = $"Apify falhou (HTTP {(int)resp.StatusCode})"
                           + (reason.Length > 0 ? " — " + reason : " — verifica token/actor/plano (actores pagos não correm no plano free).");
                log?.Report(msg);
                Diag.Warn("apify run failed | " + msg);
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
                    FrontLoad(it, Str(it, "description", "descriptionText", "jobDescription")),
                    "linkedin",
                    Str(it, "postedAt", "postedTime", "publishedAt", "listedAt")));
            }
            log?.Report($"Apify: {jobs.Count} vagas do LinkedIn.");
            Diag.Info($"apify: {jobs.Count} LinkedIn jobs (actor={cfg.ActorId})");
        }
        catch (Exception ex) { log?.Report("Apify erro: " + ex.Message); Diag.Warn("apify error | " + ex.Message); }
        return jobs;
    }

    /// <summary>Pulls the human message out of an Apify error body: {"error":{"type":..,"message":..}}.</summary>
    private static string ApifyError(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? "";
        }
        catch { /* not JSON */ }
        return "";
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

    /// <summary>Monthly usage vs limit, e.g. "$1.20 / $5.00" (free call). Null if unknown/unreachable.</summary>
    public static async Task<string?> GetUsageAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            using var r = await Http.GetAsync($"https://api.apify.com/v2/users/me/limits?token={Uri.EscapeDataString(token.Trim())}", ct);
            if (!r.IsSuccessStatusCode) return $"HTTP {(int)r.StatusCode}";
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            if (!d.RootElement.TryGetProperty("data", out var data)) return null;
            double used = Dbl(data, "current", "monthlyUsageUsd");
            double max = Dbl(data, "limits", "maxMonthlyUsageUsd");
            if (max > 0) return $"${used:0.00} / ${max:0.00}";
            return used > 0 ? $"${used:0.00}" : null;
        }
        catch { return null; }
    }

    /// <summary>Prepends structured LinkedIn fields (skills, seniority, employment type, function) to the
    /// free-text description, so the gate and scorer see them even when the body omits them.</summary>
    private static string FrontLoad(JsonElement it, string description)
    {
        var bits = new List<string>();
        void Add(string label, params string[] keys) { string v = Str(it, keys); if (v.Length > 0) bits.Add($"{label}: {v}"); }
        Add("Skills", "skills", "skillsDescription");
        Add("Seniority", "seniorityLevel", "seniority", "experienceLevel");
        Add("Employment", "employmentType", "contractType");
        Add("Function", "jobFunction", "function");
        return bits.Count == 0 ? description : string.Join(" · ", bits) + "\n\n" + description;
    }

    private static double Dbl(JsonElement e, string obj, string key)
        => e.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

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
