using System.Net;
using System.Text;

namespace JobRadar;

/// <summary>
/// Writes the researched companies (employer-health signals) out as CSV and a styled HTML→PDF report.
/// Mirrors <see cref="Reports"/> (same CSV escaping, same Edge-headless PDF path) but for
/// <see cref="CompanyReport"/>. Only companies with a report are exported.
/// </summary>
public static class CompanyExport
{
    public static void WriteCsv(string path, IEnumerable<CompanyReport> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Company,Rating,Scale,Reviews,RatingSource,Recommend%,CEOApproval%,eNPS,WorkLife,Culture,Career,Management,Compensation,Diversity,InterviewDifficulty,PayBand,PayRole,Tenure,Industry,Size,HQ,Founded,CEO,Website,TechStack,Layoffs,MostRecentLayoff,Signals,Confidence,AsOf,BottomLine,Pros,Cons,RedFlags,Sources");
        foreach (var r in reports)
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(r.Company),
                r.Rating?.ToString("0.0") ?? "", r.Rating is > 0 ? r.RatingScale.ToString("0") : "",
                r.ReviewCount?.ToString() ?? "", Csv(r.RatingSource),
                r.RecommendPct?.ToString() ?? "", r.CeoApprovalPct?.ToString() ?? "", r.ENps?.ToString() ?? "",
                r.WorkLifeRating?.ToString("0.0") ?? "", r.CultureRating?.ToString("0.0") ?? "",
                r.CareerRating?.ToString("0.0") ?? "", r.ManagementRating?.ToString("0.0") ?? "",
                r.CompensationRating?.ToString("0.0") ?? "", r.DiversityRating?.ToString("0.0") ?? "",
                Csv(r.InterviewDifficulty),
                Csv(r.PayBand), Csv(r.PayRole), Csv(r.Tenure),
                Csv(r.Industry), Csv(r.CompanySize), Csv(r.Headquarters),
                Csv(r.Founded), Csv(r.Ceo), Csv(r.Website), Csv(string.Join("; ", r.TechStack)),
                r.Layoffs.Count.ToString(), Csv(r.Layoffs.Count > 0 ? r.Layoffs[0].Display : ""),
                Csv(string.Join("; ", r.Signals)),
                Csv(r.Confidence), Csv(AsOf(r)), Csv(r.BottomLine),
                Csv(string.Join("; ", r.Pros)), Csv(string.Join("; ", r.Cons)), Csv(string.Join("; ", r.RedFlags)),
                Csv(string.Join(" ", r.Sources.Select(s => s.Url))),
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

    public static void WriteHtml(string path, IReadOnlyList<CompanyReport> reports, string day)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append($@"<!doctype html><html><head><meta charset=""utf-8"">
<title>Job Radar — Companies — {day}</title>
<style>
:root{{--ground:#FBFBFD;--ink:#17171B;--muted:#5C6370;--accent:#4C2DBE;--accent2:#ECE9FB;--line:#E4E2EC;
--signal:#1A7F4B;--amber:#B26A00;--sans:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;
--mono:ui-monospace,'Cascadia Code','SF Mono',Menlo,Consolas,monospace;}}
*{{box-sizing:border-box;}}body{{margin:0;background:#EDECF3;color:var(--ink);font-family:var(--sans);line-height:1.5;}}
.page{{max-width:860px;margin:0 auto;background:var(--ground);padding:34px 42px 40px;}}
.bar{{height:6px;background:linear-gradient(90deg,var(--accent),#7457E0);margin:-34px -42px 26px;}}
h1{{font-size:24px;font-weight:800;margin:0;letter-spacing:-.02em;}}
.sub{{font-family:var(--mono);font-size:11px;color:var(--muted);margin:7px 0 0;}}
.stats{{display:flex;gap:18px;margin:16px 0 8px;flex-wrap:wrap;}}
.stat{{background:var(--accent2);border-radius:8px;padding:8px 14px;}}
.stat b{{font-size:18px;color:var(--accent);}} .stat span{{font-size:10.5px;color:var(--muted);display:block;font-family:var(--mono);text-transform:uppercase;letter-spacing:.05em;}}
.co{{border:1px solid var(--line);border-radius:9px;padding:14px 16px;margin-bottom:11px;background:#fff;break-inside:avoid;}}
.con{{font-size:16px;font-weight:800;margin:0;}}
.chips{{margin:6px 0 2px;display:flex;gap:7px;flex-wrap:wrap;}}
.chip{{font-family:var(--mono);font-size:10.5px;font-weight:700;border-radius:7px;padding:2px 8px;background:var(--accent2);color:var(--accent);}}
.chip.rat{{background:#E2F3EA;color:var(--signal);}} .chip.lay{{background:#fff;border:1px solid var(--amber);color:var(--amber);}}
.sec{{margin:9px 0 0;}} .lab{{font-family:var(--mono);font-size:10px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--accent);margin:0 0 3px;}}
.big{{font-family:var(--mono);font-size:15px;font-weight:800;}}
.muted{{color:var(--muted);font-size:11.5px;}}
ul{{margin:3px 0 0;padding-left:0;list-style:none;}} li{{font-size:12px;padding-left:15px;position:relative;margin:1px 0;}}
li.p::before{{content:'✓';position:absolute;left:0;color:var(--signal);font-weight:700;}}
li.c::before{{content:'✕';position:absolute;left:0;color:#C0392B;font-weight:700;}}
li.f::before{{content:'⚠';position:absolute;left:0;color:var(--amber);}}
li.l::before{{content:'⚠';position:absolute;left:0;color:var(--amber);}}
li.n::before{{content:'•';position:absolute;left:0;color:var(--accent);}}
.bl{{font-style:italic;font-size:12px;margin:8px 0 0;}}
.src{{font-family:var(--mono);font-size:10px;color:var(--muted);margin:8px 0 0;word-break:break-all;}}
.src a{{color:var(--accent);text-decoration:none;}}
.foot{{margin-top:24px;padding-top:12px;border-top:1px solid var(--line);font-family:var(--mono);font-size:10px;color:var(--muted);}}
@media print{{@page{{size:A4;margin:12mm;}}body{{background:#fff;}}.page{{max-width:none;padding:0;}}.bar{{display:none;}}.co{{break-inside:avoid;}}*{{-webkit-print-color-adjust:exact;print-color-adjust:exact;}}}}
</style></head><body><div class=""page""><div class=""bar""></div>
<h1>Job Radar — Companies</h1>
<p class=""sub"">// employer-health signals · generated {day}</p>
<div class=""stats"">
<div class=""stat""><b>{reports.Count}</b><span>companies</span></div>
<div class=""stat""><b>{reports.Count(r => r.HasRating)}</b><span>with rating</span></div>
<div class=""stat""><b>{reports.Count(r => r.HasLayoffs)}</b><span>with layoffs</span></div>
</div>");

        foreach (var r in reports)
        {
            sb.Append($@"<div class=""co""><p class=""con"">{E(r.Company)}</p><div class=""chips"">");
            if (r.HasRating) sb.Append($@"<span class=""chip rat"">★ {r.Rating:0.0}</span>");
            if (r.HasLayoffs) sb.Append($@"<span class=""chip lay"">⚠ layoffs{(string.IsNullOrWhiteSpace(r.Layoffs[0].Period) ? "" : " " + E(r.Layoffs[0].Period))}</span>");
            if (r.HasPayBand) sb.Append($@"<span class=""chip"">~{E(r.PayBand)}</span>");
            if (r.HasConfidence) sb.Append($@"<span class=""chip"">{E(r.Confidence)}</span>");
            sb.Append("</div>");

            if (r.HasSatisfaction)
            {
                sb.Append(@"<div class=""sec""><p class=""lab"">Satisfaction</p>");
                if (r.HasRating) sb.Append($@"<span class=""big"">★ {r.Rating:0.0} / {r.RatingScale:0}</span> <span class=""muted"">{(r.HasReviewCount ? E(r.ReviewsText) + " reviews" : "")} {E(r.RatingSource)}</span>");
                else sb.Append($@"<span class=""muted"">{E(r.ReviewsText)} reviews {E(r.RatingSource)} — no aggregate rating in the sources</span>");
                if (r.HasRecommend) sb.Append($@"<div class=""muted"">{r.RecommendPct}% recommend</div>");
                if (r.HasCeoApproval) sb.Append($@"<div class=""muted"">{r.CeoApprovalPct}% CEO approval</div>");
                var subs = new List<string>();
                if (r.HasWorkLife) subs.Add($"Work-life {r.WorkLifeText}");
                if (r.HasCulture) subs.Add($"Culture {r.CultureText}");
                if (r.HasCareer) subs.Add($"Growth {r.CareerText}");
                if (r.HasManagement) subs.Add($"Management {r.ManagementText}");
                if (r.HasCompensation) subs.Add($"Comp {r.CompensationText}");
                if (r.HasDiversity) subs.Add($"Diversity {r.DiversityText}");
                if (subs.Count > 0) sb.Append($@"<div class=""muted"">{E(string.Join(" · ", subs))}</div>");
                if (r.HasENps) sb.Append($@"<div class=""muted"">eNPS {r.ENps}</div>");
                if (r.HasInterviewDifficulty) sb.Append($@"<div class=""muted"">Interview: {E(r.InterviewDifficulty)}</div>");
                sb.Append("</div>");
            }
            if (r.HasLayoffs)
            {
                sb.Append(@"<div class=""sec""><p class=""lab"">Recent layoffs</p><ul>");
                foreach (var l in r.Layoffs) sb.Append($@"<li class=""l"">{E(l.Display)}{(l.HasUrl ? $@" — <a href=""{E(l.Url)}"">{E(Host(l.Url))}</a>" : "")}</li>");
                sb.Append("</ul></div>");
            }
            if (r.HasSignals)
            {
                sb.Append(@"<div class=""sec""><p class=""lab"">Recent signals</p><ul>");
                foreach (var s in r.Signals) sb.Append($@"<li class=""n"">{E(s)}</li>");
                sb.Append("</ul></div>");
            }
            if (r.HasPayBand)
                sb.Append($@"<div class=""sec""><p class=""lab"">Typical pay</p><span class=""big"">{E(r.PayBand)}</span> <span class=""muted"">{E(r.PayRole)}</span></div>");
            if (r.HasTenure)
                sb.Append($@"<div class=""sec""><p class=""lab"">Average tenure</p><span class=""muted"">{E(r.Tenure)}</span></div>");
            if (r.HasFirmographics || r.HasCeo || r.HasWebsite)
            {
                sb.Append(@"<div class=""sec""><p class=""lab"">About</p>");
                if (r.HasFirmographics) sb.Append($@"<div class=""muted"">{E(r.Firmographics)}</div>");
                if (r.HasCeo) sb.Append($@"<div class=""muted"">CEO: {E(r.Ceo)}</div>");
                if (r.HasWebsite) sb.Append($@"<div class=""muted""><a href=""{E(r.Website)}"">{E(r.Website)}</a></div>");
                sb.Append("</div>");
            }
            if (r.HasTechStack)
                sb.Append($@"<div class=""sec""><p class=""lab"">Stack (from postings)</p><span class=""muted"">{E(r.TechStackText)}</span></div>");
            if (r.HasPros || r.HasCons || r.HasRedFlags)
            {
                sb.Append(@"<ul>");
                foreach (var p in r.Pros) sb.Append($@"<li class=""p"">{E(p)}</li>");
                foreach (var c in r.Cons) sb.Append($@"<li class=""c"">{E(c)}</li>");
                foreach (var f in r.RedFlags) sb.Append($@"<li class=""f"">{E(f)}</li>");
                sb.Append("</ul>");
            }
            if (r.HasBottomLine) sb.Append($@"<p class=""bl"">{E(r.BottomLine)}</p>");
            if (r.HasSources)
            {
                sb.Append(@"<p class=""src"">Sources: ");
                sb.Append(string.Join(" · ", r.Sources.Select(s => $@"<a href=""{E(s.Url)}"">[{s.N}] {E(s.Host)}</a>")));
                sb.Append("</p>");
            }
            sb.Append($@"<p class=""src"">As of {E(AsOf(r))}</p></div>");
        }

        sb.Append(@"<div class=""foot"">Job Radar · employer-health signals estimated from public search snippets (Glassdoor/kununu, news, Levels.fyi) — best-effort, not authoritative. Verify against the sources.</div></div></body></html>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string AsOf(CompanyReport r)
        => r.AsOfDate is DateTime d ? d.ToLocalTime().ToString("yyyy-MM-dd") : "";

    private static string Host(string url)
    {
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch { return url; }
    }
}
