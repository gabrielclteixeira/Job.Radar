using System.Text;

namespace JobRadar;

/// <summary>
/// Field-agnostic candidate profile that drives search, filtering and scoring.
/// Built from a CV (via <see cref="CvProfiler"/>) and refined by the user.
/// Works for any profession — not just software.
/// </summary>
public class UserProfile
{
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";

    /// <summary>The professional field/area, e.g. "Software Engineering", "Nursing", "Accounting".</summary>
    public string Field { get; set; } = "";

    /// <summary>Job titles to search for, e.g. ["Backend Developer"] or ["Registered Nurse","ICU Nurse"].</summary>
    public List<string> JobTitles { get; set; } = new();

    /// <summary>Core skills/competencies that most determine fit (most important first).</summary>
    public List<string> CoreSkills { get; set; } = new();

    /// <summary>Broader skills/tools/domains.</summary>
    public List<string> Skills { get; set; } = new();

    public int YearsExperience { get; set; }
    public string SeniorityTarget { get; set; } = "mid"; // junior | mid | senior

    public List<string> Locations { get; set; } = new();
    public bool Remote { get; set; } = true;
    public bool Hybrid { get; set; } = true;
    public bool Onsite { get; set; } = false;

    public int SalaryFloorEur { get; set; } = 0;
    public int SalaryTargetEur { get; set; } = 0;

    public List<string> MustHaves { get; set; } = new();
    public List<string> DealBreakers { get; set; } = new();
    public List<string> Languages { get; set; } = new();

    /// <summary>Readable block fed to the AI scorer.</summary>
    public string ToScoringText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {Name}");
        if (!string.IsNullOrWhiteSpace(Field)) sb.AppendLine($"Field / profession: {Field}");
        if (!string.IsNullOrWhiteSpace(Summary)) sb.AppendLine($"Summary: {Summary}");
        sb.AppendLine($"Core skills (what matters most): {string.Join(", ", CoreSkills)}");
        if (Skills.Count > 0) sb.AppendLine($"Other skills: {string.Join(", ", Skills)}");
        if (JobTitles.Count > 0) sb.AppendLine($"Target job titles: {string.Join(", ", JobTitles)}");
        sb.AppendLine($"Experience: ~{YearsExperience} years (target level: {SeniorityTarget})");
        sb.AppendLine($"Locations: {string.Join(", ", Locations)}; remote={Remote}, hybrid={Hybrid}, onsite={Onsite}");
        if (SalaryFloorEur > 0 || SalaryTargetEur > 0)
            sb.AppendLine($"Salary: floor €{SalaryFloorEur:N0}, target €{SalaryTargetEur:N0} / year");
        if (MustHaves.Count > 0) sb.AppendLine($"Must-haves: {string.Join(", ", MustHaves)}");
        if (DealBreakers.Count > 0) sb.AppendLine($"Deal-breakers: {string.Join(", ", DealBreakers)}");
        if (Languages.Count > 0) sb.AppendLine($"Spoken languages: {string.Join(", ", Languages)}");
        return sb.ToString();
    }

    /// <summary>Search queries for the fetcher: job titles first, then field, then top skills.</summary>
    public List<string> SearchQueries()
    {
        var q = new List<string>();
        q.AddRange(JobTitles.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(Field)) q.Add(Field);
        q.AddRange(CoreSkills.Take(3));
        var cleaned = q.Select(s => s.Trim()).Where(s => s.Length > 1)
                       .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        return cleaned.Count > 0 ? cleaned : new List<string> { "software developer" };
    }

    /// <summary>Role-focused queries for the job FETCHERS — the candidate's job TITLES only (not loose skills or
    /// the broad field), so sources return the target role rather than anything that merely mentions a skill.
    /// Falls back to <see cref="SearchQueries"/> when no titles are set. (CareerPlan research still uses the
    /// broader SearchQueries on purpose.)</summary>
    public List<string> RoleQueries()
    {
        var titles = JobTitles.Select(s => s.Trim()).Where(s => s.Length > 1)
                              .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        return titles.Count > 0 ? titles : SearchQueries();
    }

    private static readonly string[] TechHints =
        { "software", "developer", "engineer", "programmer", ".net", "c#", "golang", " go", "java", "python",
          "javascript", "typescript", "data", "devops", "frontend", "backend", "full stack", "cloud", "qa", "sre" };

    /// <summary>Whether this looks like a tech profile (controls tech-only job boards).</summary>
    public bool IsTechField()
    {
        string hay = (Field + " " + string.Join(" ", CoreSkills) + " " + string.Join(" ", JobTitles)).ToLowerInvariant();
        return TechHints.Any(h => hay.Contains(h));
    }
}
