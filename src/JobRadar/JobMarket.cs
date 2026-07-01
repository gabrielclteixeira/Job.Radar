using System.Text;

namespace JobRadar;

/// <summary>A scored job reduced to what corpus analysis needs (projected from the desktop `JobVm`).</summary>
public sealed record JobPosting(string Title, string Description, int Score, string Company, string Url);

/// <summary>How often one skill appears across the strong-fit jobs.</summary>
public sealed record SkillStat(string Skill, int Count, double Pct, bool IsCore);

/// <summary>A strong-fit job whose title matches one of the candidate's target roles.</summary>
public sealed record JobRef(string Title, string Company, int Score, string Url);

/// <summary>
/// Grounds the career plan in the user's OWN scored jobs instead of only web research. It's a best-effort
/// SIGNAL, never ground truth: the corpus can contain off-target jobs that slipped the filter ("poisoning"),
/// so this only looks at STRONG-FIT jobs (score ≥ threshold) and every number it produces is shown with its
/// evidence (how many jobs, which). Demand is derived by text-matching known skills against the job
/// title+description (there is no structured skill data), reusing <see cref="ProfileFilter.WordIn"/> so
/// "go" doesn't match "google" and "c#"/".net" match correctly.
/// </summary>
public sealed class JobMarketSignal
{
    public int StrongCount { get; }
    public int TotalCount { get; }
    /// <summary>Too few strong-fit jobs to trust the sample — the UI should caution the user.</summary>
    public bool Thin => StrongCount is > 0 and < 3;
    public bool HasData => StrongCount > 0;
    public IReadOnlyList<SkillStat> SkillDemand { get; }
    public IReadOnlyList<JobRef> RoleMatches { get; }
    public IReadOnlyList<JobPosting> Strong { get; }
    public bool HasSkillDemand => SkillDemand.Count > 0;
    public bool HasRoleMatches => RoleMatches.Count > 0;

    // Lowercased (full = title+desc, title only) haystacks of the strong-fit jobs, for FitDelta.
    private readonly List<(string full, string title)> _hay;

    internal JobMarketSignal(int total, IReadOnlyList<JobPosting> strong, IReadOnlyList<SkillStat> demand,
        IReadOnlyList<JobRef> roles, List<(string full, string title)> hay)
    {
        TotalCount = total; Strong = strong; StrongCount = strong.Count;
        SkillDemand = demand; RoleMatches = roles; _hay = hay;
    }

    public static readonly JobMarketSignal Empty =
        new(0, Array.Empty<JobPosting>(), Array.Empty<SkillStat>(), Array.Empty<JobRef>(), new());

    /// <summary>How many strong-fit jobs mention <paramref name="skill"/> — grades an arbitrary (LLM-proposed)
    /// gap against the user's real matched market. 0 means "not seen in your jobs" (web-only / possibly noise).</summary>
    public int FitDelta(string skill)
    {
        string s = (skill ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return 0;
        bool titleOnly = TitleOnly(s);
        int n = 0;
        foreach (var (full, title) in _hay)
            if (ProfileFilter.WordIn(titleOnly ? title : full, s)) n++;
        return n;
    }

    /// <summary>Compact, evidence-first block fed to the plan prompt under "JOBS THE APP ALREADY SCORED".
    /// States the sample is a signal (not authoritative) so the model doesn't over-fit to possibly-noisy jobs.</summary>
    public string ToMarketContext(int topSkills = 8, int topRoles = 5, int topEvidence = 3, int evidenceChars = 300)
    {
        if (!HasData) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"{StrongCount} strong-fit jobs (score>=70) out of {TotalCount} the radar scored for this candidate.");
        sb.AppendLine("This is a SIGNAL from the candidate's own matched market — not exhaustive or authoritative; it may include off-target jobs, so treat it as evidence, not proof.");
        if (SkillDemand.Count > 0)
            sb.AppendLine("Most-recurring skills across those jobs (skill: #jobs): "
                + string.Join(", ", SkillDemand.Take(topSkills).Select(d => $"{d.Skill}: {d.Count}")) + ".");
        if (RoleMatches.Count > 0)
            sb.AppendLine("Jobs matching the candidate's target roles: "
                + string.Join("; ", RoleMatches.Take(topRoles).Select(r => $"{r.Title} @ {r.Company}")) + ".");
        var ev = Strong.Take(topEvidence).ToList();
        if (ev.Count > 0)
        {
            sb.AppendLine("Verbatim requirement excerpts (evidence):");
            foreach (var j in ev)
                sb.AppendLine($"- [{j.Title} @ {j.Company}] {Trunc(j.Description, evidenceChars)}");
        }
        return sb.ToString();
    }

    private static bool TitleOnly(string skillLower)
        => skillLower.Length < 3 && !skillLower.Contains('#') && !skillLower.Contains('.') && !skillLower.Contains('+');

    private static string Trunc(string? s, int n)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > n ? s[..n] + "…" : s;
    }
}

/// <summary>Builds a <see cref="JobMarketSignal"/> from the scored jobs — pure, UI-free, deterministic.</summary>
public static class JobMarket
{
    public static JobMarketSignal Analyze(IReadOnlyList<JobPosting> jobs, UserProfile profile, int minScore = 70)
    {
        if (jobs is null || jobs.Count == 0 || profile is null) return JobMarketSignal.Empty;

        var strong = jobs.Where(j => j.Score >= minScore).ToList();
        if (strong.Count == 0)
            return new JobMarketSignal(jobs.Count, Array.Empty<JobPosting>(), Array.Empty<SkillStat>(), Array.Empty<JobRef>(), new());

        var hay = strong.Select(j => (
            full: $"{j.Title} {j.Description}".ToLowerInvariant(),
            title: (j.Title ?? "").ToLowerInvariant())).ToList();

        // Skill demand — count strong-fit jobs mentioning each of the candidate's OWN skills (all we can name).
        var core = new HashSet<string>(
            profile.CoreSkills.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var allSkills = profile.CoreSkills.Concat(profile.Skills)
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var demand = new List<SkillStat>();
        foreach (var s in allSkills)
        {
            string sl = s.ToLowerInvariant();
            bool titleOnly = sl.Length < 3 && !sl.Contains('#') && !sl.Contains('.') && !sl.Contains('+');
            int c = 0;
            foreach (var (full, title) in hay)
                if (ProfileFilter.WordIn(titleOnly ? title : full, sl)) c++;
            if (c > 0) demand.Add(new SkillStat(s, c, (double)c / strong.Count, core.Contains(s)));
        }
        demand = demand.OrderByDescending(d => d.Count).ThenByDescending(d => d.IsCore)
                       .ThenBy(d => d.Skill, StringComparer.OrdinalIgnoreCase).ToList();

        // Role matches — strong-fit jobs whose title carries a DISTINCTIVE word from the candidate's target roles.
        var roleWords = RoleWords(profile);
        var roles = new List<JobRef>();
        foreach (var j in strong.OrderByDescending(j => j.Score))
        {
            string tl = (j.Title ?? "").ToLowerInvariant();
            if (roleWords.Any(w => ProfileFilter.WordIn(tl, w)))
                roles.Add(new JobRef(j.Title, j.Company, j.Score, j.Url));
        }

        return new JobMarketSignal(jobs.Count, strong, demand, roles.Take(12).ToList(), hay);
    }

    /// <summary>Over-generic role words that shouldn't pin a role on their own (mirrors ProfileFilter.GenericRole).</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "engineer", "engineering", "developer", "development", "programmer", "coder", "software", "analyst",
        "specialist", "consultant", "manager", "management", "lead", "architect", "data", "cloud", "web",
        "fullstack", "full", "stack", "systems", "system", "it", "technology", "tech",
        "senior", "junior", "mid", "principal", "staff", "officer",
    };

    /// <summary>Distinctive words from the target job titles (drop generic role words). Falls back to the full
    /// title phrases when every word is generic, so a "Software Engineer"-only profile still matches something.</summary>
    private static List<string> RoleWords(UserProfile p)
    {
        var words = new List<string>();
        foreach (var t in p.JobTitles.Concat(p.RoleQueries()))
            foreach (var w in (t ?? "").ToLowerInvariant().Split(new[] { ' ', '/', ',', '&', '-' }, StringSplitOptions.RemoveEmptyEntries))
                if (w.Length > 1 && !Generic.Contains(w) && !words.Contains(w)) words.Add(w);
        if (words.Count == 0)
            foreach (var t in p.JobTitles)
            {
                string tl = (t ?? "").Trim().ToLowerInvariant();
                if (tl.Length > 1 && !words.Contains(tl)) words.Add(tl);
            }
        return words;
    }
}
