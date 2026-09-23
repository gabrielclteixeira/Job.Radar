using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobRadar;

/// <summary>
/// Scores jobs against the candidate profile via the configured LLM (Claude CLI or a local OpenAI-compatible
/// model). Scores in BATCHES: the profile + rubric are sent ONCE per batch and the model reasons once for the
/// whole group — far cheaper than one slow call per job, which matters a lot on local reasoning models that
/// burn ~1000+ tokens "thinking" per job. Any job the model omits (or a failed call) yields null so the caller
/// can fall back to the keyword pre-score.
/// </summary>
public class ClaudeScorer
{
    private readonly ClaudeConfig _cfg;
    private readonly string _profile;
    private readonly int _floorEur;
    private readonly int _targetEur;

    public ClaudeScorer(ClaudeConfig cfg, string profile, int floorEur, int targetEur)
    { _cfg = cfg; _profile = profile; _floorEur = floorEur; _targetEur = targetEur; }

    /// <summary>Scores a batch of jobs in ONE model call. Returns results aligned to <paramref name="jobs"/>
    /// (null where the model returned nothing usable for that job → caller uses the pre-score).</summary>
    public async Task<IReadOnlyList<AiResult?>> ScoreBatchAsync(IReadOnlyList<JobEntity> jobs, CancellationToken ct = default)
    {
        var results = new AiResult?[jobs.Count];
        if (jobs.Count == 0) return results;

        var sb = new StringBuilder();
        sb.Append(
$@"You are a STRICT, skeptical recruiter. Score how well EACH job below fits the CANDIDATE. Output ONLY one valid
JSON ARRAY and nothing else (no markdown, no prose). One object per job, shaped EXACTLY:
{{""i"":1,""score"":73,""verdict"":""one short sentence"",""reasons"":[""...""],""redFlags"":[""...""]}}
""i"" is the JOB number below. Keep it tight: verdict one sentence, at most 2 reasons, at most 2 redFlags.

PRIMARY CRITERION — FIELD & CORE SKILLS (by far the most important): the job's REQUIRED skills must explicitly
overlap the candidate's CORE skills — adjacent or merely-mentioned tech does NOT count. Be conservative: WHEN
IN DOUBT, SCORE LOWER. Score bands (follow strictly):
- 80-100: required stack clearly centres on the candidate's CORE skills, seniority fits, conditions good (remote/preferred location, pay at/above target). Reserve 90+ for near-perfect fits.
- 60-79: same field and MOST core skills present, conditions acceptable.
- 40-59: same field but several core skills missing, or the main stack differs, or poor conditions (pay below floor, location/seniority mismatch).
- 0-39: different field/profession, or core skills largely absent.
Hard rules: a DIFFERENT field/profession ⇒ score <= 40. Unclear required field/skills ⇒ <= 55. A stack the
candidate does NOT list as core (e.g. a different primary language) ⇒ keep 40-59 even if the title looks
relevant. Treat unknown pay/location/seniority as neutral-to-negative and note them in redFlags.
The candidate's salary floor is €{_floorEur:N0}/yr and target is €{_targetEur:N0}/yr — factor pay in and flag when below floor or unknown.

== CANDIDATE PROFILE ==
{_profile}

== JOBS ==
");
        for (int n = 0; n < jobs.Count; n++)
        {
            var j = jobs[n];
            string sal = string.IsNullOrEmpty(j.SalaryText) ? "not listed"
                : j.SalaryText + (j.SalaryAnnualEur is int e ? $" (~€{e:N0}/yr)" : "");
            sb.Append($"--- JOB {n + 1} ---\nTitle: {j.Title}\nCompany: {j.Company}\nLocation: {j.Location} (remote flag: {j.Remote})\nSalary: {sal}\nSource: {j.Source}\nDescription: {Trunc(j.Description, 1200)}\n\n");
        }

        string? text = await LlmClient.CompleteAsync(_cfg, sb.ToString(), ct);
        if (!string.IsNullOrWhiteSpace(text)) ParseBatch(text!, results);
        return results;
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>Parses the model's per-job scores into <paramref name="results"/>, mapping each object by its "i"
    /// field (falling back to position). Missing/garbled entries stay null. Tolerant of: a loosened parse (bare
    /// keys), a single bare object for a batch of one, a 0-based "i", and a TRUNCATED reply: when the array doesn't
    /// parse as a whole, every complete top-level object is still recovered, so a reply cut off inside job 5 keeps
    /// jobs 1-4 instead of losing the whole batch.</summary>
    internal static void ParseBatch(string raw, AiResult?[] results)
    {
        var elements = new List<JsonElement>();
        var docs = new List<JsonDocument>();
        try
        {
            int a = raw.IndexOf('['), b = raw.LastIndexOf(']');
            string block = (a >= 0 && b > a) ? raw.Substring(a, b - a + 1) : "";
            var doc = TryDoc(block) ?? TryDoc(LoosenJson(block));
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                docs.Add(doc);
                elements.AddRange(doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object));
            }
            else
            {
                doc?.Dispose();
                foreach (var obj in TopLevelObjects(a >= 0 ? raw[(a + 1)..] : raw))
                {
                    var od = TryDoc(obj) ?? TryDoc(LoosenJson(obj));
                    if (od is null) continue;
                    docs.Add(od);
                    if (od.RootElement.ValueKind == JsonValueKind.Object) elements.Add(od.RootElement);
                }
            }

            // The prompt says "i" is 1-based; a local model counting from 0 would otherwise shift every score onto
            // the wrong job without any error.
            var ids = elements.Select(e => e.TryGetProperty("i", out var ie) && ie.TryGetInt32(out var iv) ? iv : (int?)null).ToList();
            int offset = ids.Any(i => i == 0) ? 0 : 1;
            for (int n = 0; n < elements.Count; n++)
            {
                int slot = ids[n] is int id ? id - offset : n;
                if (slot >= 0 && slot < results.Length) results[slot] = ReadOne(elements[n]);
            }
        }
        finally { foreach (var d in docs) d.Dispose(); }
    }

    /// <summary>Yields each balanced top-level {...} object in the text. String-aware, so braces inside quoted
    /// text don't count; an object cut off by truncation never balances and is skipped.</summary>
    private static IEnumerable<string> TopLevelObjects(string s)
    {
        int depth = 0, start = -1;
        bool inStr = false, esc = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') { if (depth++ == 0) start = i; }
            else if (c == '}' && depth > 0 && --depth == 0) yield return s.Substring(start, i - start + 1);
        }
    }

    private static AiResult ReadOne(JsonElement el)
    {
        int score = 0;
        if (el.TryGetProperty("score", out var se))
            score = se.ValueKind == JsonValueKind.Number && se.TryGetDouble(out var sv) ? (int)Math.Round(sv) // 73.5 too
                  : se.ValueKind == JsonValueKind.String && double.TryParse(se.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var ss) ? (int)Math.Round(ss) : 0;
        string verdict = el.TryGetProperty("verdict", out var ve) && ve.ValueKind == JsonValueKind.String ? ve.GetString() ?? "" : "";
        return new AiResult(Math.Clamp(score, 0, 100), verdict, ReadArr(el, "reasons"), ReadArr(el, "redFlags"));
    }

    private static JsonDocument? TryDoc(string json)
    { try { return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json); } catch { return null; } }

    /// <summary>Quote bare object keys (e.g. {score: 5} -> {"score": 5}) for lenient parsing.</summary>
    private static string LoosenJson(string s)
        => Regex.Replace(s, @"([{,]\s*)([A-Za-z_][A-Za-z0-9_]*)(\s*:)", "$1\"$2\"$3");

    private static string[] ReadArr(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String) // a stray number/object must not throw
                .Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();
}
