using System.Net;
using System.Text;

namespace JobRadar;

/// <summary>
/// Renders a <see cref="CvDocument"/> to a self-contained HTML page for the Edge HTML→PDF path.
/// One shared single-column DOM (ATS-safe: real text, standard section headers, no tables/columns/
/// icons/photos/skill bars) + per-template CSS — "clean" (understated serif display) and "accent"
/// (the app's violet look, evolved from CvPdf). Section headers follow cv.Lang ("pt"/"en"), which
/// is independent of the app UI language. Edge renders with SYSTEM fonts only (Segoe UI, Georgia,
/// Cascadia) — the app's bundled Inter is not available to it.
/// </summary>
public static class CvTemplates
{
    public static readonly (string Id, string LocKey)[] All =
    {
        ("clean", "cv.template.clean"),
        ("accent", "cv.template.accent"),
    };

    public static string Render(CvDocument cv)
    {
        string css = cv.TemplateId == "accent" ? AccentCss(cv.AccentColor) : CleanCss(cv.AccentColor);
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        return $@"<!doctype html><html lang=""{cv.Lang}""><head><meta charset=""utf-8"">
<title>{E(cv.Header.FullName)} — CV</title>
<style>{css}</style></head><body>{Body(cv)}</body></html>";
    }

    // ---- section headers (CV language, not UI language) ----
    private static string H(string key, string lang) => (key, lang) switch
    {
        ("summary", "pt") => "Resumo",
        ("summary", _) => "Summary",
        ("experience", "pt") => "Experiência Profissional",
        ("experience", _) => "Professional Experience",
        ("education", "pt") => "Formação",
        ("education", _) => "Education",
        ("projects", "pt") => "Projetos",
        ("projects", _) => "Projects",
        ("certs", "pt") => "Certificações",
        ("certs", _) => "Certifications",
        ("skills", "pt") => "Competências",
        ("skills", _) => "Skills",
        ("languages", "pt") => "Línguas",
        ("languages", _) => "Languages",
        ("present", "pt") => "Presente",
        _ => "Present",
    };

    /// <summary>Shared single-column DOM — templates differ only in CSS. Empty sections are omitted.</summary>
    private static string Body(CvDocument cv)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string lang = cv.Lang;
        var sb = new StringBuilder();

        // Header: name, headline, one contact line with clickable links.
        sb.Append($@"<header><h1>{E(cv.Header.FullName)}</h1>");
        if (!string.IsNullOrWhiteSpace(cv.Header.Title))
            sb.Append($@"<div class=""headline"">{E(cv.Header.Title)}</div>");
        var contact = new List<string>();
        if (!string.IsNullOrWhiteSpace(cv.Header.Location)) contact.Add(E(cv.Header.Location));
        if (!string.IsNullOrWhiteSpace(cv.Header.Email))
            contact.Add($@"<a href=""mailto:{E(cv.Header.Email)}"">{E(cv.Header.Email)}</a>");
        if (!string.IsNullOrWhiteSpace(cv.Header.Phone)) contact.Add(E(cv.Header.Phone));
        foreach (var l in cv.Header.Links.Where(l => !string.IsNullOrWhiteSpace(l.Url)))
            contact.Add($@"<a href=""{E(l.Url)}"">{E(string.IsNullOrWhiteSpace(l.Label) ? l.Url : l.Label)}</a>");
        if (contact.Count > 0)
            sb.Append($@"<div class=""contact"">{string.Join(@"<span class=""sep""> · </span>", contact)}</div>");
        sb.Append("</header>");

        if (!string.IsNullOrWhiteSpace(cv.Summary))
            sb.Append($@"<section><h2>{H("summary", lang)}</h2><p class=""summary"">{E(cv.Summary)}</p></section>");

        void Dated(string role, string org, string loc, string start, string end)
        {
            string dates = string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end) ? ""
                : $"{E(start)} – {(string.IsNullOrWhiteSpace(end) ? H("present", lang) : E(end))}";
            sb.Append(@"<div class=""ehead""><div>");
            sb.Append($@"<span class=""role"">{E(role)}</span>");
            if (!string.IsNullOrWhiteSpace(org)) sb.Append($@" <span class=""co"">· {E(org)}</span>");
            if (!string.IsNullOrWhiteSpace(loc)) sb.Append($@" <span class=""co"">· {E(loc)}</span>");
            sb.Append("</div>");
            if (dates.Length > 0) sb.Append($@"<div class=""dates"">{dates}</div>");
            sb.Append("</div>");
        }

        void Bullets(List<string> items)
        {
            var real = items.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
            if (real.Count == 0) return;
            sb.Append("<ul>");
            foreach (var b in real) sb.Append($"<li>{E(b)}</li>");
            sb.Append("</ul>");
        }

        if (cv.Experience.Count > 0)
        {
            sb.Append($@"<section><h2>{H("experience", lang)}</h2>");
            foreach (var e in cv.Experience)
            {
                sb.Append(@"<div class=""entry"">");
                Dated(e.Role, e.Company, e.Location, e.Start, e.End);
                Bullets(e.Bullets);
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        if (cv.Education.Count > 0)
        {
            sb.Append($@"<section><h2>{H("education", lang)}</h2>");
            foreach (var e in cv.Education)
            {
                sb.Append(@"<div class=""entry"">");
                Dated(e.Degree, e.School, e.Location, e.Start, e.End);
                Bullets(e.Details);
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        if (cv.Projects.Count > 0)
        {
            sb.Append($@"<section><h2>{H("projects", lang)}</h2>");
            foreach (var p in cv.Projects)
            {
                sb.Append(@"<div class=""entry""><div class=""ehead""><div>");
                sb.Append(string.IsNullOrWhiteSpace(p.Link)
                    ? $@"<span class=""role"">{E(p.Name)}</span>"
                    : $@"<a class=""role"" href=""{E(p.Link)}"">{E(p.Name)}</a>");
                if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($@" <span class=""co"">— {E(p.Description)}</span>");
                sb.Append("</div></div>");
                Bullets(p.Bullets);
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        if (cv.Certifications.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            sb.Append($@"<section><h2>{H("certs", lang)}</h2>");
            Bullets(cv.Certifications);
            sb.Append("</section>");
        }

        if (cv.SkillGroups.Any(g => g.Skills.Count > 0))
        {
            sb.Append($@"<section><h2>{H("skills", lang)}</h2>");
            foreach (var g in cv.SkillGroups.Where(g => g.Skills.Count > 0))
            {
                sb.Append(@"<div class=""skills"">");
                if (!string.IsNullOrWhiteSpace(g.Label)) sb.Append($"<b>{E(g.Label)}:</b> ");
                sb.Append(E(string.Join(", ", g.Skills.Where(s => !string.IsNullOrWhiteSpace(s)))));
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        if (cv.Languages.Any(l => !string.IsNullOrWhiteSpace(l)))
            sb.Append($@"<section><h2>{H("languages", lang)}</h2><div class=""skills"">{E(string.Join(" · ", cv.Languages.Where(l => !string.IsNullOrWhiteSpace(l))))}</div></section>");

        return sb.ToString();
    }

    // ---- template CSS ----
    /// <summary>Clean: quiet serif display (Georgia) over a Segoe UI body — typography does the work.</summary>
    private static string CleanCss(string accent) => $@"
:root{{--accent:{accent};--ink:#1a1a1e;--muted:#5c6370;--line:#d9d7e0}}
@page{{size:A4;margin:14mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10.5pt/1.45 'Segoe UI',Arial,sans-serif}}
header{{margin-bottom:4pt}}
h1{{font:400 24pt/1.15 Georgia,'Times New Roman',serif;margin:0;letter-spacing:.01em}}
.headline{{font:italic 11pt Georgia,serif;color:var(--muted);margin-top:2pt}}
.contact{{font-size:9pt;color:var(--muted);margin-top:6pt}}
.contact a{{color:var(--accent);text-decoration:none}}
.sep{{color:var(--line)}}
h2{{font:600 9pt 'Segoe UI',Arial,sans-serif;letter-spacing:.16em;text-transform:uppercase;
   color:var(--accent);border-bottom:.6pt solid var(--line);padding-bottom:3pt;margin:14pt 0 7pt}}
.summary{{margin:0}}
.entry{{margin-bottom:8pt}}
.ehead{{display:flex;justify-content:space-between;gap:12pt}}
.role{{font-weight:600;color:var(--ink);text-decoration:none}}
a.role{{color:var(--accent)}}
.co{{color:var(--muted)}}
.dates{{color:var(--muted);font-size:9.5pt;white-space:nowrap}}
ul{{margin:3pt 0 0;padding-left:12pt}}
li{{margin-bottom:2pt}}
.skills{{margin-bottom:3pt}}
@media print{{body{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}";

    /// <summary>Accent: the app's own look — mono uppercase section headers, stronger colour — single column.</summary>
    private static string AccentCss(string accent) => $@"
:root{{--accent:{accent};--ink:#1c1b22;--muted:#5c6370;--line:#e4e2ec}}
@page{{size:A4;margin:14mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10.5pt/1.45 'Segoe UI',Roboto,Arial,sans-serif}}
header{{border-bottom:2.5pt solid var(--accent);padding-bottom:8pt;margin-bottom:4pt}}
h1{{font-size:22pt;margin:0;letter-spacing:-.02em;font-weight:800}}
.headline{{font-size:11pt;color:var(--accent);font-weight:600;margin-top:2pt}}
.contact{{font:9pt 'Cascadia Code',Consolas,monospace;color:var(--muted);margin-top:6pt}}
.contact a{{color:var(--accent);text-decoration:none}}
.sep{{color:var(--line)}}
h2{{font:700 8.5pt 'Cascadia Code',Consolas,monospace;letter-spacing:.16em;text-transform:uppercase;
   color:var(--accent);margin:13pt 0 6pt}}
.summary{{margin:0}}
.entry{{margin-bottom:8pt}}
.ehead{{display:flex;justify-content:space-between;gap:12pt}}
.role{{font-weight:700;color:var(--ink);text-decoration:none}}
a.role{{color:var(--accent)}}
.co{{color:var(--muted)}}
.dates{{color:var(--muted);font:9pt 'Cascadia Code',Consolas,monospace;white-space:nowrap}}
ul{{margin:3pt 0 0;padding-left:12pt}}
li{{margin-bottom:2pt}}
.skills{{margin-bottom:3pt}}
@media print{{body{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}";
}
