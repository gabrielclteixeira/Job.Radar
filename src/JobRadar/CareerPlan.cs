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

    public static async Task<(CareerPlanResult? plan, string? error)> GenerateAsync(
        ClaudeConfig llm, UserProfile profile, string marketContext,
        string? cachePath = null, IProgress<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Report(m);

        string field = !string.IsNullOrWhiteSpace(profile.Field) ? profile.Field : "software engineering";
        string topRole = profile.JobTitles.FirstOrDefault() ?? field;
        string loc = profile.Locations.FirstOrDefault() ?? "";
        int year = DateTime.UtcNow.Year;
        string sig = ProfileSig(profile, field, topRole);

        // Reuse recently-gathered research (short TTL) so a crash + retry doesn't redo all the
        // searches — and doesn't re-hit a throttled search engine.
        var results = LoadResearch(cachePath, sig);
        if (results.Count > 0)
        {
            L(Loc.Instance.T("plan.reuseResearch"));
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Merge(IEnumerable<WebResult> rs)
            {
                foreach (var r in rs)
                    if (!string.IsNullOrWhiteSpace(r.Url) && seen.Add(r.Url)) results.Add(r);
            }

            // 1) Seed searches — run concurrently (wall-clock = slowest one, not the sum).
            L(Loc.Instance.T("plan.mapping"));
            foreach (var s in await Task.WhenAll(
                WebSearch.SearchAsync($"{field} most in-demand skills {year}", 5, ct),
                WebSearch.SearchAsync($"{topRole} salary {loc} {year}", 5, ct),
                WebSearch.SearchAsync($"{field} career path from {profile.SeniorityTarget} next level", 5, ct)))
                Merge(s);

            // 2) Let the model pick deeper angles (best-effort), then run those searches concurrently too.
            L(Loc.Instance.T("plan.deepening"));
            var followUps = await PickFollowUpsAsync(llm, profile, field, topRole, results, ct);
            foreach (var q in followUps) L(Loc.Instance.F("plan.searching", q));
            if (followUps.Count > 0)
                foreach (var s in await Task.WhenAll(followUps.Select(q => WebSearch.SearchAsync(q, 4, ct))))
                    Merge(s);

            results = results.Take(14).ToList();
            // Persist BEFORE synthesis so a model crash on the next step doesn't lose the research.
            SaveResearch(cachePath, sig, results);
        }

        // 3) Synthesise the plan. If search was fully throttled (no snippets), still build a plan from
        // the profile + the already-scored jobs rather than failing outright.
        L(Loc.Instance.T("plan.synth"));
        string snippets = results.Count > 0
            ? Snippets(results.Take(10).ToList())
            : "(no web results this time — use your general knowledge, lean on the candidate profile and the scored jobs below, and be explicitly cautious about any figures)";

        // Anchors that keep weaker models honest: their own salary range, local market, and existing stack.
        int floor = profile.SalaryFloorEur, target = profile.SalaryTargetEur;
        string locName = string.IsNullOrWhiteSpace(loc) ? "the candidate's country" : loc;
        string anchor = (floor > 0 || target > 0)
            ? $"the candidate's own floor €{floor:N0} / target €{target:N0} per year"
            : "the candidate's stated level and local market norms";
        string core = profile.CoreSkills.Count > 0 ? string.Join(", ", profile.CoreSkills.Take(6)) : "their current stack";

        // Hand the model a concrete local-market ceiling to reason against (a vague "be conservative"
        // doesn't land on weaker models). It's an input to the prompt only — never a post-hoc override.
        int ceiling = SalaryCeiling(profile);
        string targetText = target > 0 ? $"€{target:N0}/yr" : "the local senior norm for this field";
        string ceilingLine = ceiling > 0
            ? $"HARD CAP: salaryPotential's upper bound must be €{ceiling:N0}/yr or LESS, and salaryNow's upper bound below that. A higher figure is WRONG unless a cited LOCAL source proves it — there is almost never such a source. {targetText} is the realistic centre."
            : $"Keep the 12–24 month \"potential\" near {targetText} — conservative local figures, never aspirational US/remote ones.";
        string reminderLine =
            $"Plans here almost always INFLATE salary — the real local band is LOWER than you think. You rarely have a cited " +
            $"local figure, so anchor salaryNow and salaryPotential to {anchor}, as LOCAL {locName} EUR" +
            (ceiling > 0 ? $", potential upper bound ≤ €{ceiling:N0}/yr. " : ". ") +
            $"Ignore €100k+ remote listings (they carry crypto/US-only/contractor caveats). Build on the candidate's {core} stack; don't switch primary language.";

        string prompt =
$@"You are a candid career coach. From the candidate, the jobs the app already found, and the web
research snippets below, build a SHORT, honest growth plan. Use ONLY the snippets for market claims
(don't invent figures); cite sources inline like [3]. Reply with ONLY one valid JSON object,
double-quoted keys/values, shape:
{{""headline"":""one-line positioning for this candidate"",""strengths"":[""short phrase""],""skillGaps"":[{{""skill"":""name"",""why"":""why it matters"",""action"":""one concrete way to build it""}}],""targetRoles"":[""role title""],""salaryRationale"":""1 sentence: state the local {locName} EUR range you assume, then how you derived the bands"",""salaryNow"":""realistic LOCAL €/yr band today"",""salaryPotential"":""LOCAL €/yr band reachable in 12–24 months"",""steps"":[{{""horizon"":""0–3 meses"",""title"":""short action"",""detail"":""one sentence""}}],""marketSignals"":[""in-demand skill or hiring trend, cite [n]""],""bottomLine"":""one encouraging, honest sentence""}}
- 3–5 strengths, 2–4 skillGaps, 2–4 targetRoles, 3–5 steps ordered by horizon (0–3 / 3–6 / 6–12 meses).
- Keep every phrase concrete and under ~16 words. Be honest when evidence is thin (empty arrays are fine).
- Write the text in {Loc.Instance.T("ai.lang")}.

== HARD RULES — READ BEFORE WRITING ANY NUMBER (most models inflate salary here) ==
1. The candidate works in {locName} and is paid in EUR. ALL salary figures must be LOCAL {locName} EUR/year.
2. The snippets rarely contain a real local figure. When you DON'T have a cited local number (you usually don't),
   you MUST anchor to {anchor} — NOT to your general expectations, US/global pay, or remote listings. Do not
   invent an aspirational figure; a cautious range anchored to the candidate's own floor/target is the correct answer.
3. Remote/global listings advertising €100k+ almost always carry heavy caveats — paid in crypto, US-only,
   contractor with no benefits/PTO, or staff/principal level. They are NOT the local {locName} rate; never anchor to them.
4. {ceilingLine}
5. Write salaryRationale FIRST (name the local range you're assuming and whether you actually found local data),
   THEN salaryNow and salaryPotential consistent with it.
6. BUILD ON THE CANDIDATE'S CORE STACK ({core}) in skillGaps and steps. Deepen and specialise within it plus
   adjacent in-demand areas. Do NOT make ""learn Python"" (or switching primary language) a step; if AI/data work
   is relevant, express it as delivered THROUGH their stack (e.g. LLM/MCP services in .NET or Go).

== CANDIDATE ==
{profile.ToScoringText()}
== JOBS THE APP ALREADY SCORED ==
{(string.IsNullOrWhiteSpace(marketContext) ? "(none yet)" : marketContext)}
== WEB RESEARCH (reference only — skews US/global, do not copy figures) ==
{snippets}
== FINAL REMINDER (this overrides anything inflated above) ==
{reminderLine}";

        string? text = await LlmClient.CompleteAsync(llm, prompt, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            string msg = Loc.Instance.T("plan.noModel");
            if (!string.IsNullOrWhiteSpace(LlmClient.LastError)) msg += ": " + LlmClient.LastError;
            return (null, msg);
        }

        var plan = Parse(text) ?? new CareerPlanResult { RawFallback = text!.Trim() };
        for (int i = 0; i < results.Count; i++)
            plan.Sources.Add(new SourceRef { N = i + 1, Url = results[i].Url, Host = Host(results[i].Url) });
        return (plan, null);
    }

    /// <summary>
    /// Adversarial self-critique. Plays an independent reviewer that is told a RIVAL tool produced the plan
    /// (models critique "someone else's" work far more honestly than their own), so it surfaces inflated
    /// salaries, over-optimism and self-contradictions instead of rubber-stamping. Depending on <paramref
    /// name="mode"/> it can also let the author rebut, and rewrite the plan from the valid objections.
    /// Best-effort: a failed/empty critique leaves the plan untouched (the section just stays hidden).
    /// Returns the same plan instance (critique attached) or, in <see cref="CritiqueMode.Revise"/>, a new
    /// revised instance.
    /// </summary>
    public static async Task<CareerPlanResult> CritiqueAsync(
        ClaudeConfig llm, UserProfile profile, CareerPlanResult plan, CritiqueMode mode,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Report(m);
        string loc = profile.Locations.FirstOrDefault() ?? "the candidate's market";
        string core = profile.CoreSkills.Count > 0 ? string.Join(", ", profile.CoreSkills.Take(6)) : "their current stack";
        string lang = Loc.Instance.T("ai.lang");
        string summary = PlanSummary(plan);
        int ceiling = SalaryCeiling(profile);
        string ceilingNote = ceiling > 0
            ? $"For reference, a realistic local 12–24 month ceiling for this candidate is about €{ceiling:N0}/yr (anchored to their own target); bands above that are almost certainly inflated."
            : "Local senior pay in this market is well below US/global figures.";
        // State the REAL profile figures — weaker models fabricate a 'target' and back-justify inflation.
        string anchor = (profile.SalaryFloorEur > 0 || profile.SalaryTargetEur > 0)
            ? $"the candidate's stated floor €{profile.SalaryFloorEur:N0} and target €{profile.SalaryTargetEur:N0}/yr — these are the ONLY salary anchors in the profile; do NOT claim any other 'target'"
            : "the candidate's stated level and local market norms";

        // Always temper trust, even if the model call below fails.
        plan.CritiqueCaveat = Loc.Instance.T("plan.caveat");

        try
        {
            // 1) ATTACKER — framed as reviewing a competitor's output.
            L(Loc.Instance.T("plan.critiquing"));
            string attackPrompt =
$@"A rival career-planning tool produced the plan below for a candidate based in {loc}. You are a rigorous,
independent senior reviewer with NO stake in it — your job is to expose where it is WRONG, overstated or
generic so the candidate doesn't trust it blindly. Scrutinise especially:
- SALARY INFLATION — in this market the usual error is salaries set TOO HIGH (especially the 12–24 month
  ""potential""). Flag bands that exceed realistic local {loc} pay or contradict the plan's own rationale.
  {ceilingNote} Do NOT argue salaries should be higher unless a cited local source proves it.
- assuming remote/global roles pay US rates (they don't; €100k+ remote listings carry crypto/US-only/contractor caveats);
- advice that ignores the candidate's actual stack ({core}) or is vague boilerplate.
Reply with ONLY one JSON object: {{""critique"":[{{""claim"":""the plan's claim, short"",""issue"":""the specific problem, blunt""}}]}}.
3–5 items, most serious first; each phrase under ~22 words. Write in {lang}.

== CANDIDATE ==
{profile.ToScoringText()}
== THE PLAN UNDER REVIEW ==
{summary}";
            string? aText = await LlmClient.CompleteAsync(llm, attackPrompt, ct);
            var points = ParseCritique(aText);
            if (points.Count == 0) return plan;   // nothing usable — leave the plan as-is
            plan.Critique = points;

            // 2) DEFENDER — rebut each objection (debate / revise modes).
            if (mode is CritiqueMode.Debate or CritiqueMode.Revise)
            {
                L(Loc.Instance.T("plan.debating"));
                string objections = string.Join("\n", points.Select((p, i) => $"{i + 1}. {p.Claim} — {p.Issue}"));
                string defendPrompt =
$@"You are the author of the plan below, responding to a reviewer's objections. For EACH objection, reply in
ONE honest sentence: either concede (state what you would change) or justify it. Do not be defensive for its
own sake. Reply with ONLY one JSON object: {{""responses"":[{{""claim"":""echo the objection's claim"",""rebuttal"":""your one-sentence response""}}]}}.
Keep the same order as the objections. Write in {lang}.

== THE PLAN ==
{summary}
== OBJECTIONS ==
{objections}";
                string? dText = await LlmClient.CompleteAsync(llm, defendPrompt, ct);
                ApplyRebuttals(points, dText);
            }

            // 3) REVISER — collect the data the critique said was missing, then rewrite from it.
            if (mode == CritiqueMode.Revise)
            {
                // 3a) Let the critique drive a fresh, targeted search (esp. for local salary figures) so the
                // reviser grounds in real data instead of falling back to a hallucinated 'target'.
                L(Loc.Instance.T("plan.researching"));
                var fresh = await GapFillResearchAsync(llm, loc, core, points, ct);
                string freshBlock = fresh.Count > 0
                    ? Snippets(fresh)
                    : "(no additional local data found — stay cautious and anchor to the candidate's floor/target; do not invent figures)";

                L(Loc.Instance.T("plan.revising"));
                string objections = string.Join("\n", points.Select((p, i) =>
                    $"{i + 1}. {p.Claim} — {p.Issue}" + (p.HasRebuttal ? $" (author: {p.Rebuttal})" : "")));
                string revisePrompt =
$@"Rewrite the career plan below to fix the VALID objections — especially any unrealistic salary. Use the FRESH
RESEARCH for concrete LOCAL figures; cite it inline like [n]. Keep bands LOCAL to {loc} and in EUR, anchored to
{anchor}. Salary corrections in {loc} are almost always DOWNWARD: if a band looks high, LOWER it toward local
reality; do NOT raise salaries unless the fresh research cites a local figure that supports it. {ceilingNote}
Keep what was already sound. Reply with ONLY one valid JSON object in EXACTLY this shape (same as the original plan):
{{""headline"":""..."",""strengths"":[""...""],""skillGaps"":[{{""skill"":""..."",""why"":""..."",""action"":""...""}}],""targetRoles"":[""...""],""salaryRationale"":""state whether you found a cited local figure, then the bands"",""salaryNow"":""local €/yr band"",""salaryPotential"":""local €/yr band"",""steps"":[{{""horizon"":""0–3 meses"",""title"":""..."",""detail"":""...""}}],""marketSignals"":[""...""],""bottomLine"":""...""}}
Write in {lang}.

== OBJECTIONS TO FIX ==
{objections}
== FRESH RESEARCH (newly fetched — use for local salary grounding) ==
{freshBlock}
== CURRENT PLAN ==
{summary}";
                string? rText = await LlmClient.CompleteAsync(llm, revisePrompt, ct);
                var revised = Parse(rText ?? "");
                if (revised is not null)
                {
                    // Carry the original sources, then append any fresh ones so inline [n] citations resolve.
                    revised.Sources = new List<SourceRef>(plan.Sources);
                    foreach (var w in fresh)
                        if (!revised.Sources.Any(s => string.Equals(s.Url, w.Url, StringComparison.OrdinalIgnoreCase)))
                            revised.Sources.Add(new SourceRef { N = revised.Sources.Count + 1, Url = w.Url, Host = Host(w.Url) });
                    revised.Critique = points;
                    revised.CritiqueCaveat = plan.CritiqueCaveat;
                    revised.Revised = true;
                    return revised;
                }
            }

            return plan;
        }
        catch
        {
            return plan;   // never let the critique break plan delivery
        }
    }

    /// <summary>Critique-driven gap-filling search: the model proposes targeted queries for the data the
    /// reviewer said was missing (especially concrete local salary figures), the app runs them, and the
    /// deduped results feed the reviser. Best-effort — returns empty on any failure.</summary>
    private static async Task<List<WebResult>> GapFillResearchAsync(
        ClaudeConfig llm, string loc, string core, List<CritiquePoint> points, CancellationToken ct)
    {
        try
        {
            string objections = string.Join("\n", points.Select((p, i) => $"{i + 1}. {p.Claim} — {p.Issue}"));
            string qPrompt =
$@"A reviewer raised the objections below about a career plan for a candidate in {loc} ({core}). Propose 1–3
precise web-search queries that would fetch the MISSING factual data — above all, concrete LOCAL salary figures
(e.g. ""{loc} senior .NET developer salary EUR""). Reply with ONLY {{""queries"":[""...""]}}. Queries in English.

== OBJECTIONS ==
{objections}";
            string? r = await LlmClient.CompleteAsync(llm, qPrompt, ct);
            if (string.IsNullOrWhiteSpace(r)) return new();
            int a = r.IndexOf('{'), b = r.LastIndexOf('}');
            if (a < 0 || b <= a) return new();
            string block = r.Substring(a, b - a + 1);
            var dto = TryGet<FollowUps>(block) ?? TryGet<FollowUps>(LoosenJson(block));
            var queries = (dto?.Queries ?? new())
                .Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Distinct().Take(3).ToList();
            if (queries.Count == 0) return new();

            var results = new List<WebResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in await Task.WhenAll(queries.Select(q => WebSearch.SearchAsync(q, 5, ct))))
                foreach (var w in s)
                    if (!string.IsNullOrWhiteSpace(w.Url) && seen.Add(w.Url)) results.Add(w);
            results = results.Take(8).ToList();

            // The snippets rarely carry the actual figure (it's in a table behind the link). Read the top
            // salary-relevant page(s) via Jina so the reviser sees real numbers, not a 240-char teaser.
            var toRead = results.OrderByDescending(r => LooksSalary(r.Url) || LooksSalary(r.Title)).Take(2).ToList();
            // Fetch enough to clear nav/boilerplate and reach the salary table (figures sit deep on data pages).
            var pages = await Task.WhenAll(toRead.Select(r => WebFetch.FetchMarkdownAsync(r.Url, 5000, ct)));
            for (int i = 0; i < toRead.Count; i++)
                if (!string.IsNullOrWhiteSpace(pages[i]))
                {
                    int idx = results.IndexOf(toRead[i]);
                    if (idx >= 0) results[idx] = toRead[i] with { Snippet = pages[i] };   // swap teaser → full page
                }
            return results;
        }
        catch { return new(); }
    }

    private static bool LooksSalary(string s)
        => !string.IsNullOrEmpty(s) && Regex.IsMatch(s, "salary|glassdoor|payscale|levels|talent|salaryexpert|wage|pay", RegexOptions.IgnoreCase);

    /// <summary>Compact, model-friendly rendering of the plan for the critic/reviser prompts.</summary>
    private static string PlanSummary(CareerPlanResult p)
    {
        if (p.HasFallback) return p.RawFallback;
        var sb = new StringBuilder();
        if (p.HasHeadline) sb.AppendLine($"Positioning: {p.Headline}");
        if (p.Strengths.Count > 0) sb.AppendLine("Strengths: " + string.Join("; ", p.Strengths));
        if (p.SkillGaps.Count > 0) sb.AppendLine("Gaps: " + string.Join("; ", p.SkillGaps.Select(g => $"{g.Skill} ({g.Why})")));
        if (p.TargetRoles.Count > 0) sb.AppendLine("Target roles: " + string.Join(", ", p.TargetRoles));
        if (p.HasSalaryNow) sb.AppendLine($"Salary now: {p.SalaryNow}");
        if (p.HasSalaryPotential) sb.AppendLine($"Salary potential (12–24mo): {p.SalaryPotential}");
        if (p.HasSalaryRationale) sb.AppendLine($"Salary rationale: {p.SalaryRationale}");
        if (p.Steps.Count > 0) sb.AppendLine("Steps: " + string.Join("; ", p.Steps.Select(s => $"[{s.Horizon}] {s.Title}")));
        if (p.MarketSignals.Count > 0) sb.AppendLine("Market signals: " + string.Join("; ", p.MarketSignals));
        if (p.HasBottomLine) sb.AppendLine($"Bottom line: {p.BottomLine}");
        return sb.ToString();
    }

    private static List<CritiquePoint> ParseCritique(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return new();
        string block = raw.Substring(a, b - a + 1);
        var dto = TryGet<CritiqueDto>(block) ?? TryGet<CritiqueDto>(LoosenJson(block));
        return (dto?.Critique ?? new())
            .Select(c => new CritiquePoint { Claim = c.Claim ?? "", Issue = c.Issue ?? "" })
            .Where(c => !string.IsNullOrWhiteSpace(c.Issue))
            .Take(5).ToList();
    }

    /// <summary>Maps the defender's responses back onto the objections (by order, then by claim match).</summary>
    private static void ApplyRebuttals(List<CritiquePoint> points, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return;
        string block = raw.Substring(a, b - a + 1);
        var dto = TryGet<RebuttalDto>(block) ?? TryGet<RebuttalDto>(LoosenJson(block));
        var resp = dto?.Responses ?? new();
        for (int i = 0; i < points.Count && i < resp.Count; i++)
            if (!string.IsNullOrWhiteSpace(resp[i].Rebuttal))
                points[i].Rebuttal = resp[i].Rebuttal!.Trim();
    }

    /// <summary>The realistic 12–24 month salary ceiling for this candidate: ~1.5× their own target
    /// (or floor). Used only to INFORM the prompt — the model is told this figure and reasons about it;
    /// nothing post-processes or overrides the model's answer. Returns 0 when there's no anchor.</summary>
    private static int SalaryCeiling(UserProfile p)
    {
        int anchor = p.SalaryTargetEur > 0 ? p.SalaryTargetEur
                   : p.SalaryFloorEur > 0 ? p.SalaryFloorEur : 0;
        return anchor == 0 ? 0 : (int)Math.Round(anchor * 1.5 / 1000.0) * 1000;
    }

    private static string ProfileSig(UserProfile p, string field, string topRole)
        => string.Join("|", field, topRole, p.SeniorityTarget, string.Join(",", p.CoreSkills.Take(5))).ToLowerInvariant();

    private const int ResearchTtlMinutes = 60;

    private static List<WebResult> LoadResearch(string? path, string sig)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new();
            var c = JsonSerializer.Deserialize<CareerResearch>(File.ReadAllText(path), J);
            if (c is null || c.ProfileSig != sig || c.Snippets.Count == 0) return new();
            if (!DateTime.TryParse(c.SavedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var saved)) return new();
            if (DateTime.UtcNow - saved.ToUniversalTime() > TimeSpan.FromMinutes(ResearchTtlMinutes)) return new();
            return c.Snippets;
        }
        catch { return new(); }
    }

    private static void SaveResearch(string? path, string sig, List<WebResult> snippets)
    {
        if (string.IsNullOrWhiteSpace(path) || snippets.Count == 0) return;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(
                new CareerResearch { SavedUtc = DateTime.UtcNow.ToString("o"), ProfileSig = sig, Snippets = snippets },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    private sealed class CareerResearch
    {
        public string SavedUtc { get; set; } = "";
        public string ProfileSig { get; set; } = "";
        public List<WebResult> Snippets { get; set; } = new();
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

    private sealed class CritiqueDto { public List<CritiqueItemDto>? Critique { get; set; } }
    private sealed class CritiqueItemDto { public string? Claim { get; set; } public string? Issue { get; set; } }
    private sealed class RebuttalDto { public List<RebuttalItemDto>? Responses { get; set; } }
    private sealed class RebuttalItemDto { public string? Claim { get; set; } public string? Rebuttal { get; set; } }
}

/// <summary>How hard the plan is red-teamed after generation. Persisted as the user's choice in the Grow area.</summary>
public enum CritiqueMode
{
    Single,   // attacker only — list the weakest claims
    Debate,   // attacker + the author's one-line rebuttals
    Revise,   // debate, then rewrite the plan from the valid objections
}
