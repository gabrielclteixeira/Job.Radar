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
    private static readonly JsonSerializerOptions J = new()
    {
        PropertyNameCaseInsensitive = true,
        // Local models often emit numbers as strings ("3.9", "1200") — read them rather than throw.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static async Task<(CompanyBrief? brief, string? error)> ResearchAsync(
        ClaudeConfig llm, UserProfile profile, string company, string role, string? location,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(company)) return (null, null);

        // 1) Gather context from the web (key-free, best-effort). One combined query — a second
        // back-to-back query gets throttled by the search engine and just adds ~10s for nothing.
        log?.Report(Loc.Instance.F("research.searching", company));
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
        log?.Report(Loc.Instance.F("research.analyzing", results.Count));
        string prompt =
$@"From the web search snippets below, build a SHORT, honest employer briefing for THIS candidate.
Use ONLY the snippets (don't invent). Reply with ONLY one valid JSON object, double-quoted keys/values, shape:
{{""pros"":[""short phrase""],""cons"":[""short phrase""],""context"":[""neutral fact""],""reputationNote"":""one sentence or empty"",""salaryFound"":""comparable figures with currency, or 'desconhecido'"",""salaryExpectation"":""a €/yr range to target at THIS company"",""salaryRationale"":""1 sentence: how you derived it"",""bottomLine"":""one sentence""}}
- pros/cons: concrete, from reviews; cite sources inline like [2]. Keep each under ~12 words.
- salaryExpectation: a realistic €/year range for THIS candidate's level and LOCATION ({location ?? "their local market"}),
  ANCHORED to what THIS company actually pays per the found figures. Company averages often mix junior roles, so a
  more experienced candidate may sit at the top of the found band or modestly above it — but NEVER far beyond what
  this employer plausibly pays. The candidate's own floor/target describe what they WANT, not what the company pays:
  never inflate the range toward them. If the company's figures sit below the candidate's floor, keep the expectation
  at the company's reality and flag the mismatch in bottomLine instead. US/global snippets are inflated — adjust DOWN
  to the local market; don't copy US numbers or assume remote = global pay. If figures are thin, widen the range and
  say so in salaryRationale.
- Be honest when data is thin (empty arrays are fine).
- Write the text in {Loc.Instance.T("ai.lang")}.

== CANDIDATE ==
Role: {role} · {cand} · location {location ?? "—"}

== SEARCH RESULTS ==
{snippets}";

        string? text = await LlmClient.CompleteAsync(llm, prompt, ct, json: true);
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

    /// <summary>
    /// Researches an employer into a STRUCTURED <see cref="CompanyReport"/> (rating, layoffs, pay band,
    /// tenure, pros/cons/red-flags) for the Company Researcher view. Same key-free web-search → LLM-extract
    /// pipeline as <see cref="ResearchAsync"/>, but the model is forced into a compact JSON schema with hard
    /// anti-fabrication rules: any signal absent from the snippets is left null/empty ("unknown"), never
    /// invented. Two targeted searches (reviews+pay, then layoffs) widen coverage; the kept output stays small
    /// so local reasoning models complete it without truncating.
    /// </summary>
    public static async Task<(CompanyReport? report, string? error)> ResearchReportAsync(
        ClaudeConfig llm, UserProfile profile, string company, string role,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(company)) return (null, null);

        // 1) Gather context — two focused queries (reviews/pay, then layoffs/news), merged + deduped by URL.
        log?.Report(Loc.Instance.F("research.searching", company));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<WebResult>();
        async Task Gather(string query, int max)
        {
            try
            {
                foreach (var r in await WebSearch.SearchAsync(query, max, ct))
                    if (!string.IsNullOrWhiteSpace(r.Url) && seen.Add(r.Url)) results.Add(r);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort — a throttled second query just yields fewer snippets */ }
        }
        await Gather($"{company} glassdoor rating reviews out of 5", 6);
        await Gather($"{company} employee reviews comparably kununu indeed", 5);
        await Gather($"{company} {role} salary levels.fyi", 4);
        await Gather($"{company} layoffs OR funding OR acquisition OR \"hiring freeze\" news 2026", 5);
        results = results.Take(16).ToList();
        if (results.Count == 0) return (null, Loc.Instance.T("research.noWeb"));

        var snippets = new StringBuilder();
        for (int i = 0; i < results.Count; i++)
            snippets.AppendLine($"[{i + 1}] {results[i].Title}\n{results[i].Snippet}\n{results[i].Url}\n");

        // The aggregate ★ rating is rarely in the SERP snippet — it lives on the reviews page. Fetch the best
        // review page (key-free, via Jina) and append its text so the model can read e.g. "3.9 ★ · 303 reviews".
        var reviewUrl = results.FirstOrDefault(r => IsReviewSite(r.Url))?.Url;
        if (!string.IsNullOrWhiteSpace(reviewUrl))
        {
            log?.Report(Loc.Instance.F("research.reading", Host(reviewUrl)));
            try
            {
                string page = await WebFetch.FetchMarkdownAsync(reviewUrl, 1800, ct);
                if (!string.IsNullOrWhiteSpace(page))
                    snippets.AppendLine($"[PAGE] {Host(reviewUrl)} (full-text excerpt)\n{page}\n");
            }
            catch (OperationCanceledException) { throw; }
            catch { /* page fetch is best-effort */ }
        }

        // Authoritative firmographics from Wikidata (key-free, structured) — grounds founded/HQ/industry/CEO
        // far better than guessing from snippets. Injected as facts; also applied directly after parsing.
        var facts = await WikidataClient.LookupAsync(company, ct);
        if (facts is not null)
            snippets.AppendLine("[WIKIDATA] authoritative firmographics — "
                + string.Join(" · ", new[]
                {
                    facts.Founded.Length > 0 ? "founded " + facts.Founded : "",
                    facts.Headquarters.Length > 0 ? "HQ " + facts.Headquarters : "",
                    facts.Country.Length > 0 ? facts.Country : "",
                    facts.Industry.Length > 0 ? "industry " + facts.Industry : "",
                    facts.Employees.Length > 0 ? facts.Employees + " employees" : "",
                    facts.Ceo.Length > 0 ? "CEO " + facts.Ceo : "",
                    facts.Website.Length > 0 ? facts.Website : "",
                }.Where(s => s.Length > 0)) + "\n");

        int floor = profile.SalaryFloorEur, target = profile.SalaryTargetEur;
        string loc = profile.Locations.FirstOrDefault() ?? "";
        string cand = $"{profile.YearsExperience} years experience, target level {profile.SeniorityTarget}" +
                      (floor > 0 || target > 0 ? $", own salary floor €{floor:N0} / target €{target:N0}/yr" : "");

        // 2) Force a compact, source-linked JSON. Nullable numbers + empty arrays = honest "unknown".
        log?.Report(Loc.Instance.F("research.analyzing", results.Count));
        string prompt =
$@"From the web search snippets (and the [PAGE] full-text excerpt, if present) below, extract STRUCTURED
employer-health signals for ""{company}"". Use ONLY the provided text — NEVER invent a number. If a signal isn't
present, leave it null (numbers) or empty (strings/arrays); we show it as ""unknown"". Reply with ONLY one valid
JSON object, double-quoted, this shape:
{{""rating"":number-or-null,""ratingScale"":5,""reviewCount"":number-or-null,""ratingSource"":""Glassdoor/kununu/Indeed or empty"",""recommendPct"":number-or-null,""ceoApprovalPct"":number-or-null,""enps"":number-or-null,""workLife"":number-or-null,""culture"":number-or-null,""career"":number-or-null,""management"":number-or-null,""compensation"":number-or-null,""diversity"":number-or-null,""interviewDifficulty"":""e.g. Average 2.9/5, or empty"",""layoffs"":[{{""period"":""e.g. 2024 / Q1 2025"",""scale"":""e.g. ~500 roles / 10% of staff"",""note"":""short"",""source"":""[n]""}}],""signals"":[""recent event ≤14 words with [n]""],""payBand"":""€/yr range for the role, or empty"",""payRole"":""role the band refers to"",""tenure"":""avg tenure if stated, else empty"",""industry"":""short, or empty"",""companySize"":""e.g. 1,000–5,000 employees, or empty"",""headquarters"":""city/country, or empty"",""founded"":""year, or empty"",""ceo"":""name, or empty"",""pros"":[""≤12 words""],""cons"":[""≤12 words""],""redFlags"":[""≤12 words, serious only""],""bottomLine"":""one sentence"",""confidence"":""high|medium|low|unknown""}}
Rules:
- rating: the 0–5 star AVERAGE if stated anywhere (e.g. ""3.9 ★"", ""3.9 out of 5"", ""rated 3.9/5"") — convert other
  scales to /5. The [PAGE] excerpt is the best place to find it. If only a review COUNT is given (e.g. ""303
  reviews"") with no average, STILL set reviewCount + ratingSource and leave rating null.
- reviewCount: total number of reviews behind the rating, if stated.
- recommendPct / ceoApprovalPct: only if explicitly stated as a percentage; else null.
- workLife/culture/career/management/compensation/diversity: Glassdoor/Comparably SUB-ratings on a 0–5 scale, only if stated; else null.
- enps: Comparably employee Net Promoter Score (-100..100), only if stated; else null. interviewDifficulty: only if stated.
- layoffs: only REAL, dated events found in the text; [] if none. Put the snippet index in ""source"" as ""[n]"".
- signals: other recent notable events from the last ~18 months — funding round, acquisition/merger, hiring freeze,
  leadership change, office open/close. Each ≤14 words with an inline [n]. [] if none. Do NOT duplicate layoffs here.
- payBand: a realistic €/year range for THIS candidate's level ({cand}) and LOCATION ({(string.IsNullOrWhiteSpace(loc) ? "their local market" : loc)}),
  ANCHORED to figures found for THIS company. A more experienced candidate may sit at the top of the found band, but
  never far beyond what this employer plausibly pays. The candidate's floor/target are wishes, NOT evidence of company
  pay — never inflate the band toward them; if company figures sit below the floor, keep the band at company reality.
  US/global snippets are inflated — adjust DOWN to the local market; never copy US figures verbatim. If figures are
  thin, widen the range and lower confidence.
- industry/companySize/headquarters/founded/ceo: short, only if stated. PREFER the [WIKIDATA] line when present (it's authoritative).
- confidence: ""high"" only if several sources corroborate; ""unknown"" if you'd be guessing — be honest.
- arrays: at most 3 items each, short. Empty arrays are fine when data is thin.
- Write any prose in {Loc.Instance.T("ai.lang")}.

== CANDIDATE ==
Role: {role} · {cand}

== SEARCH RESULTS ==
{snippets}";

        string? text = await LlmClient.CompleteAsync(llm, prompt, ct, json: true);
        if (string.IsNullOrWhiteSpace(text))
        {
            string msg = Loc.Instance.T("research.noModel");
            if (!string.IsNullOrWhiteSpace(LlmClient.LastError)) msg += ": " + LlmClient.LastError;
            return (null, msg);
        }

        var report = ParseReport(text!, results) ?? new CompanyReport { RawFallback = text!.Trim() };
        report.Company = company;
        report.AsOfUtc = DateTime.UtcNow.ToString("o");
        if (facts is not null) ApplyFacts(report, facts);   // authoritative firmographics override guesses

        // Attach sources from the search results (not from the model).
        for (int i = 0; i < results.Count; i++)
            report.Sources.Add(new SourceRef { N = i + 1, Url = results[i].Url, Host = Host(results[i].Url) });
        return (report, null);
    }

    private static CompanyReport? ParseReport(string raw, List<WebResult> results)
    {
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        string block = raw.Substring(a, b - a + 1);
        var dto = TryReportDto(block) ?? TryReportDto(LoosenJson(block));
        if (dto is null) return null;

        string conf = (dto.Confidence ?? "").Trim().ToLowerInvariant();
        if (conf is not ("high" or "medium" or "low")) conf = "unknown";

        var report = new CompanyReport
        {
            Rating = dto.Rating is > 0 ? dto.Rating : null,
            RatingScale = dto.RatingScale is > 0 ? dto.RatingScale.Value : 5,
            ReviewCount = dto.ReviewCount is > 0 ? dto.ReviewCount : null,
            RatingSource = dto.RatingSource ?? "",
            RecommendPct = dto.RecommendPct is > 0 and <= 100 ? dto.RecommendPct : null,
            CeoApprovalPct = dto.CeoApprovalPct is > 0 and <= 100 ? dto.CeoApprovalPct : null,
            ENps = dto.Enps is >= -100 and <= 100 ? dto.Enps : null,
            WorkLifeRating = Sub(dto.WorkLife),
            CultureRating = Sub(dto.Culture),
            CareerRating = Sub(dto.Career),
            ManagementRating = Sub(dto.Management),
            CompensationRating = Sub(dto.Compensation),
            DiversityRating = Sub(dto.Diversity),
            InterviewDifficulty = dto.InterviewDifficulty ?? "",
            PayBand = dto.PayBand ?? "",
            PayRole = dto.PayRole ?? "",
            Tenure = dto.Tenure ?? "",
            Industry = dto.Industry ?? "",
            CompanySize = dto.CompanySize ?? "",
            Headquarters = dto.Headquarters ?? "",
            Founded = dto.Founded ?? "",
            Ceo = dto.Ceo ?? "",
            Pros = dto.Pros ?? new(),
            Cons = dto.Cons ?? new(),
            RedFlags = dto.RedFlags ?? new(),
            BottomLine = dto.BottomLine ?? "",
            Confidence = conf,
        };
        foreach (var l in dto.Layoffs ?? new())
        {
            if (l is null) continue;
            string period = l.Period ?? "", scale = l.Scale ?? "", note = l.Note ?? "";
            if (period.Length == 0 && scale.Length == 0 && note.Length == 0) continue;
            report.Layoffs.Add(new LayoffEvent
            {
                Period = period, Scale = scale, Note = note, Url = ResolveSource(l.Source, results),
            });
        }
        foreach (var s in dto.Signals ?? new())
            if (!string.IsNullOrWhiteSpace(s)) report.Signals.Add(s.Trim());
        return report;
    }

    /// <summary>Keep a sub-rating only if it's a sane 0–5 value.</summary>
    private static double? Sub(double? v) => v is > 0 and <= 5 ? v : null;

    /// <summary>Overlays authoritative Wikidata firmographics onto a report (Wikidata wins where present).</summary>
    private static void ApplyFacts(CompanyReport r, WikidataClient.WikidataFacts f)
    {
        if (f.Founded.Length > 0) r.Founded = f.Founded;
        if (f.Industry.Length > 0) r.Industry = f.Industry;
        if (f.Ceo.Length > 0) r.Ceo = f.Ceo;
        if (f.Website.Length > 0) r.Website = f.Website;
        string hq = string.Join(", ", new[] { f.Headquarters, f.Country }.Where(s => s.Length > 0).Distinct());
        if (hq.Length > 0) r.Headquarters = hq;
        if (f.Employees.Length > 0 && int.TryParse(f.Employees, out int emp) && emp > 0)
            r.CompanySize = $"{emp:N0} employees";
    }

    /// <summary>Turns a "[n]" snippet reference (or a raw URL) into the real source URL when possible.</summary>
    private static string ResolveSource(string? src, List<WebResult> results)
    {
        src = (src ?? "").Trim();
        if (src.Length == 0) return "";
        var m = Regex.Match(src, @"\[(\d+)\]");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= results.Count)
            return results[n - 1].Url;
        return src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? src : "";
    }

    /// <summary>True for employer-review hosts worth fetching in full (the ★ average lives on the page).</summary>
    private static bool IsReviewSite(string url)
    {
        string h = Host(url).ToLowerInvariant();
        return h.Contains("glassdoor") || h.Contains("kununu") || h.Contains("indeed")
            || h.Contains("comparably") || h.Contains("ambitionbox");
    }

    private static ReportDto? TryReportDto(string json)
    {
        try { return JsonSerializer.Deserialize<ReportDto>(json, J); }
        catch { return null; }
    }

    private sealed class ReportDto
    {
        public double? Rating { get; set; }
        public double? RatingScale { get; set; }
        public int? ReviewCount { get; set; }
        public string? RatingSource { get; set; }
        public int? RecommendPct { get; set; }
        public int? CeoApprovalPct { get; set; }
        public int? Enps { get; set; }
        public double? WorkLife { get; set; }
        public double? Culture { get; set; }
        public double? Career { get; set; }
        public double? Management { get; set; }
        public double? Compensation { get; set; }
        public double? Diversity { get; set; }
        public string? InterviewDifficulty { get; set; }
        public List<LayoffDto>? Layoffs { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? Signals { get; set; }
        public string? PayBand { get; set; }
        public string? PayRole { get; set; }
        public string? Tenure { get; set; }
        public string? Industry { get; set; }
        public string? CompanySize { get; set; }
        public string? Headquarters { get; set; }
        public string? Founded { get; set; }
        public string? Ceo { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? Pros { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? Cons { get; set; }
        [JsonConverter(typeof(StringListConverter))] public List<string>? RedFlags { get; set; }
        public string? BottomLine { get; set; }
        public string? Confidence { get; set; }
    }

    private sealed class LayoffDto
    {
        public string? Period { get; set; }
        public string? Scale { get; set; }
        public string? Note { get; set; }
        public string? Source { get; set; }
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
