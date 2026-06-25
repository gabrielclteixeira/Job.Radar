namespace JobRadar;

/// <summary>Raw job exactly as emitted by the Go fetcher (stdout JSON).</summary>
public record RawJob(
    string Title, string Company, string Location, string Remote,
    string Url, string Description, string Source, string PostedAt,
    double SalaryMin = 0, double SalaryMax = 0, string SalaryCurrency = "");

/// <summary>A job scraped manually from LinkedIn (linkedin-jobs.json).</summary>
public record LinkedInJob(string? Id, string? Title, string? Company, string? Location, string? Url, string? Description = null);

/// <summary>Persisted job + analysis. Stored in SQLite via EF Core.</summary>
public class JobEntity
{
    public int Id { get; set; }
    public string Key { get; set; } = "";          // dedupe key (url, or title|company)
    public string Title { get; set; } = "";
    public string Company { get; set; } = "";
    public string Location { get; set; } = "";
    public string Remote { get; set; } = "";        // remote | hybrid | onsite | ""
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string Source { get; set; } = "";
    public string PostedAt { get; set; } = "";
    public DateTime FirstSeen { get; set; }

    public double SalaryMin { get; set; }
    public double SalaryMax { get; set; }
    public string SalaryCurrency { get; set; } = "";
    public int? SalaryAnnualEur { get; set; }   // normalized; null if unknown
    public string SalaryText { get; set; } = ""; // human-readable, e.g. "€45k–€60k"

    public bool Relevant { get; set; }
    public int PreScore { get; set; }               // cheap keyword score (0-100)
    public string? PreScoreExplanation { get; set; } // human-readable breakdown of the keyword score
    public int? AiScore { get; set; }               // Claude fit score (0-100)
    public string? AiVerdict { get; set; }
    public string? AiReasons { get; set; }          // JSON array string
    public string? AiRedFlags { get; set; }         // JSON array string
    public string Status { get; set; } = "new";     // new | seen | applied | dismissed

    public int FinalScore => AiScore ?? PreScore;
}

/// <summary>App configuration (appsettings.json at the project root).</summary>
public class AppConfig
{
    public string RawJobsPath { get; set; } = "jobs.raw.json";
    public string LinkedInJobsPath { get; set; } = "linkedin-jobs.json"; // optional manual LinkedIn pass
    public string DbPath { get; set; } = "radar.db";
    public string OutputDir { get; set; } = "output";
    public string ProfilePath { get; set; } = "profile.md";

    public List<string> IncludeKeywords { get; set; } = new();
    public List<string> StrongKeywords { get; set; } = new();
    public List<string> StackKeywords { get; set; } = new(); // C#/.NET/Go — the stack that really matters
    public int StackBonus { get; set; } = 30;                // pre-score boost when stack matches
    public int OffStackPenalty { get; set; } = 22;           // pre-score penalty when relevant but off-stack
    public List<string> LocationKeywords { get; set; } = new();
    public List<string> ExcludeKeywords { get; set; } = new();
    public List<string> SeniorityExclude { get; set; } = new();

    public int ScoreTopN { get; set; } = 25;
    public string ScoringMode { get; set; } = "ai"; // "ai" | "keywords"
    public ClaudeConfig Claude { get; set; } = new();
    public SalaryConfig Salary { get; set; } = new();
}

public class SalaryConfig
{
    public int FloorEur { get; set; } = 45000;
    public int TargetEur { get; set; } = 65000;
    public int AboveTargetBoost { get; set; } = 15;  // + to pre-score when >= target
    public int BelowFloorPenalty { get; set; } = 20; // - to pre-score when < floor
    public int NoSalaryPenalty { get; set; } = 3;    // - when no salary is listed
    public double UsdToEur { get; set; } = 0.92;
    public double GbpToEur { get; set; } = 1.17;
    public int MonthsPerYear { get; set; } = 12;     // for monthly figures (conservative)
}

public class ClaudeConfig
{
    public bool Enabled { get; set; } = true;
    public string Exe { get; set; } = "claude";
    public int TimeoutSeconds { get; set; } = 90;
}

public record AiResult(int Score, string Verdict, string[] Reasons, string[] RedFlags);
