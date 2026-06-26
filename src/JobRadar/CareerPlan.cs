using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JobRadar;

/// <summary>
/// Builds a personalised career-growth plan through MULTI-STEP web research:
///   1) seed searches around the candidate's field, role and salary;
///   2) the model picks 2–3 areas worth digging into (deep search);
///   3) those targeted searches run and feed back in;
///   4) the model synthesises everything — plus the jobs the app already saw — into a
///      STRUCTURED plan (strengths, skill gaps, target roles, salary trajectory, next steps).
/// Key-free (DuckDuckGo) and BYOK: works with the Claude CLI or a local OpenAI-compatible model.
/// Returns null only if nothing at all could be gathered.
/// </summary>
public static class CareerPlan
{
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    public static async Task<CareerPlanResult?> GenerateAsync(
        ClaudeConfig llm, UserProfile profile, string marketContext,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Report(m);

        string field = !string.IsNullOrWhiteSpace(profile.Field) ? profile.Field : "software engineering";
        string topRole = profile.JobTitles.FirstOrDefault() ?? field;
        string loc = profile.Locations.FirstOrDefault() ?? "";
        int year = DateTime.UtcNow.Year;

        var results = new List<WebResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Merge(IEnumerable<WebResult> rs)
        {
            foreach (var r in rs)
                if (!string.IsNullOrWhiteSpace(r.Url) && seen.Add(r.Url)) results.Add(r);
        }

        // 1) Seed searches.
        L("A mapear o mercado para o teu perfil…");
        Merge(await WebSearch.SearchAsync($"{field} most in-demand skills {year}", 5, ct));
        Merge(await WebSearch.SearchAsync($"{topRole} salary {loc} {year}", 5, ct));
        Merge(await WebSearch.SearchAsync($"{field} career path from {profile.SeniorityTarget} next level", 5, ct));

        // 2) Let the model pick deeper angles (best-effort; skip on failure).
        L("A identificar áreas a aprofundar…");
        foreach (var q in await PickFollowUpsAsync(llm, profile, field, topRole, results, ct))
        {
            ct.ThrowIfCancellationRequested();
            L($"A pesquisar: {q}");
            Merge(await WebSearch.SearchAsync(q, 4, ct));
        }

        results = results.Take(14).ToList();
        if (results.Count == 0) return null;

        // 3) Synthesise the plan.
        L("A sintetizar o teu plano de carreira…");
        string snippets = Snippets(results);
        string prompt =
$@"You are a candid career coach. From the candidate, the jobs the app already found, and the web
research snippets below, build a SHORT, honest growth plan. Use ONLY the snippets for market claims
(don't invent figures); cite sources inline like [3]. Reply with ONLY one valid JSON object,
double-quoted keys/values, shape:
{{""headline"":""one-line positioning for this candidate"",""strengths"":[""short phrase""],""skillGaps"":[{{""skill"":""name"",""why"":""why it matters"",""action"":""one concrete way to build it""}}],""targetRoles"":[""role title""],""salaryNow"":""realistic €/yr band today"",""salaryPotential"":""€/yr band reachable in 12–24 months with this plan"",""salaryRationale"":""1 sentence: how you derived the bands"",""steps"":[{{""horizon"":""0–3 meses"",""title"":""short action"",""detail"":""one sentence""}}],""marketSignals"":[""in-demand skill or hiring trend, cite [n]""],""bottomLine"":""one encouraging, honest sentence""}}
- 3–5 strengths, 2–4 skillGaps, 2–4 targetRoles, 3–5 steps ordered by horizon (0–3 / 3–6 / 6–12 meses).
- Salary bands in EUR/year, reasoned from the found figures + the candidate's level; widen and say so if data is thin.
- Keep every phrase concrete and under ~16 words. Be honest when evidence is thin (empty arrays are fine).
- Write the text in European Portuguese.

== CANDIDATE ==
{profile.ToScoringText()}
== JOBS THE APP ALREADY SCORED ==
{(string.IsNullOrWhiteSpace(marketContext) ? "(none yet)" : marketContext)}
== WEB RESEARCH ==
{snippets}";

        string? text = await LlmClient.CompleteAsync(llm, prompt, ct);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var plan = Parse(text) ?? new CareerPlanResult { RawFallback = text!.Trim() };
        for (int i = 0; i < results.Count; i++)
            plan.Sources.Add(new SourceRef { N = i + 1, Url = results[i].Url, Host = Host(results[i].Url) });
        return plan;
    }

    /// <summary>Ask the model for 2–3 targeted follow-up queries based on the seed snippets.</summary>
    private static async Task<List<string>> PickFollowUpsAsync(
        ClaudeConfig llm, UserProfile profile, string field, string topRole, List<WebResult> seed, CancellationToken ct)
    {
        try
        {
            string prompt =
$@"A candidate is a {profile.SeniorityTarget}-level {topRole} in {field}. Based on the snippets below,
suggest 2–3 web search queries that would best inform a career-growth plan (skills to gain, salary
trajectory, hiring trends). Reply with ONLY a JSON object: {{""queries"":[""...""]}}. Queries in English.

{Snippets(seed.Take(6).ToList())}";
            string? r = await LlmClient.CompleteAsync(llm, prompt, ct);
            if (string.IsNullOrWhiteSpace(r)) return new();
            int a = r.IndexOf('{'), b = r.LastIndexOf('}');
            if (a < 0 || b <= a) return new();
            string block = r.Substring(a, b - a + 1);
            var dto = TryGet<FollowUps>(block) ?? TryGet<FollowUps>(LoosenJson(block));
            return (dto?.Queries ?? new())
                .Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Distinct().Take(3).ToList();
        }
        catch { return new(); }
    }

    private static string Snippets(List<WebResult> rs)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < rs.Count; i++)
            sb.AppendLine($"[{i + 1}] {rs[i].Title}\n{rs[i].Snippet}\n{rs[i].Url}\n");
        return sb.ToString();
    }

    private static CareerPlanResult? Parse(string raw)
    {
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        string block = raw.Substring(a, b - a + 1);
        var dto = TryGet<PlanDto>(block) ?? TryGet<PlanDto>(LoosenJson(block));
        if (dto is null) return null;
        return new CareerPlanResult
        {
            Headline = dto.Headline ?? "",
            Strengths = dto.Strengths ?? new(),
            SkillGaps = (dto.SkillGaps ?? new()).Select(g => new SkillGap
            {
                Skill = g.Skill ?? "", Why = g.Why ?? "", Action = g.Action ?? ""
            }).Where(g => !string.IsNullOrWhiteSpace(g.Skill)).ToList(),
            TargetRoles = dto.TargetRoles ?? new(),
            SalaryNow = dto.SalaryNow ?? "",
            SalaryPotential = dto.SalaryPotential ?? "",
            SalaryRationale = dto.SalaryRationale ?? "",
            Steps = (dto.Steps ?? new()).Select(s => new PlanStep
            {
                Horizon = s.Horizon ?? "", Title = s.Title ?? "", Detail = s.Detail ?? ""
            }).Where(s => !string.IsNullOrWhiteSpace(s.Title)).ToList(),
            MarketSignals = dto.MarketSignals ?? new(),
            BottomLine = dto.BottomLine ?? "",
        };
    }

    private static T? TryGet<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, J); }
        catch { return null; }
    }

    /// <summary>Quote bare object keys (e.g. {skill: …} -> {"skill": …}) for lenient parsing.</summary>
    private static string LoosenJson(string s)
        => Regex.Replace(s, @"([{,]\s*)([A-Za-z_][A-Za-z0-9_]*)(\s*:)", "$1\"$2\"$3");

    private static string Host(string url)
    {
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch { return url; }
    }

    private sealed class FollowUps { public List<string>? Queries { get; set; } }

    private sealed class PlanDto
    {
        public string? Headline { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? Strengths { get; set; }
        public List<GapDto>? SkillGaps { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? TargetRoles { get; set; }
        public string? SalaryNow { get; set; }
        public string? SalaryPotential { get; set; }
        public string? SalaryRationale { get; set; }
        public List<StepDto>? Steps { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? MarketSignals { get; set; }
        public string? BottomLine { get; set; }
    }
    private sealed class GapDto { public string? Skill { get; set; } public string? Why { get; set; } public string? Action { get; set; } }
    private sealed class StepDto { public string? Horizon { get; set; } public string? Title { get; set; } public string? Detail { get; set; } }
}
