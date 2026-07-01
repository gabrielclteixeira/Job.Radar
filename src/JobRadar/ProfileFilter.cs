namespace JobRadar;

/// <summary>
/// Cheap, deterministic relevance gate + pre-score, driven by the user's
/// <see cref="UserProfile"/> — field-agnostic (works for any profession).
/// Also returns a human-readable explanation of how the score was built.
/// </summary>
public static class ProfileFilter
{
    public static (bool relevant, int preScore, string explanation, string baseVerdict) Evaluate(JobEntity j, UserProfile profile, AppConfig cfg)
    {
        string hay = $"{j.Title} {j.Description} {j.Location}".ToLowerInvariant();
        string title = j.Title.ToLowerInvariant();
        string titleLoc = $"{j.Title} {j.Location} {j.Remote}".ToLowerInvariant();

        var core = Lower(profile.CoreSkills);
        // Keyword *words* from skills/titles/field. Tokenising avoids missing "Backend Engineer"
        // when the title is the phrase "Backend Developer", and lets us match on word boundaries.
        var tokens = new HashSet<string>();
        void AddWords(IEnumerable<string> xs)
        {
            foreach (var x in Lower(xs))
                foreach (var w in SplitWords(x))
                    if (w.Length > 1 && !Generic.Contains(w)) tokens.Add(w);
        }
        AddWords(profile.CoreSkills);
        AddWords(profile.Skills);
        AddWords(profile.JobTitles);
        AddWords(new[] { profile.Field });

        var excludes = Lower(profile.DealBreakers).Concat(Lower(cfg.ExcludeKeywords)).ToList();
        var seniority = SeniorityExcludes(profile.SeniorityTarget, cfg);
        var locations = Lower(profile.Locations);

        // Hard exclusions.
        foreach (var ex in excludes)
            if (ex.Length > 1 && hay.Contains(ex)) return (false, 0, "", "");
        foreach (var sx in seniority)
            if (title.Contains(sx)) return (false, 0, "", "");

        if (tokens.Count == 0) return (false, 0, "", ""); // no profile yet

        // Relevance gate — TITLE *or* DESCRIPTION. A profile word in the title, OR the candidate's CORE STACK
        // clearly present in the description (>=2 distinct skills — enough to spot a dev role with an unusual
        // title, while ignoring a non-dev role that just mentions one tech in passing). Honours descriptions,
        // where the real requirements often live. Ambiguous short skills (e.g. "go" — also the English verb)
        // count only in the title; longer/symbol skills (".net", "c#", "docker") count in the description too.
        var matched = tokens.Where(t => WordIn(title, t)).ToList();
        var stackHits = core.Where(s => s.Length >= 3 || s.Contains('#') || s.Contains('.') ? WordIn(hay, s) : WordIn(title, s)).ToList();
        if (matched.Count == 0 && stackHits.Count < 2) return (false, 0, "", "");

        // Role-specificity: a shared GENERIC word ("engineer"/"developer") with NO core stack anywhere is an
        // off-stack role, not a fit. A distinctive title term, or any stack hit (incl. from the description), keeps it.
        if (core.Count > 0 && !matched.Any(t => !GenericRole.Contains(t)) && stackHits.Count == 0)
            return (false, 0, "", "");

        // Location gate honouring the user's prefs. Read remote/hybrid from the STRUCTURED signal + title/location
        // only — NOT the description: boilerplate like "remote-friendly team" was falsely flagging onsite foreign
        // jobs (e.g. Berlin) as remote, slipping them past the gate when the user accepts remote.
        bool isRemote = j.Remote == "remote" || titleLoc.Contains("remote") || titleLoc.Contains("remoto");
        bool isHybrid = j.Remote == "hybrid" || titleLoc.Contains("hybrid") || titleLoc.Contains("híbrido");
        bool locMatch = locations.Any(l => l.Length > 1 && titleLoc.Contains(l));
        // A remote job only counts if its location is flexible for this candidate — our location, a region-wide
        // marker (Europe/EMEA/Worldwide), or unknown. A specific foreign city (e.g. "Remote — Berlin") is dropped.
        bool remoteGeoOk = locMatch || RemoteFlexible(j);
        bool locOk = (profile.Remote && isRemote && remoteGeoOk)   // remote: PT / region-wide / unknown only
                  || (profile.Hybrid && isHybrid && locMatch)       // hybrid: must be commutable → our location
                  || (profile.Onsite && locMatch)                   // onsite: our location
                  || locMatch;                                       // any explicit match to our location → keep
        if (!locOk) return (false, 0, "", "");

        // Weighted pre-score with a breakdown.
        var parts = new List<string>();
        int score = matched.Count * 5;
        parts.Add($"{matched.Count} termo(s) ({Trunc(string.Join(", ", matched), 60)}) +{matched.Count * 5}");

        var coreHits = core.Where(s => WordIn(hay, s)).ToList();
        if (coreHits.Count > 0) { score += cfg.StackBonus; parts.Add($"competências-chave ({string.Join(", ", coreHits)}) +{cfg.StackBonus}"); }
        else { score -= cfg.OffStackPenalty; parts.Add($"sem competência-chave −{cfg.OffStackPenalty}"); }

        if (isRemote) { score += 12; parts.Add("remoto +12"); }
        else if (isHybrid) { score += 8; parts.Add("híbrido +8"); }
        if (locMatch) { score += 10; parts.Add("localização +10"); }

        int floor = profile.SalaryFloorEur > 0 ? profile.SalaryFloorEur : cfg.Salary.FloorEur;
        int target = profile.SalaryTargetEur > 0 ? profile.SalaryTargetEur : cfg.Salary.TargetEur;
        string salaryNote;
        if (j.SalaryAnnualEur is int eur)
        {
            if (eur >= target) { score += cfg.Salary.AboveTargetBoost; parts.Add($"salário ≥ alvo +{cfg.Salary.AboveTargetBoost}"); salaryNote = "paga ≥ o teu alvo"; }
            else if (eur < floor) { score -= cfg.Salary.BelowFloorPenalty; parts.Add($"salário < mínimo −{cfg.Salary.BelowFloorPenalty}"); salaryNote = "abaixo do teu mínimo"; }
            else { salaryNote = "salário no intervalo"; }
        }
        else { score -= cfg.Salary.NoSalaryPenalty; parts.Add($"sem salário −{cfg.Salary.NoSalaryPenalty}"); salaryNote = "sem salário indicado"; }

        int final = Math.Clamp(score, 1, 100);
        string explanation = string.Join(" · ", parts) + $"  =  {final}";

        // Deterministic "base" classification for keyword mode (no LLM), salary included.
        string tier = final >= 70 ? "Forte correspondência" : final >= 50 ? "Boa correspondência" : "Correspondência possível";
        var bits = new List<string> { coreHits.Count > 0 ? "usa competências-chave" : "sem competência-chave" };
        if (isRemote) bits.Add("remoto");
        else if (isHybrid) bits.Add("híbrido");
        if (locMatch) bits.Add("localização preferida");
        bits.Add(salaryNote);
        string baseVerdict = $"{tier} — {string.Join("; ", bits)}.";

        return (true, final, explanation, baseVerdict);
    }

    /// <summary>Region-wide / "open to anyone" location markers a Portugal-based candidate can take remotely.</summary>
    private static readonly string[] RegionWide =
        { "portugal", "europe", "european", "emea", "eea", "worldwide", "anywhere", "global" };

    /// <summary>True when a remote job's location is geographically flexible for this candidate: unknown, just
    /// "Remote", or a region-wide marker — NOT a specific foreign place. Strips the word "remote" first so
    /// "Remote — Berlin" is judged on "Berlin" (dropped), while "Remote · Portugal"/"EMEA"/"" are kept.</summary>
    private static bool RemoteFlexible(JobEntity j)
    {
        string loc = (j.Location ?? "").ToLowerInvariant()
            .Replace("remote", " ").Replace("remoto", " ")
            .Trim(' ', ',', '·', '-', '/', '(', ')', '|');
        if (loc.Length == 0) return true;                 // just "Remote" / unknown → flexible
        return RegionWide.Any(loc.Contains);              // region-wide → keep; a specific foreign place → drop
    }

    /// <summary>Over-generic words that shouldn't gate relevance on their own.</summary>
    private static readonly HashSet<string> Generic = new()
    {
        "and", "or", "the", "of", "for", "with", "in", "to", "a", "an",
    };

    /// <summary>Role words too generic to pin a role on their own — a title matching ONLY these (with no core
    /// skill present) is treated as off-stack. Distinctive skills like c#/.net/go are deliberately NOT here.</summary>
    private static readonly HashSet<string> GenericRole = new()
    {
        "engineer", "engineering", "developer", "development", "programmer", "coder", "software", "analyst",
        "specialist", "consultant", "manager", "management", "lead", "architect", "data", "cloud", "web",
        "fullstack", "full", "stack", "systems", "system", "it", "technology", "tech",
        "senior", "junior", "mid", "principal", "staff", "officer",
    };

    /// <summary>
    /// True if <paramref name="token"/> occurs in <paramref name="text"/> as a whole word
    /// (no alphanumeric neighbour). Handles tokens with symbols like "c#"/".net" since the
    /// boundary test only looks at letters/digits. Prevents "go" matching "good"/"category".
    /// Shared with <see cref="JobMarket"/> so skill matching is defined in one place.
    /// </summary>
    internal static bool WordIn(string text, string token)
    {
        int i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            int end = i + token.Length;
            bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftOk && rightOk) return true;
            i = end;
        }
        return false;
    }

    private static List<string> Lower(IEnumerable<string> xs)
        => xs.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()).ToList();

    private static IEnumerable<string> SplitWords(string s)
        => (s ?? "").Split(new[] { ' ', '/', ',', '&', '-' }, StringSplitOptions.RemoveEmptyEntries);

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    private static List<string> SeniorityExcludes(string target, AppConfig cfg)
    {
        var baseEx = Lower(cfg.SeniorityExclude);
        return (target?.ToLowerInvariant()) switch
        {
            "senior" => baseEx.Where(x => x is "intern" or "internship" or "estágio" or "junior" or "júnior" or "graduate").ToList(),
            "junior" => new() { "principal", "staff", "head of", "director", "manager", "lead" },
            _ => baseEx,
        };
    }
}
