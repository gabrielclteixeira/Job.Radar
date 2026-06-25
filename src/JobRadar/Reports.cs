using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace JobRadar;

/// <summary>Writes the relevant jobs out as JSON, CSV and a styled HTML→PDF report.</summary>
public static class Reports
{
    public static void WriteJson(string path, IEnumerable<JobEntity> jobs)
        => File.WriteAllText(path, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true }));

    public static void WriteCsv(string path, IEnumerable<JobEntity> jobs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Score,AiScore,PreScore,Title,Company,Location,Remote,Salary,SalaryEur,Source,Status,Verdict,Url");
        foreach (var j in jobs)
            sb.AppendLine(string.Join(",", new[]
            {
                j.FinalScore.ToString(), j.AiScore?.ToString() ?? "", j.PreScore.ToString(),
                Csv(j.Title), Csv(j.Company), Csv(j.Location), Csv(j.Remote),
                Csv(j.SalaryText), j.SalaryAnnualEur?.ToString() ?? "",
                Csv(j.Source), Csv(j.Status), Csv(j.AiVerdict ?? ""), Csv(j.Url),
            }));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM so Excel reads UTF-8
    }

    private static string Csv(string? s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    public static void WriteHtml(string path, IReadOnlyList<JobEntity> jobs, int newCount, string day)
    {
        string Tier(int s) => s >= 70 ? "strong" : s >= 50 ? "good" : "maybe";
        string TierLabel(int s) => s >= 70 ? "Strong fit" : s >= 50 ? "Good" : "Maybe";

        var sb = new StringBuilder();
        sb.Append($@"<!doctype html><html><head><meta charset=""utf-8"">
<title>Job Radar — {day}</title>
<style>
:root{{--ground:#FBFBFD;--ink:#17171B;--muted:#5C6370;--accent:#4C2DBE;--accent2:#ECE9FB;--line:#E4E2EC;
--strong:#1A7F4B;--good:#4C2DBE;--maybe:#8A8699;--sans:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;
--mono:ui-monospace,'Cascadia Code','SF Mono',Menlo,Consolas,monospace;}}
*{{box-sizing:border-box;}}body{{margin:0;background:#EDECF3;color:var(--ink);font-family:var(--sans);line-height:1.5;}}
.page{{max-width:860px;margin:0 auto;background:var(--ground);padding:34px 42px 40px;}}
.bar{{height:6px;background:linear-gradient(90deg,var(--accent),#7457E0);margin:-34px -42px 26px;}}
h1{{font-size:24px;font-weight:800;margin:0;letter-spacing:-.02em;}}
.sub{{font-family:var(--mono);font-size:11px;color:var(--muted);margin:7px 0 0;}}
.stats{{display:flex;gap:18px;margin:16px 0 8px;flex-wrap:wrap;}}
.stat{{background:var(--accent2);border-radius:8px;padding:8px 14px;}}
.stat b{{font-size:18px;color:var(--accent);}} .stat span{{font-size:10.5px;color:var(--muted);display:block;font-family:var(--mono);text-transform:uppercase;letter-spacing:.05em;}}
.job{{border:1px solid var(--line);border-radius:9px;padding:13px 15px;margin-bottom:10px;background:#fff;break-inside:avoid;}}
.jh{{display:flex;gap:12px;align-items:flex-start;}}
.score{{flex:none;width:46px;height:46px;border-radius:9px;display:flex;flex-direction:column;align-items:center;justify-content:center;color:#fff;font-weight:800;font-size:17px;font-family:var(--mono);}}
.score small{{font-size:7px;font-weight:600;opacity:.85;letter-spacing:.05em;}}
.s-strong{{background:var(--strong);}} .s-good{{background:var(--good);}} .s-maybe{{background:var(--maybe);}}
.jt{{font-size:14px;font-weight:700;margin:0;}} .jt a{{color:var(--ink);text-decoration:none;}} .jt a:hover{{color:var(--accent);}}
.jmeta{{font-family:var(--mono);font-size:10.5px;color:var(--muted);margin:2px 0 0;display:flex;gap:10px;flex-wrap:wrap;}}
.jmeta .co{{color:var(--accent);font-weight:600;}}
.rmt{{display:inline-block;font-size:9px;font-family:var(--mono);text-transform:uppercase;border-radius:7px;padding:1px 6px;background:var(--accent2);color:var(--accent);}}
.sal{{display:inline-block;font-size:9.5px;font-family:var(--mono);font-weight:700;border-radius:7px;padding:1px 7px;background:#1A7F4B;color:#fff;}}
.verdict{{font-size:12px;color:#33323b;margin:8px 0 0;}}
.reasons{{margin:5px 0 0;padding:0;list-style:none;}}
.reasons li{{font-size:11.5px;color:#4a4956;padding-left:14px;position:relative;}}
.reasons li::before{{content:'▹';position:absolute;left:0;color:var(--accent);}}
.tierhead{{font-family:var(--mono);font-size:11px;font-weight:700;letter-spacing:.1em;text-transform:uppercase;color:var(--accent);margin:22px 0 10px;}}
.foot{{margin-top:24px;padding-top:12px;border-top:1px solid var(--line);font-family:var(--mono);font-size:10px;color:var(--muted);}}
@media print{{@page{{size:A4;margin:12mm;}}body{{background:#fff;}}.page{{max-width:none;padding:0;}}.bar{{display:none;}}.job{{break-inside:avoid;}}*{{-webkit-print-color-adjust:exact;print-color-adjust:exact;}}}}
</style></head><body><div class=""page""><div class=""bar""></div>
<h1>Job Radar</h1>
<p class=""sub"">// vagas .NET/Go · remoto · híbrido · Porto — geradas {day}</p>
<div class=""stats"">
<div class=""stat""><b>{jobs.Count}</b><span>relevantes</span></div>
<div class=""stat""><b>{newCount}</b><span>novas hoje</span></div>
<div class=""stat""><b>{jobs.Count(j => j.FinalScore >= 70)}</b><span>strong fit</span></div>
</div>");

        string lastTier = "";
        foreach (var j in jobs)
        {
            string tier = Tier(j.FinalScore);
            if (tier != lastTier)
            {
                sb.Append($@"<div class=""tierhead"">{TierLabel(j.FinalScore)}</div>");
                lastTier = tier;
            }
            sb.Append($@"<div class=""job""><div class=""jh"">
<div class=""score s-{tier}"">{j.FinalScore}<small>{(j.AiScore.HasValue ? "AI" : "KW")}</small></div>
<div><p class=""jt""><a href=""{WebUtility.HtmlEncode(j.Url)}"">{WebUtility.HtmlEncode(j.Title)}</a></p>
<div class=""jmeta""><span class=""co"">{WebUtility.HtmlEncode(j.Company)}</span><span>{WebUtility.HtmlEncode(j.Location)}</span>");
            if (!string.IsNullOrEmpty(j.Remote)) sb.Append($@"<span class=""rmt"">{WebUtility.HtmlEncode(j.Remote)}</span>");
            if (!string.IsNullOrEmpty(j.SalaryText)) sb.Append($@"<span class=""sal"">{WebUtility.HtmlEncode(j.SalaryText)}</span>");
            sb.Append($@"<span>{WebUtility.HtmlEncode(j.Source)}</span></div>");
            if (!string.IsNullOrWhiteSpace(j.AiVerdict))
                sb.Append($@"<p class=""verdict"">{WebUtility.HtmlEncode(j.AiVerdict)}</p>");
            var reasons = ParseReasons(j.AiReasons);
            if (reasons.Length > 0)
            {
                sb.Append(@"<ul class=""reasons"">");
                foreach (var r in reasons.Take(3)) sb.Append($"<li>{WebUtility.HtmlEncode(r)}</li>");
                sb.Append("</ul>");
            }
            sb.Append("</div></div></div>");
        }

        sb.Append($@"<div class=""foot"">Porto Job Radar · {jobs.Count} vagas · gerado por fetcher Go + core C# + scoring Claude CLI. Confirmar sempre na página oficial.</div></div></body></html>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string[] ParseReasons(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public static bool WritePdf(string htmlPath, string pdfPath, string edgePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = edgePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--headless=new");
            psi.ArgumentList.Add("--disable-gpu");
            psi.ArgumentList.Add("--no-pdf-header-footer");
            psi.ArgumentList.Add($"--print-to-pdf={pdfPath}");
            psi.ArgumentList.Add("file:///" + htmlPath.Replace('\\', '/'));
            using var p = Process.Start(psi);
            return p is not null && p.WaitForExit(30000) && File.Exists(pdfPath);
        }
        catch { return false; }
    }

    public static string? FindEdge()
    {
        foreach (var p in new[]
        {
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        })
            if (File.Exists(p)) return p;
        return null;
    }
}
