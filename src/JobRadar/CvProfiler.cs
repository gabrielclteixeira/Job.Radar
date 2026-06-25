using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;

namespace JobRadar;

/// <summary>
/// Turns a CV PDF into a structured <see cref="UserProfile"/>: extracts the text
/// with PdfPig, then asks the local Claude CLI to structure it. The user then
/// reviews/edits the result in the UI. Returns null if the CLI is unavailable so
/// the caller can fall back to a manual/empty profile.
/// </summary>
public static class CvProfiler
{
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Extracts plain text from a (text-based) PDF. Empty for scanned/image PDFs.</summary>
    public static string ExtractText(string pdfPath)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages()) sb.AppendLine(page.Text);
        return sb.ToString().Trim();
    }

    public static async Task<UserProfile?> BuildProfileAsync(string cvText, ClaudeConfig cfg, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cvText)) return null;

        string prompt =
$@"Extract a candidate profile from the CV text below — for ANY profession (software, nursing, finance,
design, etc.). Reply with ONLY one valid JSON object (double-quoted keys/values, no prose), exactly this shape:
{{""name"":""..."",""summary"":""one or two sentences"",""field"":""professional field, e.g. Software Engineering / Nursing / Accounting"",""jobTitles"":[""job titles to search for""],""coreSkills"":[""2-6 core skills/competencies, most important first""],""skills"":[""other relevant skills/tools""],""yearsExperience"":4,""seniorityTarget"":""mid"",""locations"":[""City, Country""],""languages"":[""English C2""]}}
- ""field"" = the person's profession/area in a few words.
- ""jobTitles"" = realistic titles to search job boards with (e.g. [""Registered Nurse"",""ICU Nurse""] or [""Backend Developer""]).
- ""coreSkills"" = the competencies that most define their fit.
- ""seniorityTarget"" = junior | mid | senior (best guess from years/roles).

== CV TEXT ==
{(cvText.Length > 8000 ? cvText[..8000] : cvText)}";

        string? text = await RunClaudeAsync(cfg, prompt, ct);
        if (text is null) return null;

        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<CvDto>(text.Substring(a, b - a + 1), J);
            if (dto is null) return null;
            return new UserProfile
            {
                Name = dto.Name ?? "",
                Summary = dto.Summary ?? "",
                Field = dto.Field ?? "",
                JobTitles = dto.JobTitles ?? new(),
                CoreSkills = dto.CoreSkills ?? dto.Stack ?? new(),
                Skills = dto.Skills ?? new(),
                YearsExperience = dto.YearsExperience,
                SeniorityTarget = string.IsNullOrWhiteSpace(dto.SeniorityTarget) ? "mid" : dto.SeniorityTarget!,
                Locations = dto.Locations ?? new(),
                Languages = dto.Languages ?? new(),
            };
        }
        catch { return null; }
    }

    private sealed class CvDto
    {
        public string? Name { get; set; }
        public string? Summary { get; set; }
        public string? Field { get; set; }
        public List<string>? JobTitles { get; set; }
        public List<string>? CoreSkills { get; set; }
        public List<string>? Stack { get; set; } // legacy/fallback
        public List<string>? Skills { get; set; }
        public int YearsExperience { get; set; }
        public string? SeniorityTarget { get; set; }
        public List<string>? Locations { get; set; }
        public List<string>? Languages { get; set; }
    }

    /// <summary>Runs `claude -p &lt;prompt&gt; --output-format json` and returns the model's text.</summary>
    internal static async Task<string?> RunClaudeAsync(ClaudeConfig cfg, string prompt, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cfg.Exe,
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
            p.StandardInput.Close();

            var stdout = p.StandardOutput.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(cfg.TimeoutSeconds * 1000);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }

            string raw = await stdout;
            try
            {
                using var env = JsonDocument.Parse(raw);
                if (env.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                    return r.GetString();
            }
            catch { /* not an envelope */ }
            return raw;
        }
        catch { return null; }
    }
}
