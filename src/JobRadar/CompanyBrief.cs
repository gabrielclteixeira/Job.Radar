namespace JobRadar;

/// <summary>Structured employer briefing rendered with colour-coded sections in the UI.</summary>
public class CompanyBrief
{
    public List<string> Pros { get; set; } = new();
    public List<string> Cons { get; set; } = new();
    public List<string> Context { get; set; } = new();
    public string ReputationNote { get; set; } = "";
    public string SalaryFound { get; set; } = "";
    public string SalaryExpectation { get; set; } = "";   // tailored to this candidate's level
    public string SalaryRationale { get; set; } = "";
    public string BottomLine { get; set; } = "";
    public string RawFallback { get; set; } = "";          // set when JSON parsing failed
    public List<SourceRef> Sources { get; set; } = new();

    public bool HasPros => Pros.Count > 0;
    public bool HasCons => Cons.Count > 0;
    public bool HasContext => Context.Count > 0;
    public bool HasReputationNote => !string.IsNullOrWhiteSpace(ReputationNote);
    public bool HasReputation => HasPros || HasCons || HasContext || HasReputationNote;
    public bool HasSalaryFound => !string.IsNullOrWhiteSpace(SalaryFound);
    public bool HasSalaryExpectation => !string.IsNullOrWhiteSpace(SalaryExpectation);
    public bool HasSalaryRationale => !string.IsNullOrWhiteSpace(SalaryRationale);
    public bool HasSalary => HasSalaryFound || HasSalaryExpectation;
    public bool HasBottomLine => !string.IsNullOrWhiteSpace(BottomLine);
    public bool HasFallback => !string.IsNullOrWhiteSpace(RawFallback);
    public bool HasSources => Sources.Count > 0;
}

public class SourceRef
{
    public int N { get; set; }
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public string Label => $"[{N}]  {Host}";
}
