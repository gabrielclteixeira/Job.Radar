using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JobRadar;

/// <summary>
/// Turns the career plan into a LIVING document: a compact, persisted snapshot of each generated plan (so the
/// user keeps a growth history) and a plan-to-plan <see cref="PlanDiff"/> that surfaces what changed since last
/// time — gaps closed, new gaps, roles added/dropped and which way the salary bands moved. Pure logic; the
/// ViewModel owns the file I/O (a <c>List&lt;PlanSnapshot&gt;</c> beside the active plan).
/// </summary>
public sealed class PlanSnapshot
{
    public string SavedUtc { get; set; } = "";
    public string Headline { get; set; } = "";
    public string SalaryNow { get; set; } = "";
    public string SalaryPotential { get; set; } = "";
    public List<string> TargetRoles { get; set; } = new();
    public List<string> Gaps { get; set; } = new();   // skill names only — enough to diff + show
    public int DoneCount { get; set; }
    public int TotalCount { get; set; }

    [JsonIgnore] public string AsOfDate => PlanText.AsOfDate(SavedUtc);
    [JsonIgnore] public string ProgressText => TotalCount > 0 ? $"{DoneCount}/{TotalCount}" : "";
    [JsonIgnore] public bool HasProgress => TotalCount > 0;
    [JsonIgnore] public string SalaryLine =>
        string.Join(" → ", new[] { SalaryNow, SalaryPotential }.Where(s => !string.IsNullOrWhiteSpace(s)));
    [JsonIgnore] public bool HasSalaryLine => !string.IsNullOrWhiteSpace(SalaryLine);

    public static PlanSnapshot From(CareerPlanResult p) => new()
    {
        SavedUtc = string.IsNullOrWhiteSpace(p.SavedUtc) ? DateTime.UtcNow.ToString("o") : p.SavedUtc,
        Headline = p.Headline,
        SalaryNow = p.SalaryNow,
        SalaryPotential = p.SalaryPotential,
        TargetRoles = new List<string>(p.TargetRoles),
        Gaps = p.SkillGaps.Select(g => g.Skill).Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
        DoneCount = p.DoneCount,
        TotalCount = p.TrackableCount,
    };
}

/// <summary>What changed between the previous plan and the freshly generated one.</summary>
public sealed class PlanDiff
{
    public string PrevDate { get; set; } = "";
    public List<string> GapsClosed { get; set; } = new();   // were gaps last time, gone now (progress!)
    public List<string> GapsNew { get; set; } = new();      // new gaps this time
    public List<string> RolesAdded { get; set; } = new();
    public List<string> RolesDropped { get; set; } = new();
    public int SalaryNowDir { get; set; }                    // -1 down / 0 same-or-unknown / +1 up
    public int SalaryPotentialDir { get; set; }
    public string SalaryNowBefore { get; set; } = "";
    public string SalaryNowAfter { get; set; } = "";
    public string SalaryPotentialBefore { get; set; } = "";
    public string SalaryPotentialAfter { get; set; } = "";

    public bool HasGapsClosed => GapsClosed.Count > 0;
    public bool HasGapsNew => GapsNew.Count > 0;
    public bool HasRolesAdded => RolesAdded.Count > 0;
    public bool HasRolesDropped => RolesDropped.Count > 0;
    public bool HasSalaryNowMove => SalaryNowDir != 0;
    public bool HasSalaryPotentialMove => SalaryPotentialDir != 0;
    public bool HasChanges => HasGapsClosed || HasGapsNew || HasRolesAdded || HasRolesDropped
                              || HasSalaryNowMove || HasSalaryPotentialMove;

    public string GapsClosedText => string.Join(", ", GapsClosed);
    public string GapsNewText => string.Join(", ", GapsNew);
    public string RolesAddedText => string.Join(", ", RolesAdded);
    public string RolesDroppedText => string.Join(", ", RolesDropped);
    public string SalaryNowArrow => SalaryNowDir > 0 ? "▲" : SalaryNowDir < 0 ? "▼" : "";
    public string SalaryPotentialArrow => SalaryPotentialDir > 0 ? "▲" : SalaryPotentialDir < 0 ? "▼" : "";

    /// <summary>Builds the diff of <paramref name="now"/> against the <paramref name="prev"/> snapshot.
    /// Comparison is case-insensitive and whitespace-normalized; returns an all-empty diff when prev is null.</summary>
    public static PlanDiff Between(PlanSnapshot? prev, CareerPlanResult now)
    {
        var d = new PlanDiff();
        if (prev is null) return d;
        d.PrevDate = prev.AsOfDate;

        var prevGaps = Keyed(prev.Gaps);
        var nowGaps = Keyed(now.SkillGaps.Select(g => g.Skill));
        d.GapsClosed = prev.Gaps.Where(g => !nowGaps.Contains(Key(g))).Distinct().ToList();
        d.GapsNew = now.SkillGaps.Select(g => g.Skill).Where(g => !string.IsNullOrWhiteSpace(g) && !prevGaps.Contains(Key(g))).Distinct().ToList();

        var prevRoles = Keyed(prev.TargetRoles);
        var nowRoles = Keyed(now.TargetRoles);
        d.RolesAdded = now.TargetRoles.Where(r => !prevRoles.Contains(Key(r))).Distinct().ToList();
        d.RolesDropped = prev.TargetRoles.Where(r => !nowRoles.Contains(Key(r))).Distinct().ToList();

        (d.SalaryNowDir, d.SalaryNowBefore, d.SalaryNowAfter) = Compare(prev.SalaryNow, now.SalaryNow);
        (d.SalaryPotentialDir, d.SalaryPotentialBefore, d.SalaryPotentialAfter) = Compare(prev.SalaryPotential, now.SalaryPotential);
        return d;
    }

    private static (int dir, string before, string after) Compare(string before, string after)
    {
        int? a = PlanText.FirstEur(before), b = PlanText.FirstEur(after);
        if (a is null || b is null || a == b) return (0, before, after);
        return (b > a ? 1 : -1, before, after);
    }

    private static HashSet<string> Keyed(IEnumerable<string> xs) =>
        new(xs.Select(Key).Where(k => k.Length > 0), StringComparer.Ordinal);

    /// <summary>Normalization used to match items across regenerations (so "React.js " and "react.js" are one).</summary>
    public static string Key(string s) =>
        Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"\s+", " ");
}

/// <summary>Small text helpers shared by the snapshot/diff (date + EUR-figure parsing).</summary>
internal static class PlanText
{
    /// <summary>A short local date ("2026-07-01") from a round-trip ISO stamp; the raw string if unparseable.</summary>
    public static string AsOfDate(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return DateTime.TryParse(iso, null, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd") : iso;
    }

    /// <summary>The first EUR figure in a band string as a plain number, handling "€35k", "35.000", "35 000",
    /// "€35,000", "35K". Returns null when no figure is present. Used only to decide which way a band moved.</summary>
    public static int? FirstEur(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"(\d[\d\s.,]*)\s*(k|K)?");
        if (!m.Success) return null;
        string digits = Regex.Replace(m.Groups[1].Value, @"[\s.,]", "");
        if (!int.TryParse(digits, out int n) || n <= 0) return null;
        bool k = m.Groups[2].Value.Length > 0;
        if (k) n *= 1000;
        // "35" meaning 35k is common in shorthand bands; treat small figures as thousands.
        else if (n < 1000) n *= 1000;
        return n;
    }
}
