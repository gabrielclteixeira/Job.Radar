namespace JobRadar;

/// <summary>
/// Cheap, deterministic relevance gate + pre-score, driven by the user's
/// <see cref="UserProfile"/> — field-agnostic (works for any profession).
/// Also returns a human-readable explanation of how the score was built.
/// </summary>
public static class ProfileFilter
{
    public static (bool relevant, int preScore, string explanation) Evaluate(JobEntity j, UserProfile profile, AppConfig cfg)
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
            if (ex.Length > 1 && hay.Contains(ex)) return (false, 0, "");
        foreach (var sx in seniority)
            if (title.Contains(sx)) return (false, 0, "");

        if (tokens.Count == 0) return (false, 0, ""); // no profile yet

        // Relevance gate: a keyword must appear in the TITLE (word boundary), not buried in the
        // noisy description — otherwise short tokens like "go" match "good"/"category" everywhere.
        var matched = tokens.Where(t => WordIn(title, t)).ToList();
        if (matched.Count == 0) return (false, 0, "");

        // Location gate honouring the user's prefs.
        bool isRemote = j.Remote == "remote" || hay.Contains("remote") || hay.Contains("remoto");
        bool isHybrid = j.Remote == "hybrid" || hay.Contains("hybrid") || hay.Contains("híbrido");
        bool locMatch = locations.Any(l => l.Length > 1 && titleLoc.Contains(l));
        bool locOk = (profile.Remote && isRemote) || (profile.Hybrid && isHybrid) || locMatch || (profile.Onsite && locMatch);
        if (!locOk) return (false, 0, "");

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
        if (j.SalaryAnnualEur is int eur)
        {
            if (eur >= target) { score += cfg.Salary.AboveTargetBoost; parts.Add($"salário ≥ alvo +{cfg.Salary.AboveTargetBoost}"); }
            else if (eur < floor) { score -= cfg.Salary.BelowFloorPenalty; parts.Add($"salário < mínimo −{cfg.Salary.BelowFloorPenalty}"); }
        }
        else { score -= cfg.Salary.NoSalaryPenalty; parts.Add($"sem salário −{cfg.Salary.NoSalaryPenalty}"); }

        int final = Math.Clamp(score, 1, 100);
        string explanation = string.Join(" · ", parts) + $"  =  {final}";
        return (true, final, explanation);
    }

    /// <summary>Over-generic words that shouldn't gate relevance on their own.</summary>
    private static readonly HashSet<string> Generic = new()
    {
        "and", "or", "the", "of", "for", "with", "in", "to", "a", "an",
    };

    /// <summary>
    /// True if <paramref name="token"/> occurs in <paramref name="text"/> as a whole word
    /// (no alphanumeric neighbour). Handles tokens with symbols like "c#"/".net" since the
    /// boundary test only looks at letters/digits. Prevents "go" matching "good"/"category".
    /// </summary>
    private static bool WordIn(string text, string token)
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
