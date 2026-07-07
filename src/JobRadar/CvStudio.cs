using System.Text;
using System.Text.Json;

namespace JobRadar;

/// <summary>
/// CV Studio core (no UI): imports a full structured <see cref="CvDocument"/> from CV text via the
/// LLM, seeds one from the saved profile, and powers the in-view assistant chat that critiques the
/// CV and RETURNS EDITS — the model answers <c>{"reply":"...","cv":{...}|null}</c> and the caller
/// applies a valid "cv" with an undo snapshot. Facts are sacred: both prompts forbid inventing
/// employers, dates, titles, metrics or skills.
/// </summary>
public static class CvStudio
{
    private static readonly JsonSerializerOptions JCase = new() { PropertyNameCaseInsensitive = true };
    private const int ImportCharCap = 16_000;
    private const int JobBlockCap = 4_000;
    private const int PromptCap = 20_000;   // stay well inside the Claude CLI argv budget

    /// <summary>Config clone with enough output budget for a full CV JSON echo (never mutates the shared instance).</summary>
    public static ClaudeConfig WithCvBudget(ClaudeConfig cfg) => new()
    {
        Enabled = cfg.Enabled,
        Provider = cfg.Provider,
        Exe = cfg.Exe,
        BaseUrl = cfg.BaseUrl,
        Model = cfg.Model,
        ApiKey = cfg.ApiKey,
        TimeoutSeconds = cfg.TimeoutSeconds,
        MaxTokens = Math.Max(cfg.MaxTokens, 8192),
    };

    /// <summary>Extracts the FULL structured CV from raw PDF text. Null on failure.</summary>
    public static async Task<CvDocument?> ImportAsync(string cvText, UserProfile profile, ClaudeConfig cfg, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cvText)) return null;
        string text = cvText.Length > ImportCharCap ? cvText[..ImportCharCap] : cvText;
        string prompt =
$@"Extract the candidate's FULL CV from the text below into structured JSON — for ANY profession.
Reply with ONLY one valid JSON object (double-quoted keys/strings, no prose, no markdown fences), exactly this shape:
{{""header"":{{""fullName"":""..."",""title"":""professional headline"",""email"":""..."",""phone"":""..."",""location"":""City, Country"",""links"":[{{""label"":""LinkedIn"",""url"":""https://...""}}]}},
""summary"":""2-4 sentence professional summary"",
""experience"":[{{""company"":""..."",""role"":""..."",""start"":""Jan 2020"",""end"":""Present"",""location"":""..."",""bullets"":[""one achievement or responsibility per bullet""]}}],
""education"":[{{""school"":""..."",""degree"":""..."",""start"":""2014"",""end"":""2017"",""location"":""..."",""details"":[""honours, thesis, relevant coursework""]}}],
""projects"":[{{""name"":""..."",""link"":""https://... or empty"",""description"":""one line"",""bullets"":[""...""]}}],
""certifications"":[""name (issuer, year)""],
""skillGroups"":[{{""label"":""e.g. Languages / Tools / Methods"",""skills"":[""...""]}}],
""languages"":[""Portuguese (native)"",""English (C1)""]}}
Rules:
- Copy facts EXACTLY as written — never invent employers, dates, titles, numbers or skills.
- Keep the CV's own ordering (most recent first if the CV does) and its own date format; keep ""Present""/""Presente"" as written.
- Split multi-sentence blobs into separate bullets; keep each bullet under ~30 words.
- If something is absent from the CV, use """" or [].

== CV TEXT ==
{text}";

        string? raw = await LlmClient.CompleteAsync(WithCvBudget(cfg), prompt, ct, json: true);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        CvDocument? doc;
        try { doc = JsonSerializer.Deserialize<CvDocument>(raw[a..(b + 1)], JCase); }
        catch { return null; }
        if (doc is null) return null;
        CvStore.Normalize(doc);

        // Fallbacks from the saved profile for anything the extraction missed.
        if (string.IsNullOrWhiteSpace(doc.Header.FullName)) doc.Header.FullName = profile.Name;
        if (string.IsNullOrWhiteSpace(doc.Header.Title)) doc.Header.Title = profile.Field;
        if (string.IsNullOrWhiteSpace(doc.Header.Location)) doc.Header.Location = profile.Locations.FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(doc.Summary)) doc.Summary = profile.Summary;
        if (doc.SkillGroups.Count == 0)
        {
            var skills = profile.CoreSkills.Concat(profile.Skills).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            if (skills.Count > 0) doc.SkillGroups.Add(new CvSkillGroup { Skills = skills });
        }
        if (doc.Languages.Count == 0) doc.Languages = new List<string>(profile.Languages);

        bool plausible = !string.IsNullOrWhiteSpace(doc.Header.FullName)
            || doc.Experience.Count > 0 || !string.IsNullOrWhiteSpace(doc.Summary);
        return plausible ? doc : null;
    }

    /// <summary>Seed a CV without a PDF — header/summary/skills/languages from the saved profile.</summary>
    public static CvDocument FromProfile(UserProfile p)
    {
        var doc = new CvDocument
        {
            Header = new CvHeader
            {
                FullName = p.Name,
                Title = p.Field,
                Location = p.Locations.FirstOrDefault() ?? "",
            },
            Summary = p.Summary,
            Languages = new List<string>(p.Languages),
        };
        var skills = p.CoreSkills.Concat(p.Skills).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (skills.Count > 0) doc.SkillGroups.Add(new CvSkillGroup { Skills = skills });
        return doc;
    }

    /// <summary>Builds the assistant prompt: persona + current CV JSON + short history + optional
    /// job posting (tailoring) + the user's message + the {"reply","cv"} contract.</summary>
    public static string BuildChatPrompt(CvDocument cv, IReadOnlyList<(bool IsUser, string Text)> history,
        string userText, string? jobBlock)
    {
        string cvJson = JsonSerializer.Serialize(cv);
        var hist = new StringBuilder();
        // Drop oldest turns first if the whole prompt would overflow the budget.
        var kept = new List<(bool IsUser, string Text)>(history);
        while (true)
        {
            hist.Clear();
            foreach (var (isUser, t) in kept)
                hist.AppendLine((isUser ? "User: " : "Assistant: ") + (t.Length > 1500 ? t[..1500] + "…" : t));
            int size = cvJson.Length + hist.Length + (jobBlock?.Length ?? 0) + userText.Length + 1500;
            if (size <= PromptCap || kept.Count == 0) break;
            kept.RemoveAt(0);
        }

        var sb = new StringBuilder();
        sb.AppendLine("You are the CV editor inside the Job Radar app. You critique the user's CV and, when asked, edit it.");
        sb.AppendLine();
        sb.AppendLine("== CURRENT CV DOCUMENT (JSON) ==");
        sb.AppendLine(cvJson);
        if (hist.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== RECENT CONVERSATION ==");
            sb.Append(hist);
        }
        if (!string.IsNullOrWhiteSpace(jobBlock))
        {
            sb.AppendLine();
            sb.AppendLine("== JOB POSTING ==");
            sb.AppendLine(jobBlock);
        }
        sb.AppendLine();
        sb.AppendLine("== LATEST USER MESSAGE ==");
        sb.AppendLine(userText);
        sb.AppendLine();
        sb.AppendLine(@"Reply with ONLY one valid JSON object, exactly: {""reply"":""..."",""cv"":{...}}  or  {""reply"":""..."",""cv"":null}");
        sb.AppendLine("Rules:");
        sb.AppendLine("- \"reply\": your answer to the user, written in the same language the user wrote in; concise; markdown allowed.");
        sb.AppendLine("- If the user asked for ANY change to the CV (rewrite, reorder, trim, tailor, translate…), set \"cv\" to the COMPLETE updated CV document — the SAME schema as the current document above, with EVERY section included (copy unchanged sections verbatim). Otherwise set \"cv\" to null.");
        sb.AppendLine("- NEVER invent facts: no new employers, dates, titles, metrics or skills the user did not provide. Rephrasing, reordering and cutting are fine.");
        sb.AppendLine($"- The CV content must stay in the CV's language (\"{cv.Lang}\") unless the user explicitly asks to translate; keep \"templateId\" and \"accentColor\" exactly as they are.");
        sb.AppendLine("- When \"cv\" is not null, briefly say in \"reply\" WHAT you changed.");
        return sb.ToString();
    }

    /// <summary>Parses the assistant reply. A returned cv is normalized, its presentation fields are
    /// force-copied from the current document (app-owned), and an implausible husk is rejected.
    /// TriedButInvalid = the model attempted a cv object that couldn't be applied.</summary>
    public static (string Reply, CvDocument? Cv, bool TriedButInvalid) ParseChatReply(string raw, CvDocument current)
    {
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return (raw.Trim(), null, false);   // prose fallback: show as reply
        try
        {
            using var doc = JsonDocument.Parse(raw[a..(b + 1)]);
            string reply = doc.RootElement.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "" : "";
            bool tried = false;
            CvDocument? cv = null;
            if (doc.RootElement.TryGetProperty("cv", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                tried = true;
                try { cv = c.Deserialize<CvDocument>(JCase); } catch { cv = null; }
                if (cv is not null)
                {
                    CvStore.Normalize(cv);
                    cv.TemplateId = current.TemplateId;          // visual settings are app-owned
                    cv.AccentColor = current.AccentColor;
                    if (cv.Lang is not ("pt" or "en")) cv.Lang = current.Lang;
                    if (string.IsNullOrWhiteSpace(cv.TailoredFor)) cv.TailoredFor = current.TailoredFor;
                    bool plausible = !string.IsNullOrWhiteSpace(cv.Header.FullName)
                        || cv.Experience.Count > 0 || !string.IsNullOrWhiteSpace(cv.Summary);
                    if (!plausible) cv = null;                   // model returned a husk — refuse to apply
                }
            }
            return (reply, cv, tried && cv is null);
        }
        catch { return (raw.Trim(), null, false); }
    }

    /// <summary>Section keys ("summary","experience",…) whose serialized content differs — for the
    /// "changes applied" note. Language-free; the VM maps keys to Loc.</summary>
    public static List<string> ChangedSections(CvDocument oldDoc, CvDocument newDoc)
    {
        var changed = new List<string>();
        void Cmp(string key, object a, object b)
        {
            if (JsonSerializer.Serialize(a) != JsonSerializer.Serialize(b)) changed.Add(key);
        }
        Cmp("header", oldDoc.Header, newDoc.Header);
        Cmp("summary", oldDoc.Summary, newDoc.Summary);
        Cmp("experience", oldDoc.Experience, newDoc.Experience);
        Cmp("education", oldDoc.Education, newDoc.Education);
        Cmp("projects", oldDoc.Projects, newDoc.Projects);
        Cmp("certs", oldDoc.Certifications, newDoc.Certifications);
        Cmp("skills", oldDoc.SkillGroups, newDoc.SkillGroups);
        Cmp("langs", oldDoc.Languages, newDoc.Languages);
        return changed;
    }

    /// <summary>Job context block for the "tune for this job" action.</summary>
    public static string BuildTailorJobBlock(JobEntity j)
    {
        string desc = j.Description ?? "";
        if (desc.Length > JobBlockCap) desc = desc[..JobBlockCap] + "…";
        return $"{j.Title} at {j.Company}\n{desc}";
    }
}
