namespace JobRadar;

/// <summary>
/// Structured career-growth plan, rendered with colour-coded sections in the UI.
/// Built by <see cref="CareerPlan"/> from the candidate profile + multi-step web research.
/// </summary>
public class CareerPlanResult
{
    public string Headline { get; set; } = "";            // one-line positioning for this candidate
    public List<string> Strengths { get; set; } = new();  // what's already working (green)
    public List<SkillGap> SkillGaps { get; set; } = new();// what to close to reach the target level
    public List<string> TargetRoles { get; set; } = new();// realistic next roles
    public string SalaryNow { get; set; } = "";           // realistic band today
    public string SalaryPotential { get; set; } = "";     // band reachable in 12–24 months with the plan
    public string SalaryRationale { get; set; } = "";     // how the bands were derived
    public List<PlanStep> Steps { get; set; } = new();    // concrete next steps, time-boxed
    public List<string> MarketSignals { get; set; } = new(); // in-demand skills / hiring trends found
    public string BottomLine { get; set; } = "";
    public string RawFallback { get; set; } = "";          // set when JSON parsing failed
    public List<SourceRef> Sources { get; set; } = new();

    public bool HasHeadline => !string.IsNullOrWhiteSpace(Headline);
    public bool HasStrengths => Strengths.Count > 0;
    public bool HasSkillGaps => SkillGaps.Count > 0;
    public bool HasTargetRoles => TargetRoles.Count > 0;
    public bool HasSalaryNow => !string.IsNullOrWhiteSpace(SalaryNow);
    public bool HasSalaryPotential => !string.IsNullOrWhiteSpace(SalaryPotential);
    public bool HasSalaryRationale => !string.IsNullOrWhiteSpace(SalaryRationale);
    public bool HasSalary => HasSalaryNow || HasSalaryPotential;
    public bool HasSteps => Steps.Count > 0;
    public bool HasMarketSignals => MarketSignals.Count > 0;
    public bool HasBottomLine => !string.IsNullOrWhiteSpace(BottomLine);
    public bool HasFallback => !string.IsNullOrWhiteSpace(RawFallback);
    public bool HasSources => Sources.Count > 0;
}

/// <summary>A capability to build, why it matters, and a concrete way to close it.</summary>
public class SkillGap
{
    public string Skill { get; set; } = "";
    public string Why { get; set; } = "";
    public string Action { get; set; } = "";
    public bool HasWhy => !string.IsNullOrWhiteSpace(Why);
    public bool HasAction => !string.IsNullOrWhiteSpace(Action);
}

/// <summary>One time-boxed step in the plan.</summary>
public class PlanStep
{
    public string Horizon { get; set; } = "";  // e.g. "0–3 meses"
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool HasHorizon => !string.IsNullOrWhiteSpace(Horizon);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}
