using System.Text.Json;

namespace JobRadar;

/// <summary>
/// The structured CV document behind CV Studio — everything the templates render and the AI chat
/// edits. Plain JSON-friendly classes (all lists initialised) persisted to cv-data.json
/// (machine-local, gitignored: personal data). Lang/TemplateId/AccentColor are presentation
/// settings owned by the app; the AI is never allowed to change the visual ones.
/// </summary>
public sealed class CvDocument
{
    public string Lang { get; set; } = "pt";            // "pt" | "en" — CV output language (≠ UI language)
    public string TemplateId { get; set; } = "clean";   // "clean" | "accent"
    public string AccentColor { get; set; } = "#4C2DBE";
    public string TailoredFor { get; set; } = "";       // company name when tuned to a job ("" = generic)
    public CvHeader Header { get; set; } = new();
    public string Summary { get; set; } = "";
    public List<CvExperience> Experience { get; set; } = new();
    public List<CvEducation> Education { get; set; } = new();
    public List<CvProject> Projects { get; set; } = new();
    public List<string> Certifications { get; set; } = new();
    public List<CvSkillGroup> SkillGroups { get; set; } = new();
    public List<string> Languages { get; set; } = new();
}

public sealed class CvHeader
{
    public string FullName { get; set; } = "";
    public string Title { get; set; } = "";             // professional headline under the name
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Location { get; set; } = "";
    public List<CvLink> Links { get; set; } = new();
}

public sealed class CvLink
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class CvExperience
{
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Start { get; set; } = "";             // free-form, kept as written ("Jan 2020")
    public string End { get; set; } = "";               // "" or "Present"/"Presente" → rendered as such
    public string Location { get; set; } = "";
    public List<string> Bullets { get; set; } = new();
}

public sealed class CvEducation
{
    public string School { get; set; } = "";
    public string Degree { get; set; } = "";
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public string Location { get; set; } = "";
    public List<string> Details { get; set; } = new();
}

public sealed class CvProject
{
    public string Name { get; set; } = "";
    public string Link { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Bullets { get; set; } = new();
}

public sealed class CvSkillGroup
{
    public string Label { get; set; } = "";             // "" = unlabelled group
    public List<string> Skills { get; set; } = new();
}

/// <summary>Load/save for the CV document (mirrors the CompanyCache pattern).</summary>
public static class CvStore
{
    private static readonly JsonSerializerOptions J = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JCase = new() { PropertyNameCaseInsensitive = true };

    public static CvDocument? Load(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<CvDocument>(File.ReadAllText(path), JCase);
            if (doc is null) return null;
            Normalize(doc);
            return doc;
        }
        catch { return null; }   // corrupt file → treated as "no CV yet"
    }

    public static void Save(string? path, CvDocument doc)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.WriteAllText(path, JsonSerializer.Serialize(doc, J)); }
        catch { /* best-effort */ }
    }

    /// <summary>Repairs a document coming from disk or from the LLM: null lists/objects become
    /// empty, and the presentation fields are clamped to known-valid values.</summary>
    public static void Normalize(CvDocument d)
    {
        d.Header ??= new CvHeader();
        d.Header.Links ??= new List<CvLink>();
        d.Header.Links.RemoveAll(l => l is null);
        d.Experience ??= new List<CvExperience>();
        d.Experience.RemoveAll(e => e is null);
        foreach (var e in d.Experience) e.Bullets ??= new List<string>();
        d.Education ??= new List<CvEducation>();
        d.Education.RemoveAll(e => e is null);
        foreach (var e in d.Education) e.Details ??= new List<string>();
        d.Projects ??= new List<CvProject>();
        d.Projects.RemoveAll(p => p is null);
        foreach (var p in d.Projects) p.Bullets ??= new List<string>();
        d.Certifications ??= new List<string>();
        d.SkillGroups ??= new List<CvSkillGroup>();
        d.SkillGroups.RemoveAll(g => g is null);
        foreach (var g in d.SkillGroups) g.Skills ??= new List<string>();
        d.Languages ??= new List<string>();
        d.Summary ??= "";
        d.TailoredFor ??= "";
        if (d.Lang is not ("pt" or "en")) d.Lang = "pt";
        if (!System.Text.RegularExpressions.Regex.IsMatch(d.AccentColor ?? "", "^#[0-9a-fA-F]{6}$"))
            d.AccentColor = "#4C2DBE";
        if (d.TemplateId is not ("clean" or "accent" or "sidebar")) d.TemplateId = "clean";
    }
}
