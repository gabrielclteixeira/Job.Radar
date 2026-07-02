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
        string? cachePath = null, IProgress<string>? log = null, IProgress<string>? reasoning = null,
        string? completedContext = null, string? partsCachePath = null, CancellationToken ct = default)
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

        // Salary anchors that keep weaker models honest (single source — shared with the split parts and revise).
        var salary = BuildSalaryCtx(profile, loc);
        string locName = salary.LocName, anchor = salary.Anchor, core = salary.Core,
               ceilingLine = salary.CeilingLine, reminderLine = salary.ReminderLine;

        string prompt =
$@"You are a candid career coach. From the candidate, the jobs the app already found, and the web
research snippets below, build a SHORT, honest growth plan. Use ONLY the snippets for market claims
(don't invent figures); cite sources inline like [3]. Reply with ONLY one valid JSON object,
double-quoted keys/values, shape:
{{""headline"":""one-line positioning for this candidate"",""strengths"":[""short phrase""],""skillGaps"":[{{""skill"":""name"",""why"":""why it matters"",""action"":""one concrete way to build it""}}],""targetRoles"":[""role title""],""salaryRationale"":""1 sentence: state the local {locName} EUR range you assume, then how you derived the bands"",""salaryNow"":""the €/yr band ONLY, e.g. €45,000–€55,000/yr"",""salaryPotential"":""the €/yr band ONLY, e.g. €52,000–€60,000/yr"",""steps"":[{{""horizon"":""0–3 meses"",""title"":""short action"",""detail"":""one sentence""}}],""marketSignals"":[""in-demand skill or hiring trend, cite [n]""],""bottomLine"":""one encouraging, honest sentence""}}
- 3–5 strengths, 2–4 skillGaps, 2–4 targetRoles, 3–5 steps ordered by horizon (0–3 / 3–6 / 6–12 meses).
- Prefer skillGaps SUPPORTED by the demand signal in the JOBS block (the app's own scored jobs); a recurring gap there beats a web-only one. That list is a signal, not authoritative — it may include off-target jobs, so don't over-fit.
- salaryNow / salaryPotential = the NUMERIC BAND ONLY (e.g. €45,000–€55,000/yr) — no words/caveats/parentheses; put every caveat and derivation in salaryRationale.
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
{(string.IsNullOrWhiteSpace(marketContext) ? "(none yet)" : marketContext)}{DoneBlock(completedContext)}
== WEB RESEARCH (reference only — skews US/global, do not copy figures) ==
{snippets}
== FINAL REMINDER (this overrides anything inflated above) ==
{reminderLine}";

        CareerPlanResult plan;
        if (IsLocal(llm))
        {
            // Local/OpenAI-compatible models truncate one big JSON — build the plan in small, reliably-
            // completing parts (helps reasoning models that overthink AND weak small models alike). Each
            // completed part is cached (keyed on the exact inputs) so a paused/interrupted run resumes without
            // redoing finished parts.
            string partsSig = PartsSig(sig, snippets, marketContext, completedContext);
            plan = await SynthesizeAsync(llm, profile, marketContext, snippets, salary, completedContext,
                partsCachePath, partsSig, log, reasoning, ct);
        }
        else
        {
            // Claude CLI handles the whole plan in one shot and is metered — keep it a single call.
            string? text = await LlmClient.CompleteAsync(llm, prompt, reasoning, ct);
            plan = string.IsNullOrWhiteSpace(text)
                ? new CareerPlanResult()
                : (Parse(text) ?? new CareerPlanResult { RawFallback = text!.Trim() });
        }

        // Nothing usable came back (even Positioning failed, or the model is down).
        if (!plan.HasHeadline && plan.Strengths.Count == 0 && plan.Steps.Count == 0 && !plan.HasSalaryNow && !plan.HasFallback)
        {
            string msg = Loc.Instance.T("plan.noModel");
            if (!string.IsNullOrWhiteSpace(LlmClient.LastError)) msg += ": " + LlmClient.LastError;
            return (null, msg);
        }

        plan.SavedUtc = DateTime.UtcNow.ToString("o");
        for (int i = 0; i < results.Count; i++)
            plan.Sources.Add(new SourceRef { N = i + 1, Url = results[i].Url, Host = Host(results[i].Url) });
        return (plan, null);
    }

    // ===== Split synthesis (local models): build the plan in small parts that complete reliably =====

    private static bool IsLocal(ClaudeConfig cfg)
        => (cfg.Provider?.Trim().ToLowerInvariant()) is "openai" or "local" or "http";

    /// <summary>The "already done" section reinjected on a regenerate so the model advances from completed work
    /// instead of listing it again. Empty when nothing is done yet.</summary>
    private static string DoneBlock(string? completed) =>
        string.IsNullOrWhiteSpace(completed) ? "" :
$@"
== ALREADY DONE (the candidate has COMPLETED these — build FORWARD from here; do NOT repeat them as gaps or steps) ==
{completed.Trim()}";

    /// <summary>Salary-prompt anchors, computed once and shared by the one-shot prompt, the split Salary part,
    /// and the surgical revise — so "what counts as a sane local band" is defined in exactly one place.</summary>
    private readonly record struct SalaryCtx(string LocName, string Anchor, string Core, int Ceiling,
        string CeilingLine, string ReminderLine, string TargetText);

    private static SalaryCtx BuildSalaryCtx(UserProfile profile, string loc)
    {
        int floor = profile.SalaryFloorEur, target = profile.SalaryTargetEur;
        string locName = string.IsNullOrWhiteSpace(loc) ? "the candidate's country" : loc;
        string anchor = (floor > 0 || target > 0)
            ? $"the candidate's own floor €{floor:N0} / target €{target:N0} per year"
            : "the candidate's stated level and local market norms";
        string core = profile.CoreSkills.Count > 0 ? string.Join(", ", profile.CoreSkills.Take(6)) : "their current stack";
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
        return new SalaryCtx(locName, anchor, core, ceiling, ceilingLine, reminderLine, targetText);
    }

    /// <summary>Shared candidate/jobs/research context block, identical across every part prompt.</summary>
    private static string CandidateBlock(UserProfile profile, string marketContext, string snippets) =>
$@"== CANDIDATE ==
{profile.ToScoringText()}
== JOBS THE APP ALREADY SCORED ==
{(string.IsNullOrWhiteSpace(marketContext) ? "(none yet)" : marketContext)}
== WEB RESEARCH (reference only — skews US/global, do not copy figures) ==
{snippets}";

    /// <summary>A one-line hand-off so later parts cohere with the positioning/roles chosen first.</summary>
    private static string Coherence(string? headline, List<string>? roles)
    {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(headline)) bits.Add($"Positioning: {headline}");
        if (roles is { Count: > 0 }) bits.Add("Target roles: " + string.Join(", ", roles));
        return bits.Count == 0 ? "" : "Stay coherent with the rest of the plan — " + string.Join("; ", bits) + ". ";
    }

    /// <summary>Builds the plan as 4 small, sequential calls (each a tiny JSON that finishes inside the cap),
    /// then assembles them. Positioning runs first and feeds the others for coherence.</summary>
    private static async Task<CareerPlanResult> SynthesizeAsync(ClaudeConfig llm, UserProfile profile,
        string marketContext, string snippets, SalaryCtx salary, string? completedContext,
        string? partsCachePath, string partsSig, IProgress<string>? log, IProgress<string>? reasoning, CancellationToken ct)
    {
        string lang = Loc.Instance.T("ai.lang");
        string ctx = CandidateBlock(profile, marketContext, snippets) + DoneBlock(completedContext);

        // Resume support: reuse any parts a prior (paused/crashed) run of THIS exact generation already produced,
        // and persist each part as it completes so a pause loses at most the part in flight.
        var cache = LoadParts(partsCachePath, partsSig);
        cache.Sig = partsSig;
        async Task<string?> Part(string? cached, string prompt)
        {
            if (!string.IsNullOrWhiteSpace(cached)) return cached;
            return await LlmClient.CompleteAsync(llm, prompt, reasoning, ct);
        }

        log?.Report(Loc.Instance.T("plan.synth.positioning"));
        cache.Positioning = await Part(cache.Positioning,
$@"You are a candid career coach. Using the candidate, the jobs already scored, and the research below, output
ONLY one JSON object: {{""headline"":""one-line positioning for this candidate"",""strengths"":[""short phrase""],""targetRoles"":[""role title""]}}
- 3–5 strengths, 2–4 targetRoles. Every phrase concrete, under ~16 words. Write in {lang}.
{ctx}");
        // Cache only output that PARSES — junk in the cache would make every retry within the TTL replay the
        // same failure without re-rolling the model. Same guard on every part below.
        var pos = ParsePart<PositioningDto>(cache.Positioning);
        if (pos is null || string.IsNullOrWhiteSpace(pos.Headline))
        {
            cache.Positioning = null;
            SaveParts(partsCachePath, cache);
            return new CareerPlanResult();  // hard-fail upstream
        }
        SaveParts(partsCachePath, cache);
        string coherence = Coherence(pos.Headline, pos.TargetRoles);

        log?.Report(Loc.Instance.T("plan.synth.gaps"));
        cache.GapsSteps = await Part(cache.GapsSteps,
$@"You are a candid career coach. {coherence}Output ONLY one JSON object:
{{""skillGaps"":[{{""skill"":""name"",""why"":""why it matters"",""action"":""one concrete way to build it""}}],""steps"":[{{""horizon"":""0–3 meses"",""title"":""short action"",""detail"":""one sentence""}}]}}
- 2–4 skillGaps, 3–5 steps ordered by horizon (0–3 / 3–6 / 6–12 meses). Cite sources [n] where relevant.
- PREFER skill gaps SUPPORTED by the demand signal in the jobs the app already scored (the JOBS block): a gap
  seen recurring there is stronger than a web-only one. But that job list is a SIGNAL, not exhaustive or
  authoritative — it may contain off-target jobs, so don't over-fit to it or invent a gap just because one job mentions it.
- BUILD ON THE CANDIDATE'S CORE STACK ({salary.Core}); deepen/specialise within it plus adjacent in-demand areas.
  Do NOT make ""learn Python"" (or switching primary language) a step; express AI/data work THROUGH their stack.
- Every phrase concrete, under ~16 words. Write in {lang}.
{ctx}");
        var gs = ParsePart<GapsStepsDto>(cache.GapsSteps);
        if (gs is null) cache.GapsSteps = null;
        SaveParts(partsCachePath, cache);

        log?.Report(Loc.Instance.T("plan.synth.salary"));
        cache.Salary = await Part(cache.Salary, BuildSalaryPrompt(profile, marketContext, snippets, salary, coherence));
        var sal = ParsePart<SalaryDto>(cache.Salary);
        if (sal is null) cache.Salary = null;
        SaveParts(partsCachePath, cache);

        log?.Report(Loc.Instance.T("plan.synth.signals"));
        cache.Signals = await Part(cache.Signals,
$@"You are a candid career coach. {coherence}From the research snippets, output ONLY one JSON object:
{{""marketSignals"":[""in-demand skill or hiring trend, cite [n]""],""bottomLine"":""one encouraging, honest sentence""}}
- 2–5 marketSignals, each citing [n]. Use ONLY the snippets for market claims. Write in {lang}.
{ctx}");
        var sig = ParsePart<SignalsDto>(cache.Signals);
        if (sig is null) cache.Signals = null;
        SaveParts(partsCachePath, cache);

        return Assemble(pos, gs, sal, sig);
    }

    // ===== Part-level resume cache (mirrors the research cache: SavedUtc + Sig + TTL) =====

    private const int PartsTtlMinutes = 90;

    /// <summary>Keys the parts cache on the EXACT generation inputs (profile + research + market + already-done),
    /// so cached parts are reused only for a genuinely-identical interrupted run — never for a real regenerate
    /// (e.g. after ticking items off, which changes the completed-context). Stable across restarts (SHA-256).</summary>
    private static string PartsSig(string profileSig, string snippets, string marketContext, string? completed)
    {
        string blob = string.Join("", profileSig, snippets ?? "", marketContext ?? "", completed ?? "");
        byte[] h = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(blob));
        return profileSig + "#" + Convert.ToHexString(h, 0, 6);
    }

    private sealed class PlanPartsCache
    {
        public string SavedUtc { get; set; } = "";
        public string Sig { get; set; } = "";
        // Raw LLM text per part (re-parsed on load) — plain strings avoid serializing the custom-converter DTOs.
        public string? Positioning { get; set; }
        public string? GapsSteps { get; set; }
        public string? Salary { get; set; }
        public string? Signals { get; set; }
    }

    private static PlanPartsCache LoadParts(string? path, string sig)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new();
            var c = JsonSerializer.Deserialize<PlanPartsCache>(File.ReadAllText(path), J);
            if (c is null || c.Sig != sig) return new();
            if (!DateTime.TryParse(c.SavedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var saved)) return new();
            if (DateTime.UtcNow - saved.ToUniversalTime() > TimeSpan.FromMinutes(PartsTtlMinutes)) return new();
            return c;
        }
        catch { return new(); }
    }

    private static void SaveParts(string? path, PlanPartsCache c)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            c.SavedUtc = DateTime.UtcNow.ToString("o");
            File.WriteAllText(path, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Deletes the parts cache once a whole run (synthesis + critique) has completed, so the next fresh
    /// regenerate starts clean. Called by the ViewModel after the full GeneratePlan flow succeeds.</summary>
    public static void ClearParts(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    /// <summary>The Salary sub-prompt — the anti-inflation hard rules live ONLY here, so they don't bloat (and
    /// truncate) the other parts. Shared by synthesis and surgical revise.</summary>
    private static string BuildSalaryPrompt(UserProfile profile, string marketContext, string snippets, SalaryCtx s, string coherence) =>
$@"You are a candid career coach setting REALISTIC LOCAL salary expectations. {coherence}Output ONLY one JSON object:
{{""salaryRationale"":""1 sentence: the local {s.LocName} EUR range you assume, then how you derived the bands"",""salaryNow"":""the €/yr band ONLY, e.g. €45,000–€55,000/yr"",""salaryPotential"":""the €/yr band ONLY, e.g. €52,000–€60,000/yr""}}

== HARD RULES — READ BEFORE WRITING ANY NUMBER (most models inflate salary here) ==
1. ALL salary figures must be LOCAL {s.LocName} EUR/year.
2. The snippets rarely contain a real local figure. When you DON'T have a cited local number (you usually don't),
   anchor to {s.Anchor} — NOT your general expectations, US/global pay, or remote listings. A cautious range
   anchored to the candidate's own floor/target is the correct answer; do not invent an aspirational figure.
3. €100k+ remote listings carry crypto/US-only/contractor caveats — they are NOT the local {s.LocName} rate.
4. {s.CeilingLine}
5. salaryNow and salaryPotential contain the NUMERIC BAND ONLY (e.g. €45,000–€55,000/yr) — no words, caveats,
   ""with"", ""reachable"", or parentheses. Put EVERY caveat, stretch note and derivation in salaryRationale.
6. Write salaryRationale FIRST, then salaryNow and salaryPotential consistent with it. Write in {Loc.Instance.T("ai.lang")}.
{CandidateBlock(profile, marketContext, snippets)}
== FINAL REMINDER (overrides anything inflated above) ==
{s.ReminderLine}";

    /// <summary>Keeps only the numeric band in a salary field. Models sometimes append rationale/caveats to
    /// <c>salaryNow</c>/<c>salaryPotential</c> ("…/yr, with €65k reachable only…"), which belong in the rationale
    /// and overflow the band card. Cuts at the first explicit prose joiner AFTER a figure; returns the input
    /// unchanged when it's already a clean band (conservative — never blanks a real value).</summary>
    private static string CleanBand(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return "";
        // Cut at unambiguously-prose joiners only. Never a bare dash (that's the range separator, e.g.
        // "€45,000 – €55,000") or a bare comma (thousands separator) — those keep a valid band intact.
        foreach (var sep in new[] { " with ", " com ", " (", " reachable", " atingível" })
        {
            int i = s.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 3 && s[..i].Any(char.IsDigit)) s = s[..i];
        }
        return s.TrimEnd(',', ' ', '-', '–', '.', ';');
    }

    /// <summary>Generalized JSON salvage (same as <see cref="Parse"/>) for the small part objects.</summary>
    private static T? ParsePart<T>(string? raw) where T : class
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        string block = raw.Substring(a, b - a + 1);
        return TryGet<T>(block) ?? TryGet<T>(LoosenJson(block));
    }

    /// <summary>Merges the part fragments into one plan, with the SAME defaults/filters as the one-shot
    /// <see cref="Parse"/> — a missing part just degrades to empty and its UI/PDF section hides.</summary>
    private static CareerPlanResult Assemble(PositioningDto pos, GapsStepsDto? gs, SalaryDto? sal, SignalsDto? sig) => new()
    {
        Headline = pos.Headline ?? "",
        Strengths = pos.Strengths ?? new(),
        TargetRoles = pos.TargetRoles ?? new(),
        SkillGaps = (gs?.SkillGaps ?? new()).Select(g => new SkillGap { Skill = g.Skill ?? "", Why = g.Why ?? "", Action = g.Action ?? "" })
            .Where(g => !string.IsNullOrWhiteSpace(g.Skill)).ToList(),
        Steps = (gs?.Steps ?? new()).Select(s => new PlanStep { Horizon = s.Horizon ?? "", Title = s.Title ?? "", Detail = s.Detail ?? "" })
            .Where(s => !string.IsNullOrWhiteSpace(s.Title)).ToList(),
        SalaryRationale = sal?.SalaryRationale ?? "",
        SalaryNow = CleanBand(sal?.SalaryNow),
        SalaryPotential = CleanBand(sal?.SalaryPotential),
        MarketSignals = sig?.MarketSignals ?? new(),
        BottomLine = sig?.BottomLine ?? "",
    };

    private sealed class PositioningDto
    {
        public string? Headline { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? Strengths { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? TargetRoles { get; set; }
    }
    private sealed class GapsStepsDto { public List<GapDto>? SkillGaps { get; set; } public List<StepDto>? Steps { get; set; } }
    private sealed class SalaryDto { public string? SalaryRationale { get; set; } public string? SalaryNow { get; set; } public string? SalaryPotential { get; set; } }
    private sealed class SignalsDto { [JsonConverter(typeof(StringListConverter))] public List<string>? MarketSignals { get; set; } public string? BottomLine { get; set; } }

    /// <summary>Local-model revise: re-run ONLY the salary part with fresh research + objections (salary is the
    /// usual downward correction), then overwrite just those fields. Mutates and returns the plan in place; a
    /// failed re-run leaves it untouched. Other sections are kept (rarely the flagged problem).</summary>
    private static async Task<CareerPlanResult> ReviseSurgicalAsync(ClaudeConfig llm, UserProfile profile,
        CareerPlanResult plan, List<CritiquePoint> points, List<WebResult> fresh, SalaryCtx salary,
        IProgress<string>? log, IProgress<string>? reasoning, CancellationToken ct)
    {
        log?.Report(Loc.Instance.T("plan.revising.salary"));
        string freshBlock = fresh.Count > 0
            ? Snippets(fresh)
            : "(no additional local data found — stay cautious and anchor to the candidate's floor/target; do not invent figures)";
        string objections = string.Join("\n", points.Select((p, i) =>
            $"{i + 1}. {p.Claim} — {p.Issue}" + (p.HasRebuttal ? $" (author: {p.Rebuttal})" : "")));
        string coherence = Coherence(plan.Headline, plan.TargetRoles);

        var sal = ParsePart<SalaryDto>(await LlmClient.CompleteAsync(llm,
            BuildSalaryRevisePrompt(profile, freshBlock, salary, coherence, objections), reasoning, ct));
        if (sal is not null)
        {
            if (!string.IsNullOrWhiteSpace(sal.SalaryRationale)) plan.SalaryRationale = sal.SalaryRationale!;
            if (!string.IsNullOrWhiteSpace(sal.SalaryNow)) plan.SalaryNow = CleanBand(sal.SalaryNow);
            if (!string.IsNullOrWhiteSpace(sal.SalaryPotential)) plan.SalaryPotential = CleanBand(sal.SalaryPotential);
        }

        // Append any freshly-cited sources so inline [n] resolve.
        foreach (var w in fresh)
            if (!plan.Sources.Any(s => string.Equals(s.Url, w.Url, StringComparison.OrdinalIgnoreCase)))
                plan.Sources.Add(new SourceRef { N = plan.Sources.Count + 1, Url = w.Url, Host = Host(w.Url) });
        plan.Revised = true;
        return plan;
    }

    private static string BuildSalaryRevisePrompt(UserProfile profile, string freshBlock, SalaryCtx s, string coherence, string objections) =>
$@"A reviewer flagged salary problems in a career plan for a candidate in {s.LocName}. Re-state ONLY the salary,
corrected. {coherence}Salary corrections here are almost always DOWNWARD: if a band looks high, LOWER it toward
local reality; do NOT raise unless the FRESH RESEARCH cites a local figure that supports it.
Output ONLY one JSON object: {{""salaryRationale"":""whether you found a cited local figure, then the bands"",""salaryNow"":""the €/yr band ONLY, e.g. €45,000–€55,000/yr"",""salaryPotential"":""the €/yr band ONLY, e.g. €52,000–€60,000/yr""}}

== HARD RULES ==
1. ALL figures LOCAL {s.LocName} EUR/year, anchored to {s.Anchor}.
2. {s.CeilingLine}
3. €100k+ remote listings carry crypto/US-only/contractor caveats — never the local rate.
4. salaryNow/salaryPotential = the NUMERIC BAND ONLY (e.g. €45,000–€55,000/yr) — no words or caveats; those go in salaryRationale.
Write in {Loc.Instance.T("ai.lang")}.

== OBJECTIONS ==
{objections}
== FRESH RESEARCH (newly fetched — use for local salary grounding) ==
{freshBlock}
== CANDIDATE ==
{profile.ToScoringText()}";

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
        IProgress<string>? log = null, IProgress<string>? reasoning = null, CancellationToken ct = default)
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
            // 1) ATTACKER — split by dimension: two tiny single-objection calls instead of one open-ended
            // "find all flaws" pass. Each is the smallest possible adversarial ask, so a heavy reasoning model
            // is likelier to commit and emit; any that still ruminates to truncation is dropped (clean skip).
            L(Loc.Instance.T("plan.critiquing"));
            var points = new List<CritiquePoint>();
            AddIfReal(points, ParsePart<CritiqueItemDto>(
                await LlmClient.CompleteAsync(llm, BuildSalaryObjectionPrompt(loc, ceilingNote, lang, summary), reasoning, ct)));
            AddIfReal(points, ParsePart<CritiqueItemDto>(
                await LlmClient.CompleteAsync(llm, BuildAdviceObjectionPrompt(core, lang, summary), reasoning, ct)));
            if (points.Count == 0) return plan;   // nothing usable — leave the plan as-is
            plan.Critique = points;

            // 2) DEFENDER — rebut each objection (debate / revise modes).
            if (mode is CritiqueMode.Debate or CritiqueMode.Revise)
            {
                L(Loc.Instance.T("plan.debating"));
                string objections = string.Join("\n", points.Select((p, i) => $"{i + 1}. {p.Claim} — {p.Issue}"));
                string defendPrompt =
$@"You are the plan's author. For EACH objection below, reply in ONE honest sentence (≤20 words): concede what
you'd change, or briefly justify. Output ONLY this JSON, then STOP — do not deliberate:
{{""responses"":[{{""claim"":""echo the objection's claim"",""rebuttal"":""your one-sentence response""}}]}}
Same order as the objections. Write in {lang}.

== OBJECTIONS ==
{objections}";
                string? dText = await LlmClient.CompleteAsync(llm, defendPrompt, reasoning, ct);
                ApplyRebuttals(points, dText);
            }

            // 3) REVISER — collect the data the critique said was missing, then rewrite from it.
            if (mode == CritiqueMode.Revise)
            {
                // 3a) Let the critique drive a fresh, targeted search (esp. for local salary figures) so the
                // reviser grounds in real data instead of falling back to a hallucinated 'target'.
                L(Loc.Instance.T("plan.researching"));
                var fresh = await GapFillResearchAsync(llm, loc, core, points, ct);

                // Local models: surgically re-run ONLY the salary part (the usual downward correction) — a whole-
                // plan rewrite would truncate just like synthesis. Claude keeps the full rewrite below.
                if (IsLocal(llm))
                    return await ReviseSurgicalAsync(llm, profile, plan, points, fresh, BuildSalaryCtx(profile, loc), log, reasoning, ct);

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
                string? rText = await LlmClient.CompleteAsync(llm, revisePrompt, reasoning, ct);
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

    /// <summary>A salvaged-from-a-truncated-draft value: empty, or just the schema's "…"/stub placeholder. We
    /// must drop these so a critique that didn't finish renders nothing (not garbage) and never feeds the defender.</summary>
    private static bool Degenerate(string? s)
        => string.IsNullOrWhiteSpace(s) || s.Trim().Trim('.', '…', '"', ' ', '-').Length < 4;

    /// <summary>Adds a single salvaged objection only if it's real — non-degenerate and not an explicit "none".</summary>
    private static void AddIfReal(List<CritiquePoint> list, CritiqueItemDto? c)
    {
        if (c is null) return;
        string claim = c.Claim ?? "", issue = c.Issue ?? "";
        if (Degenerate(claim) || Degenerate(issue)) return;
        if (issue.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ||
            claim.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        list.Add(new CritiquePoint { Claim = claim, Issue = issue });
    }

    private static string BuildSalaryObjectionPrompt(string loc, string ceilingNote, string lang, string summary) =>
$@"Check ONE thing in the plan below: is the salary (especially the 12–24 month ""potential"") set too HIGH for the
local {loc} market? {ceilingNote} Never argue it should be higher.
If a band is unrealistic or contradicts the plan's own rationale, output the objection; otherwise output 'none'.
Output ONLY this JSON, then STOP — do not deliberate:
{{""claim"":""the plan's exact salary claim, ≤10 words, or 'none'"",""issue"":""why it's too high, ≤18 words, or 'none'""}}
Write in {lang}.

== THE PLAN ==
{summary}";

    private static string BuildAdviceObjectionPrompt(string core, string lang, string summary) =>
$@"Check ONE thing in the plan below: is there a single most generic/boilerplate item, or a ""gap"" the candidate's
stack ({core}) already covers? If so, output the objection; otherwise output 'none'.
Output ONLY this JSON, then STOP — do not deliberate:
{{""claim"":""the plan's exact claim, ≤10 words, or 'none'"",""issue"":""why it's generic, ≤18 words, or 'none'""}}
Write in {lang}.

== THE PLAN ==
{summary}";

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
            if (!Degenerate(resp[i].Rebuttal))
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
            SalaryNow = CleanBand(dto.SalaryNow),
            SalaryPotential = CleanBand(dto.SalaryPotential),
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
