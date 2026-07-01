using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

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

    // Living document: when the plan was generated (round-trip ISO), for the "as of" label, history and diff.
    public string SavedUtc { get; set; } = "";

    // Adversarial self-critique: a second pass red-teams the plan so the user calibrates trust.
    public List<CritiquePoint> Critique { get; set; } = new();
    public string CritiqueCaveat { get; set; } = "";       // one-line "this is AI, question it" framing
    public bool Revised { get; set; }                       // true when the plan was rewritten from the critique

    // ---- progress (the plan as a checklist you work through) ----
    /// <summary>The trackable actions the user ticks off: every skill gap plus every next step.</summary>
    public int TrackableCount => SkillGaps.Count + Steps.Count;
    public int DoneCount => SkillGaps.Count(g => g.Done) + Steps.Count(s => s.Done);
    public bool HasTrackable => TrackableCount > 0;
    public double ProgressFraction => TrackableCount == 0 ? 0 : (double)DoneCount / TrackableCount;
    public int ProgressPercent => (int)System.Math.Round(ProgressFraction * 100);
    public bool IsComplete => TrackableCount > 0 && DoneCount == TrackableCount;
    /// <summary>Human labels of every completed item — reinjected into the next plan so it builds forward.</summary>
    public IEnumerable<string> DoneLabels =>
        SkillGaps.Where(g => g.Done).Select(g => g.Skill)
            .Concat(Steps.Where(s => s.Done).Select(s => s.Title))
            .Where(s => !string.IsNullOrWhiteSpace(s));

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
    public bool HasCritique => Critique.Count > 0;
    public bool HasCaveat => !string.IsNullOrWhiteSpace(CritiqueCaveat);
}

/// <summary>One adversarial objection to the plan: the claim under scrutiny, the flaw, and (in debate
/// modes) the defender's one-line response.</summary>
public class CritiquePoint
{
    public string Claim { get; set; } = "";   // the plan claim being challenged
    public string Issue { get; set; } = "";    // the attacker's objection
    public string Rebuttal { get; set; } = ""; // defender's response (debate / revise modes)
    public bool HasRebuttal => !string.IsNullOrWhiteSpace(Rebuttal);
}

/// <summary>A capability to build, why it matters, and a concrete way to close it.
/// <see cref="Done"/> raises change notification so the checklist UI + progress bar stay live.</summary>
public class SkillGap : INotifyPropertyChanged
{
    public string Skill { get; set; } = "";
    public string Why { get; set; } = "";
    public string Action { get; set; } = "";
    private bool _done;
    public bool Done { get => _done; set { if (_done != value) { _done = value; OnChanged(); } } }
    /// <summary>How many of the user's strong-fit jobs mention this skill (set by the VM from the corpus signal).
    /// >0 = grounded in the real matched market; 0 = web-only (flagged honestly in the UI). Notifies so the
    /// grounding chip refreshes when the corpus is re-analysed.</summary>
    private int _corpusHits;
    public int CorpusHits { get => _corpusHits; set { if (_corpusHits != value) { _corpusHits = value; OnChanged(); OnChanged(nameof(HasCorpusHits)); OnChanged(nameof(CorpusLabel)); } } }
    public bool HasWhy => !string.IsNullOrWhiteSpace(Why);
    public bool HasAction => !string.IsNullOrWhiteSpace(Action);
    public bool HasCorpusHits => CorpusHits > 0;
    /// <summary>Localized grounding chip text, e.g. "em 12 das tuas vagas" (shown only when <see cref="HasCorpusHits"/>).</summary>
    public string CorpusLabel => Loc.Instance.F("improve.gap.grounded", CorpusHits);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>One time-boxed step in the plan. <see cref="Done"/> is a tickable checklist item.</summary>
public class PlanStep : INotifyPropertyChanged
{
    public string Horizon { get; set; } = "";  // e.g. "0–3 meses"
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    private bool _done;
    public bool Done { get => _done; set { if (_done != value) { _done = value; OnChanged(); } } }
    public bool HasHorizon => !string.IsNullOrWhiteSpace(Horizon);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
