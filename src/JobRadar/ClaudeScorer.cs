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

    /// <summary>Parses the JSON array of per-job scores into <paramref name="results"/>, mapping each object by
    /// its "i" field (falling back to position). Missing/garbled entries stay null. Tolerates a loosened parse
    /// and a single-object reply for a batch of one.</summary>
    private static void ParseBatch(string raw, AiResult?[] results)
    {
        int a = raw.IndexOf('['), b = raw.LastIndexOf(']');
        string block = (a >= 0 && b > a) ? raw.Substring(a, b - a + 1) : "";
        var doc = TryDoc(block) ?? TryDoc(LoosenJson(block));
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            using (doc)
            {
                int idx = 0;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    int slot = el.TryGetProperty("i", out var ie) && ie.TryGetInt32(out var iv) ? iv - 1 : idx;
                    idx++;
                    if (slot >= 0 && slot < results.Length) results[slot] = ReadOne(el);
                }
            }
            return;
        }
        doc?.Dispose();

        // Fallback: a batch of one the model returned as a bare object instead of an array.
        if (results.Length == 1)
        {
            int oa = raw.IndexOf('{'), ob = raw.LastIndexOf('}');
            if (oa >= 0 && ob > oa)
            {
                string one = raw.Substring(oa, ob - oa + 1);
                var od = TryDoc(one) ?? TryDoc(LoosenJson(one));
                if (od is not null) { using (od) results[0] = ReadOne(od.RootElement); }
            }
        }
    }

    private static AiResult ReadOne(JsonElement el)
    {
        int score = 0;
        if (el.TryGetProperty("score", out var se))
            score = se.ValueKind == JsonValueKind.Number && se.TryGetInt32(out var sv) ? sv
                  : se.ValueKind == JsonValueKind.String && int.TryParse(se.GetString(), out var ss) ? ss : 0;
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
            ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();
}
