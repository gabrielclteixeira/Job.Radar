using System.Diagnostics;
using System.Text.Json;

namespace JobRadar;

/// <summary>
/// Scores a job against the candidate profile by shelling out to the local
/// Claude CLI in non-interactive mode (`claude -p ... --output-format json`).
/// No API key needed — uses the user's existing Claude subscription.
/// Any failure returns null so the caller can fall back to the pre-score.
/// </summary>
public class ClaudeScorer
{
    private readonly ClaudeConfig _cfg;
    private readonly string _profile;
    private readonly int _floorEur;
    private readonly int _targetEur;

    public ClaudeScorer(ClaudeConfig cfg, string profile, int floorEur, int targetEur)
    {
        _cfg = cfg;
        _profile = profile;
        _floorEur = floorEur;
        _targetEur = targetEur;
    }

    public async Task<AiResult?> ScoreAsync(JobEntity j)
    {
        string prompt =
$@"Score how well the JOB below fits the CANDIDATE. Output ONLY one valid JSON object and nothing else
(no markdown, no prose). All keys and string values MUST be double-quoted. Always return your best
estimate even if uncertain. Exact shape:
{{""score"": 73, ""verdict"": ""one short sentence"", ""reasons"": [""...""], ""redFlags"": [""...""]}}
PRIMARY CRITERION — FIELD & CORE SKILLS (most important by far): judge fit against the candidate's
own field and core skills as stated in their profile below.
- If the job is clearly in the candidate's field and uses their core skills, it can score high.
- If the job is in a DIFFERENT field/profession, the score MUST be <= 40 no matter how good the rest is.
- If the field/skills required are unclear, cap the score at <= 55.
Then, secondarily, factor seniority, location (remote/hybrid/preferred locations) and pay.
The candidate's salary floor is €{_floorEur:N0}/yr and target is €{_targetEur:N0}/yr — factor pay in and flag when below floor or unknown. Be critical and honest.

== CANDIDATE PROFILE ==
{_profile}

== JOB ==
Title: {j.Title}
Company: {j.Company}
Location: {j.Location} (remote flag: {j.Remote})
Salary: {(string.IsNullOrEmpty(j.SalaryText) ? "not listed" : j.SalaryText + (j.SalaryAnnualEur is int e ? $" (~€{e:N0}/yr)" : ""))}
Source: {j.Source}
Description:
{Trunc(j.Description, 3500)}";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _cfg.Exe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");

            using var p = Process.Start(psi);
            if (p is null) return null;

            p.StandardInput.Close(); // signal EOF so the CLI doesn't wait for piped stdin

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(_cfg.TimeoutSeconds * 1000);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }

            string text = await stdout;
            _ = await stderr;
            return Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>
    /// `--output-format json` returns an envelope whose "result" field holds the
    /// model's text. We unwrap that, then extract the inner JSON object.
    /// </summary>
    private static AiResult? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string text = raw;
        try
        {
            using var env = JsonDocument.Parse(raw);
            if (env.RootElement.ValueKind == JsonValueKind.Object &&
                env.RootElement.TryGetProperty("result", out var r) &&
                r.ValueKind == JsonValueKind.String)
            {
                text = r.GetString() ?? raw;
            }
        }
        catch { /* not an envelope — treat raw as the text */ }

        int a = text.IndexOf('{');
        int b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        string block = text.Substring(a, b - a + 1);

        // Try strict JSON, then a loosened variant (some replies omit quotes on keys).
        var result = TryParse(block) ?? TryParse(LoosenJson(block));
        if (result is not null) return result;

        // Last resort: regex-extract just the score so we still get a number.
        var m = System.Text.RegularExpressions.Regex.Match(block, @"""?score""?\s*:\s*(\d{1,3})");
        return m.Success && int.TryParse(m.Groups[1].Value, out var sc)
            ? new AiResult(Math.Clamp(sc, 0, 100), "", Array.Empty<string>(), Array.Empty<string>())
            : null;
    }

    private static AiResult? TryParse(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var root = d.RootElement;
            int score = root.TryGetProperty("score", out var se) && se.TryGetInt32(out var sv) ? sv : 0;
            string verdict = root.TryGetProperty("verdict", out var ve) && ve.ValueKind == JsonValueKind.String ? ve.GetString() ?? "" : "";
            return new AiResult(Math.Clamp(score, 0, 100), verdict, ReadArr(root, "reasons"), ReadArr(root, "redFlags"));
        }
        catch { return null; }
    }

    /// <summary>Quote bare object keys (e.g. {score: 5} -> {"score": 5}) for lenient parsing.</summary>
    private static string LoosenJson(string s)
        => System.Text.RegularExpressions.Regex.Replace(s, @"([{,]\s*)([A-Za-z_][A-Za-z0-9_]*)(\s*:)", "$1\"$2\"$3");

    private static string[] ReadArr(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();
}
