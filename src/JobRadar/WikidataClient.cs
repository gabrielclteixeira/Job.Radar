using System.Text.Json;

namespace JobRadar;

/// <summary>Authoritative, key-free firmographics from Wikidata (100M+ entities, no auth). Resolves a company
/// name to an entity, then one SPARQL query returns founded year, HQ, country, industry, employee count and
/// CEO — with English labels. Best-effort: returns null on no match / any error. Used to ground the
/// Company Researcher's "About" facts instead of guessing them from search snippets.</summary>
public static class WikidataClient
{
    // Wikimedia asks for a descriptive User-Agent; requests without one are throttled/blocked.
    private static readonly HttpClient Http = MakeClient();
    private static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "JobRadar/0.6 (employer research; contact via github)");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return h;
    }

    public record WikidataFacts(
        string Founded, string Headquarters, string Country, string Industry,
        string Employees, string Ceo, string Website)
    {
        public bool Any => new[] { Founded, Headquarters, Country, Industry, Employees, Ceo, Website }
            .Any(s => !string.IsNullOrWhiteSpace(s));
    }

    public static async Task<WikidataFacts?> LookupAsync(string company, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(company)) return null;
        try
        {
            string? qid = await FindEntityAsync(company, ct);
            if (qid is null) return null;
            return await FactsAsync(qid, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diag.Warn("wikidata lookup failed | " + ex.Message); return null; }
    }

    /// <summary>Top entity match for the name, accepted only if it looks like an organisation.</summary>
    private static async Task<string?> FindEntityAsync(string company, CancellationToken ct)
    {
        string url = "https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&type=item&language=en&limit=3"
                   + "&search=" + Uri.EscapeDataString(company);
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("search", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var it in arr.EnumerateArray())
        {
            string id = Str(it, "id");
            string desc = Str(it, "description").ToLowerInvariant();
            // Skip obvious non-companies (people, places, software) — keep org-ish or unknown-but-first.
            if (desc.Length == 0 || OrgWords.Any(desc.Contains)) return id;
        }
        // Fall back to the very first hit only if its description doesn't scream "not a company".
        var first = arr.EnumerateArray().FirstOrDefault();
        string fd = Str(first, "description").ToLowerInvariant();
        return NonOrgWords.Any(fd.Contains) ? null : Str(first, "id") is { Length: > 0 } fid ? fid : null;
    }

    private static readonly string[] OrgWords =
        { "company", "business", "enterprise", "corporation", "firm", "organisation", "organization",
          "provider", "agency", "manufacturer", "bank", "startup", "consultanc", "group", "holding", "gmbh", "ltd", "inc" };
    private static readonly string[] NonOrgWords =
        { "person", "actor", "singer", "politician", "footballer", "village", "town", "river", "film", "album", "song", "genus", "species" };

    private static async Task<WikidataFacts?> FactsAsync(string qid, CancellationToken ct)
    {
        string sparql =
$@"SELECT ?foundedYear ?hqLabel ?countryLabel ?industryLabel ?employees ?ceoLabel ?website WHERE {{
  OPTIONAL {{ wd:{qid} wdt:P571 ?inception. BIND(YEAR(?inception) AS ?foundedYear) }}
  OPTIONAL {{ wd:{qid} wdt:P159 ?hq. }}
  OPTIONAL {{ wd:{qid} wdt:P17 ?country. }}
  OPTIONAL {{ wd:{qid} wdt:P452 ?industry. }}
  OPTIONAL {{ wd:{qid} wdt:P1128 ?employees. }}
  OPTIONAL {{ wd:{qid} wdt:P169 ?ceo. }}
  OPTIONAL {{ wd:{qid} wdt:P856 ?website. }}
  SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". ?hq rdfs:label ?hqLabel. ?country rdfs:label ?countryLabel. ?industry rdfs:label ?industryLabel. ?ceo rdfs:label ?ceoLabel. }}
}} LIMIT 1";
        string url = "https://query.wikidata.org/sparql?format=json&query=" + Uri.EscapeDataString(sparql);
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("results", out var res)
            || !res.TryGetProperty("bindings", out var b) || b.ValueKind != JsonValueKind.Array
            || b.GetArrayLength() == 0) return null;
        var row = b[0];
        string V(string key) => row.TryGetProperty(key, out var v) && v.TryGetProperty("value", out var val)
            ? val.GetString() ?? "" : "";
        var facts = new WikidataFacts(
            Founded: V("foundedYear"), Headquarters: V("hqLabel"), Country: V("countryLabel"),
            Industry: V("industryLabel"), Employees: V("employees"), Ceo: V("ceoLabel"), Website: V("website"));
        return facts.Any ? facts : null;
    }

    private static string Str(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
