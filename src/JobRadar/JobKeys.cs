using System.Text.RegularExpressions;

namespace JobRadar;

/// <summary>
/// How two job rows are recognised as the same posting. The dedupe key used to be the raw URL, but job boards
/// append per-visit tracking to it — LinkedIn serves one job as ".../view/backend-developer-at-x-4302579973
/// ?position=39&amp;refId=…" with a different position/refId on every search — so the same job was re-inserted on
/// each run (one Wolters Kluwer role showed up six times). Reposts under a new id, same title/company/city, were
/// the other half of the noise.
/// </summary>
public static class JobKeys
{
    /// <summary>Query parameters that only track the visit, never identify the job.</summary>
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "refid", "trk", "trackingid", "position", "pagenum", "src", "source", "lipi",
        "gclid", "fbclid", "mc_cid", "mc_eid", "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
    };

    /// <summary>
    /// Stable identity of a posting URL: lower-case host without "www."/country subdomain for LinkedIn, no
    /// fragment, no trailing slash, tracking parameters removed (identifying ones like Greenhouse's gh_jid kept).
    /// LinkedIn job pages reduce to "linkedin.com/jobs/view/&lt;id&gt;" — the slug and every query param are noise.
    /// </summary>
    public static string CanonicalUrl(string? url)
    {
        string u = (url ?? "").Trim();
        if (u.Length == 0) return "";
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return u.ToLowerInvariant();

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        string path = uri.AbsolutePath.TrimEnd('/');

        if (host == "linkedin.com" || host.EndsWith(".linkedin.com"))
        {
            var id = Regex.Match(path, @"/jobs/view/(?:[^/]*?-)?(\d{6,})$");
            return id.Success ? $"linkedin.com/jobs/view/{id.Groups[1].Value}" : $"linkedin.com{path}".ToLowerInvariant();
        }

        var kept = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !TrackingParams.Contains(p.Split('=')[0]) && !p.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        string query = kept.Count > 0 ? "?" + string.Join("&", kept) : "";
        return $"{host}{path}{query}".ToLowerInvariant();
    }

    /// <summary>Key used to store a new job: its canonical URL, or "title|company" when it has none.</summary>
    public static string StorageKey(string? url, string? title, string? company)
    {
        string c = CanonicalUrl(url);
        return c.Length > 0 ? c : $"{title}|{company}".Trim().ToLowerInvariant();
    }

    /// <summary>
    /// "Same posting" key across ids: title + company + city. Different cities stay separate on purpose
    /// (a Porto and a Braga opening are two jobs); "Porto, Porto, Portugal" and "Porto, Portugal (Híbrido)"
    /// are the same city.
    /// </summary>
    public static string FuzzyKey(string? title, string? company, string? location)
        => $"{Norm(title)}|{Norm(company)}|{City(location)}";

    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"\s+", " ").Trim();

    private static string City(string? location)
    {
        string first = (location ?? "").Split(',', '(', '·', '|', '/')[0];
        first = Regex.Replace(first.ToLowerInvariant(), @"\b(remote|remoto|hybrid|híbrido|on-?site|presencial)\b", " ");
        return Norm(first);
    }

    /// <summary>
    /// Picks, among rows that are the same posting, the one to keep: the one the user acted on (status), then the
    /// AI-scored one with the highest score, then the richer description, then the newest. Returns the rows to delete.
    /// </summary>
    public static List<JobEntity> Duplicates(IEnumerable<JobEntity> jobs)
    {
        var drop = new List<JobEntity>();
        foreach (var g in jobs.GroupBy(j => FuzzyKey(j.Title, j.Company, j.Location)))
        {
            if (g.Count() < 2) continue;
            var keep = g.OrderByDescending(j => !string.IsNullOrEmpty(j.Status) && j.Status != "new")
                        .ThenByDescending(j => j.AiScore.HasValue)
                        .ThenByDescending(j => j.AiScore ?? -1)            // keep the best evaluation of the posting
                        .ThenByDescending(j => j.Description?.Length ?? 0)
                        .ThenByDescending(j => j.PostedAt, StringComparer.Ordinal)
                        .First();
            drop.AddRange(g.Where(j => !ReferenceEquals(j, keep)));
        }
        return drop;
    }
}
