using System.Net;
using System.Text;

namespace JobRadar;

/// <summary>
/// Renders a <see cref="CareerPlanResult"/> as a clean A4 PDF, reusing the same Edge headless
/// HTML→PDF path as the CV/report exports (<see cref="Reports.FindEdge"/> + <see cref="Reports.WritePdf"/>).
/// Mirrors the structure of the in-app Grow view: positioning, strengths, gaps, target roles,
/// salary trajectory, next steps, market signals and sources.
/// </summary>
public static class CareerPlanPdf
{
    /// <summary>Builds the plan as a self-contained HTML document in the app's violet style.
    /// <paramref name="withProgress"/> adds the user's checklist progress bar (opt-in — the PDF is normally the
    /// shareable, recruiter-facing snapshot; personal progress is included only when the user asks).</summary>
    public static string BuildHtml(CareerPlanResult plan, UserProfile p, bool withProgress = false)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string name = string.IsNullOrWhiteSpace(p.Name) ? "Career Plan" : p.Name;
        string field = string.IsNullOrWhiteSpace(p.Field) ? "" : p.Field;

        var sb = new StringBuilder();
        sb.Append($@"<!doctype html><html lang=""en""><head><meta charset=""utf-8""><title>{E(name)} — Career Plan</title>
<style>
:root{{--ink:#17171B;--muted:#5C6370;--accent:#4C2DBE;--accent2:#ECE9FB;--line:#E4E2EC;--signal:#1A7F4B;
--sans:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;--mono:'Cascadia Code',Consolas,monospace;}}
*{{box-sizing:border-box;}}body{{margin:0;color:var(--ink);font-family:var(--sans);line-height:1.5;font-size:13px;background:#fff;}}
.page{{max-width:820px;margin:0 auto;padding:38px 46px 40px;}}
.bar{{height:6px;background:linear-gradient(90deg,var(--accent),#7457E0);margin:-38px -46px 24px;}}
header h1{{font-size:26px;margin:0;letter-spacing:-.02em;font-weight:800;}}
.eyebrow{{font-family:var(--mono);font-size:10px;font-weight:700;letter-spacing:.16em;text-transform:uppercase;color:var(--accent);}}
.sub{{font-family:var(--mono);font-size:10.5px;color:var(--muted);margin:6px 0 0;}}
.headline{{background:var(--accent2);border:1px solid var(--accent);border-radius:10px;padding:13px 15px;margin:18px 0 0;}}
.headline .t{{font-size:14.5px;font-weight:600;color:var(--ink);margin:3px 0 0;}}
section{{margin-top:20px;break-inside:avoid;}}
h2{{font-family:var(--mono);font-size:11px;font-weight:700;letter-spacing:.14em;text-transform:uppercase;color:var(--accent);margin:0 0 8px;}}
ul{{margin:0;padding-left:18px;}}li{{margin-bottom:3px;color:#3c3a4b;}}
.chk{{list-style:none;padding:0;}}.chk li{{padding-left:18px;position:relative;}}.chk li::before{{content:'✓';position:absolute;left:0;color:var(--signal);font-weight:700;}}
.gap{{border:1px solid var(--line);border-radius:8px;padding:9px 12px;margin-bottom:7px;}}
.gap .s{{font-weight:700;color:var(--ink);}}.gap .w{{color:var(--muted);font-size:12px;}}.gap .a{{font-size:12px;margin-top:3px;}}.gap .a b{{color:var(--accent);}}
.chips span{{display:inline-block;background:var(--accent2);color:var(--accent);border-radius:7px;padding:2px 9px;font-size:11px;font-family:var(--mono);margin:0 5px 5px 0;}}
.sal{{display:flex;gap:12px;flex-wrap:wrap;}}
.salcard{{flex:1;min-width:200px;border:1px solid var(--line);border-radius:9px;padding:11px 13px;}}
.salcard.pot{{background:var(--accent2);border-color:var(--accent);}}
.salcard .k{{font-family:var(--mono);font-size:9.5px;letter-spacing:.06em;text-transform:uppercase;color:var(--muted);}}
.salcard .v{{font-size:16px;font-weight:800;color:var(--accent);margin-top:2px;}}
.salrat{{font-size:11.5px;color:var(--muted);margin-top:7px;}}
.step{{display:flex;gap:11px;margin-bottom:8px;}}
.step .h{{flex:none;width:78px;font-family:var(--mono);font-size:10px;font-weight:700;color:var(--accent);padding-top:1px;}}
.step .t{{font-weight:600;}}.step .d{{font-size:12px;color:#4a4956;}}
.foot{{margin-top:26px;padding-top:10px;border-top:1px solid var(--line);font-family:var(--mono);font-size:9.5px;color:var(--muted);}}
.src{{font-family:var(--mono);font-size:10px;color:var(--muted);}}.src li{{margin-bottom:2px;word-break:break-all;}}.src a{{color:var(--muted);text-decoration:none;}}
.revised{{display:inline-block;background:var(--signal);color:#fff;border-radius:6px;padding:1px 8px;font-size:10px;font-weight:700;font-family:var(--mono);margin-left:9px;vertical-align:middle;}}
.caveat{{font-size:11px;color:var(--muted);font-style:italic;margin:9px 0 0;}}
.prog{{margin:14px 0 0;}}.prog .lbl{{display:flex;justify-content:space-between;font-family:var(--mono);font-size:10px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);margin-bottom:4px;}}
.prog .track{{height:7px;background:var(--accent2);border-radius:4px;overflow:hidden;}}.prog .fill{{height:100%;background:var(--accent);border-radius:4px;}}
.crit{{border:1px solid #C2410C;border-radius:9px;padding:12px 15px;margin-top:18px;break-inside:avoid;}}
.crit h2{{color:#C2410C;margin-top:0;}}
.crititem{{margin-bottom:9px;}}.crititem .c{{font-weight:700;color:var(--ink);}}
.crititem .i{{color:var(--muted);font-size:12px;}}
.crititem .r{{font-size:12px;margin-top:2px;}}.crititem .r b{{color:var(--accent);}}
@media print{{@page{{size:A4;margin:0;}}body{{-webkit-print-color-adjust:exact;print-color-adjust:exact;}}.bar{{margin-top:0;}}}}
</style></head><body><div class=""page""><div class=""bar""></div>

<header>
  <div class=""eyebrow"">Career Plan</div>
  <h1>{E(name)}{(plan.Revised ? $@"<span class=""revised"">{E(Loc.Instance.T("improve.revised"))}</span>" : "")}</h1>
  <div class=""sub"">{E(field)}{(string.IsNullOrWhiteSpace(field) || p.Locations.Count == 0 ? "" : "  ·  ")}{E(string.Join(" · ", p.Locations))}</div>
</header>");

        if (plan.HasHeadline)
            sb.Append($@"<div class=""headline""><div class=""eyebrow"">Positioning</div><div class=""t"">{E(plan.Headline)}</div></div>");

        if (plan.HasCaveat)
            sb.Append($@"<div class=""caveat"">{E(plan.CritiqueCaveat)}</div>");

        if (withProgress && plan.HasTrackable)
            sb.Append($@"<div class=""prog""><div class=""lbl""><span>{E(Loc.Instance.T("improve.progress"))}</span><span>{plan.DoneCount}/{plan.TrackableCount} · {plan.ProgressPercent}%</span></div><div class=""track""><div class=""fill"" style=""width:{plan.ProgressPercent}%;""></div></div></div>");

        if (plan.HasStrengths)
        {
            sb.Append(@"<section><h2>Strengths</h2><ul class=""chk"">");
            foreach (var s in plan.Strengths) sb.Append($"<li>{E(s)}</li>");
            sb.Append("</ul></section>");
        }

        if (plan.HasSkillGaps)
        {
            sb.Append(@"<section><h2>Gaps to close</h2>");
            foreach (var g in plan.SkillGaps)
            {
                sb.Append($@"<div class=""gap""><div class=""s"">{E(g.Skill)}</div>");
                if (g.HasWhy) sb.Append($@"<div class=""w"">{E(g.Why)}</div>");
                if (g.HasAction) sb.Append($@"<div class=""a""><b>→</b> {E(g.Action)}</div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        if (plan.HasTargetRoles)
        {
            sb.Append(@"<section><h2>Target roles</h2><div class=""chips"">");
            foreach (var r in plan.TargetRoles) sb.Append($"<span>{E(r)}</span>");
            sb.Append("</div></section>");
        }

        if (plan.HasSalary)
        {
            sb.Append(@"<section><h2>Salary trajectory</h2><div class=""sal"">");
            if (plan.HasSalaryNow)
                sb.Append($@"<div class=""salcard""><div class=""k"">Now</div><div class=""v"">{E(plan.SalaryNow)}</div></div>");
            if (plan.HasSalaryPotential)
                sb.Append($@"<div class=""salcard pot""><div class=""k"">Potential · 12–24 months</div><div class=""v"">{E(plan.SalaryPotential)}</div></div>");
            sb.Append("</div>");
            if (plan.HasSalaryRationale) sb.Append($@"<div class=""salrat"">{E(plan.SalaryRationale)}</div>");
            sb.Append("</section>");
        }

        if (plan.HasSteps)
        {
            sb.Append(@"<section><h2>Next steps</h2>");
            foreach (var s in plan.Steps)
            {
                sb.Append(@"<div class=""step"">");
                sb.Append($@"<div class=""h"">{E(s.Horizon)}</div><div><div class=""t"">{E(s.Title)}</div>");
                if (s.HasDetail) sb.Append($@"<div class=""d"">{E(s.Detail)}</div>");
                sb.Append("</div></div>");
            }
            sb.Append("</section>");
        }

        if (plan.HasMarketSignals)
        {
            sb.Append(@"<section><h2>Market signals</h2><ul>");
            foreach (var m in plan.MarketSignals) sb.Append($"<li>{E(m)}</li>");
            sb.Append("</ul></section>");
        }

        if (plan.HasCritique)
        {
            sb.Append($@"<div class=""crit""><h2>{E(Loc.Instance.T("improve.critique"))}</h2>");
            foreach (var c in plan.Critique)
            {
                sb.Append($@"<div class=""crititem""><div class=""c"">⚠ {E(c.Claim)}</div><div class=""i"">{E(c.Issue)}</div>");
                if (c.HasRebuttal) sb.Append($@"<div class=""r""><b>{E(Loc.Instance.T("improve.rebuttal"))}:</b> {E(c.Rebuttal)}</div>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }

        if (plan.HasBottomLine)
            sb.Append($@"<section><h2>Bottom line</h2><p>{E(plan.BottomLine)}</p></section>");

        if (plan.HasFallback)
            sb.Append($@"<section><h2>Plan</h2><p style=""white-space:pre-wrap;"">{E(plan.RawFallback)}</p></section>");

        if (plan.HasSources)
        {
            sb.Append(@"<section><h2>Sources</h2><ul class=""src"">");
            foreach (var src in plan.Sources) sb.Append($@"<li>[{src.N}] {E(src.Host)} — <a href=""{E(src.Url)}"">{E(src.Url)}</a></li>");
            sb.Append("</ul></section>");
        }

        sb.Append(@"<div class=""foot"">Generated by Job Radar from the candidate profile and key-free web research. Treat figures as guidance, not guarantees.</div>");
        sb.Append(@"</div></body></html>");
        return sb.ToString();
    }

    /// <summary>Writes the plan to <paramref name="outDir"/> as HTML and (if Edge is available) PDF.
    /// Returns the path to open (PDF when produced, otherwise the HTML), or null on failure.</summary>
    public static string? Export(CareerPlanResult plan, UserProfile p, string outDir, string stamp, bool withProgress = false)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            string safeName = string.Concat((string.IsNullOrWhiteSpace(p.Name) ? "CareerPlan" : p.Name)
                .Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
            string html = Path.Combine(outDir, $"{safeName}-CareerPlan-{stamp}.html");
            string pdf = Path.Combine(outDir, $"{safeName}-CareerPlan-{stamp}.pdf");
            File.WriteAllText(html, BuildHtml(plan, p, withProgress), new UTF8Encoding(false));

            string? edge = Reports.FindEdge();
            if (edge is not null && Reports.WritePdf(html, pdf, edge) && File.Exists(pdf))
                return pdf;
            return html; // Edge missing — at least hand back the HTML
        }
        catch { return null; }
    }
}
