using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JobRadar;

/// <summary>
/// Researches an employer: a couple of key-free web searches (reviews + comparable salaries) → the snippets
/// are handed to the configured LLM, which returns a STRUCTURED briefing (pros/cons/salary + a salary
/// expectation tailored to the candidate). Works with a local model too (it only summarises the fetched
/// text). Returns null if nothing could be gathered.
/// </summary>
public static class CompanyResearch
{
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    public static async Task<(CompanyBrief? brief, string? error)> ResearchAsync(
        ClaudeConfig llm, UserProfile profile, string company, string role, string? location, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(company)) return (null, null);

        // 1) Gather context from the web (key-free, best-effort). One combined query — a second
        // back-to-back query gets throttled by the search engine and just adds ~10s for nothing.
        var raw = await WebSearch.SearchAsync($"{company} employee reviews salary", 8, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = raw.Where(r => !string.IsNullOrWhiteSpace(r.Url) && seen.Add(r.Url)).Take(8).ToList();
        if (results.Count == 0) return (null, Loc.Instance.T("research.noWeb"));

        var snippets = new StringBuilder();
        for (int i = 0; i < results.Count; i++)
            snippets.AppendLine($"[{i + 1}] {results[i].Title}\n{results[i].Snippet}\n{results[i].Url}\n");

        int floor = profile.SalaryFloorEur, target = profile.SalaryTargetEur;
        string cand = $"{profile.YearsExperience} years experience, target level {profile.SeniorityTarget}" +
                      (floor > 0 || target > 0 ? $", own salary floor €{floor:N0} / target €{target:N0}/yr" : "");

        // 2) Ask the model for ONE JSON object — using only the snippets, with a tailored salary expectation.
        string prompt =
$@"From the web search snippets below, build a SHORT, honest employer briefing for THIS candidate.
Use ONLY the snippets (don't invent). Reply with ONLY one valid JSON object, double-quoted keys/values, shape:
{{""pros"":[""short phrase""],""cons"":[""short phrase""],""context"":[""neutral fact""],""reputationNote"":""one sentence or empty"",""salaryFound"":""comparable figures with currency, or 'desconhecido'"",""salaryExpectation"":""a €/yr range to target at THIS company"",""salaryRationale"":""1 sentence: how you derived it"",""bottomLine"":""one sentence""}}
- pros/cons: concrete, from reviews; cite sources inline like [2]. Keep each under ~12 words.
- salaryExpectation: a realistic €/year range for THIS candidate's level, reasoning from the found figures
  and their experience/seniority/target. If figures are thin, widen the range and say so in salaryRationale.
- Be honest when data is thin (empty arrays are fine).
- Write the text in {Loc.Instance.T("ai.lang")}.

== CANDIDATE ==
Role: {role} · {cand} · location {location ?? "—"}

== SEARCH RESULTS ==
{snippets}";

        string? text = await LlmClient.CompleteAsync(llm, prompt, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            string msg = Loc.Instance.T("research.noModel");
            if (!string.IsNullOrWhiteSpace(LlmClient.LastError)) msg += ": " + LlmClient.LastError;
            return (null, msg);
        }

        var brief = Parse(text) ?? new CompanyBrief { RawFallback = text!.Trim() };

        // Attach sources from the search results (not from the model).
        for (int i = 0; i < results.Count; i++)
            brief.Sources.Add(new SourceRef { N = i + 1, Url = results[i].Url, Host = Host(results[i].Url) });
        return (brief, null);
    }

    private static CompanyBrief? Parse(string raw)
    {
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        string block = raw.Substring(a, b - a + 1);
        var dto = TryDto(block) ?? TryDto(LoosenJson(block));
        if (dto is null) return null;
        return new CompanyBrief
        {
            Pros = dto.Pros ?? new(),
            Cons = dto.Cons ?? new(),
            Context = dto.Context ?? new(),
            ReputationNote = dto.ReputationNote ?? "",
            SalaryFound = dto.SalaryFound ?? "",
            SalaryExpectation = dto.SalaryExpectation ?? "",
            SalaryRationale = dto.SalaryRationale ?? "",
            BottomLine = dto.BottomLine ?? "",
        };
    }

    private static BriefDto? TryDto(string json)
    {
        try { return JsonSerializer.Deserialize<BriefDto>(json, J); }
        catch { return null; }
    }

    /// <summary>Quote bare object keys (e.g. {pros: …} -> {"pros": …}) for lenient parsing.</summary>
    private static string LoosenJson(string s)
        => Regex.Replace(s, @"([{,]\s*)([A-Za-z_][A-Za-z0-9_]*)(\s*:)", "$1\"$2\"$3");

    private sealed class BriefDto
    {
        [JsonConverter(typeof(StringListConverter))] public List<string>? Pros { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? Cons { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? Context { get; set; }
        public string? ReputationNote { get; set; }
        public string? SalaryFound { get; set; }
        public string? SalaryExpectation { get; set; }
        public string? SalaryRationale { get; set; }
        public string? BottomLine { get; set; }
    }

    private static string Host(string url)
    {
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch { return url; }
    }
}
