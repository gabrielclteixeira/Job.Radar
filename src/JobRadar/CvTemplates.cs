using System.Net;
using System.Text;

namespace JobRadar;

/// <summary>
/// Renders a <see cref="CvDocument"/> to a self-contained HTML page for the Edge HTML→PDF path.
/// Three templates:
///   - "clean"   — understated serif display, single column, maximum ATS safety.
///   - "accent"  — rich single column: colour header band, timeline experience, skill chips.
///   - "sidebar" — two-column with a tinted side panel (the classic "designed" look) — reads
///                 richer to humans, slightly less ATS-friendly (flagged in its label).
/// All real text, standard section headers, no icons/photos/skill bars. Section headers follow
/// cv.Lang ("pt"/"en"), independent of the app UI language. Edge renders with SYSTEM fonts only
/// (Segoe UI, Georgia, Cascadia) — the app's bundled Inter is not available to it.
/// </summary>
public static class CvTemplates
{
    public static readonly (string Id, string LocKey)[] All =
    {
        ("clean", "cv.template.clean"),
        ("accent", "cv.template.accent"),
        ("sidebar", "cv.template.sidebar"),
    };

    public static string Render(CvDocument cv)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        (string css, string body) = cv.TemplateId switch
        {
            "accent" => (AccentCss(cv.AccentColor), StandardBody(cv, rich: true)),
            "sidebar" => (SidebarCss(cv.AccentColor), SidebarBody(cv)),
            _ => (CleanCss(cv.AccentColor), StandardBody(cv, rich: false)),
        };
        return $@"<!doctype html><html lang=""{cv.Lang}""><head><meta charset=""utf-8"">
<title>{E(cv.Header.FullName)} — CV</title>
<style>{css}</style></head><body>{body}</body></html>";
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
        ("contact", "pt") => "Contacto",
        ("contact", _) => "Contact",
        ("present", "pt") => "Presente",
        _ => "Present",
    };

    private static string E2(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Dates(string start, string end, string lang)
        => string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end) ? ""
            : $"{E2(start)} – {(string.IsNullOrWhiteSpace(end) ? H("present", lang) : E2(end))}";

    private static void EHead(StringBuilder sb, string role, string org, string loc, string dates)
    {
        sb.Append(@"<div class=""ehead""><div>");
        sb.Append($@"<span class=""role"">{E2(role)}</span>");
        if (!string.IsNullOrWhiteSpace(org)) sb.Append($@" <span class=""co"">· {E2(org)}</span>");
        if (!string.IsNullOrWhiteSpace(loc)) sb.Append($@" <span class=""co"">· {E2(loc)}</span>");
        sb.Append("</div>");
        if (dates.Length > 0) sb.Append($@"<div class=""dates"">{dates}</div>");
        sb.Append("</div>");
    }

    private static void Bullets(StringBuilder sb, List<string> items)
    {
        var real = items.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        if (real.Count == 0) return;
        sb.Append("<ul>");
        foreach (var b in real) sb.Append($"<li>{E2(b)}</li>");
        sb.Append("</ul>");
    }

    private static string Chips(IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        foreach (var s in items.Where(s => !string.IsNullOrWhiteSpace(s)))
            sb.Append($@"<span class=""chip"">{E2(s)}</span>");
        return sb.ToString();
    }

    private static List<string> ContactParts(CvHeader h, bool includeLocation = true)
    {
        var parts = new List<string>();
        if (includeLocation && !string.IsNullOrWhiteSpace(h.Location)) parts.Add(E2(h.Location));
        if (!string.IsNullOrWhiteSpace(h.Email)) parts.Add($@"<a href=""mailto:{E2(h.Email)}"">{E2(h.Email)}</a>");
        if (!string.IsNullOrWhiteSpace(h.Phone)) parts.Add(E2(h.Phone));
        foreach (var l in h.Links.Where(l => !string.IsNullOrWhiteSpace(l.Url)))
            parts.Add($@"<a href=""{E2(l.Url)}"">{E2(string.IsNullOrWhiteSpace(l.Label) ? l.Url : l.Label)}</a>");
        return parts;
    }

    // ---- single-column body (clean + accent; accent adds the header band / timeline / chips) ----
    private static string StandardBody(CvDocument cv, bool rich)
    {
        string lang = cv.Lang;
        var sb = new StringBuilder();

        sb.Append($@"<header><h1>{E2(cv.Header.FullName)}</h1>");
        if (!string.IsNullOrWhiteSpace(cv.Header.Title))
            sb.Append($@"<div class=""headline"">{E2(cv.Header.Title)}</div>");
        var contact = ContactParts(cv.Header);
        if (contact.Count > 0)
            sb.Append($@"<div class=""contact"">{string.Join(@"<span class=""sep""> · </span>", contact)}</div>");
        sb.Append("</header>");

        if (!string.IsNullOrWhiteSpace(cv.Summary))
            sb.Append($@"<section><h2>{H("summary", lang)}</h2><p class=""summary"">{E2(cv.Summary)}</p></section>");

        if (cv.Experience.Count > 0)
        {
            sb.Append($@"<section><h2>{H("experience", lang)}</h2><div class=""timeline"">");
            foreach (var e in cv.Experience)
            {
                sb.Append(@"<div class=""entry"">");
                EHead(sb, e.Role, e.Company, e.Location, Dates(e.Start, e.End, lang));
                Bullets(sb, e.Bullets);
                sb.Append("</div>");
            }
            sb.Append("</div></section>");
        }

        if (cv.Education.Count > 0)
        {
            sb.Append($@"<section><h2>{H("education", lang)}</h2><div class=""timeline"">");
            foreach (var e in cv.Education)
            {
                sb.Append(@"<div class=""entry"">");
                EHead(sb, e.Degree, e.School, e.Location, Dates(e.Start, e.End, lang));
                Bullets(sb, e.Details);
                sb.Append("</div>");
            }
            sb.Append("</div></section>");
        }

        if (cv.Projects.Count > 0)
        {
            sb.Append($@"<section><h2>{H("projects", lang)}</h2><div class=""timeline"">");
            foreach (var p in cv.Projects)
            {
                sb.Append(@"<div class=""entry""><div class=""ehead""><div>");
                sb.Append(string.IsNullOrWhiteSpace(p.Link)
                    ? $@"<span class=""role"">{E2(p.Name)}</span>"
                    : $@"<a class=""role"" href=""{E2(p.Link)}"">{E2(p.Name)}</a>");
                if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($@" <span class=""co"">— {E2(p.Description)}</span>");
                sb.Append("</div></div>");
                Bullets(sb, p.Bullets);
                sb.Append("</div>");
            }
            sb.Append("</div></section>");
        }

        if (cv.Certifications.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            sb.Append($@"<section><h2>{H("certs", lang)}</h2>");
            Bullets(sb, cv.Certifications);
            sb.Append("</section>");
        }

        if (cv.SkillGroups.Any(g => g.Skills.Count > 0))
        {
            sb.Append($@"<section><h2>{H("skills", lang)}</h2>");
            foreach (var g in cv.SkillGroups.Where(g => g.Skills.Count > 0))
            {
                sb.Append(@"<div class=""skills"">");
                if (!string.IsNullOrWhiteSpace(g.Label)) sb.Append($@"<span class=""glabel"">{E2(g.Label)}</span> ");
                sb.Append(rich ? Chips(g.Skills) : E2(string.Join(", ", g.Skills.Where(s => !string.IsNullOrWhiteSpace(s)))));
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        if (cv.Languages.Any(l => !string.IsNullOrWhiteSpace(l)))
        {
            sb.Append($@"<section><h2>{H("languages", lang)}</h2><div class=""skills"">");
            sb.Append(rich ? Chips(cv.Languages)
                : E2(string.Join(" · ", cv.Languages.Where(l => !string.IsNullOrWhiteSpace(l)))));
            sb.Append("</div></section>");
        }

        return sb.ToString();
    }

    // ---- two-column body (sidebar template) ----
    private static string SidebarBody(CvDocument cv)
    {
        string lang = cv.Lang;
        var sb = new StringBuilder();
        sb.Append(@"<div class=""wrap""><aside>");

        // Side panel: contact → skills → languages → certifications.
        var contact = ContactParts(cv.Header);
        if (contact.Count > 0)
        {
            sb.Append($@"<h3>{H("contact", lang)}</h3><div class=""side"">");
            foreach (var c in contact) sb.Append($@"<div class=""citem"">{c}</div>");
            sb.Append("</div>");
        }
        if (cv.SkillGroups.Any(g => g.Skills.Count > 0))
        {
            sb.Append($@"<h3>{H("skills", lang)}</h3><div class=""side"">");
            foreach (var g in cv.SkillGroups.Where(g => g.Skills.Count > 0))
            {
                if (!string.IsNullOrWhiteSpace(g.Label)) sb.Append($@"<div class=""glabel"">{E2(g.Label)}</div>");
                sb.Append($@"<div class=""chips"">{Chips(g.Skills)}</div>");
            }
            sb.Append("</div>");
        }
        if (cv.Languages.Any(l => !string.IsNullOrWhiteSpace(l)))
        {
            sb.Append($@"<h3>{H("languages", lang)}</h3><div class=""side"">");
            foreach (var l in cv.Languages.Where(l => !string.IsNullOrWhiteSpace(l)))
                sb.Append($@"<div class=""citem"">{E2(l)}</div>");
            sb.Append("</div>");
        }
        if (cv.Certifications.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            sb.Append($@"<h3>{H("certs", lang)}</h3><div class=""side"">");
            foreach (var c in cv.Certifications.Where(c => !string.IsNullOrWhiteSpace(c)))
                sb.Append($@"<div class=""citem"">{E2(c)}</div>");
            sb.Append("</div>");
        }

        sb.Append("</aside><main>");
        sb.Append($@"<h1>{E2(cv.Header.FullName)}</h1>");
        if (!string.IsNullOrWhiteSpace(cv.Header.Title))
            sb.Append($@"<div class=""headline"">{E2(cv.Header.Title)}</div>");

        if (!string.IsNullOrWhiteSpace(cv.Summary))
            sb.Append($@"<section><h2>{H("summary", lang)}</h2><p class=""summary"">{E2(cv.Summary)}</p></section>");

        void Entries<T>(string headerKey, List<T> items, Action<T> render)
        {
            if (items.Count == 0) return;
            sb.Append($@"<section><h2>{H(headerKey, lang)}</h2><div class=""timeline"">");
            foreach (var it in items) { sb.Append(@"<div class=""entry"">"); render(it); sb.Append("</div>"); }
            sb.Append("</div></section>");
        }

        Entries("experience", cv.Experience, e =>
        {
            EHead(sb, e.Role, e.Company, e.Location, Dates(e.Start, e.End, lang));
            Bullets(sb, e.Bullets);
        });
        Entries("education", cv.Education, e =>
        {
            EHead(sb, e.Degree, e.School, e.Location, Dates(e.Start, e.End, lang));
            Bullets(sb, e.Details);
        });
        Entries("projects", cv.Projects, p =>
        {
            sb.Append(@"<div class=""ehead""><div>");
            sb.Append(string.IsNullOrWhiteSpace(p.Link)
                ? $@"<span class=""role"">{E2(p.Name)}</span>"
                : $@"<a class=""role"" href=""{E2(p.Link)}"">{E2(p.Name)}</a>");
            if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($@" <span class=""co"">— {E2(p.Description)}</span>");
            sb.Append("</div></div>");
            Bullets(sb, p.Bullets);
        });

        sb.Append("</main></div>");
        return sb.ToString();
    }

    // ---- template CSS ----
    // Shared building blocks: .timeline entries get a soft accent spine + dot in the rich templates;
    // .chip renders skills as tinted pills. Alpha-suffixed hex (#RRGGBBAA) is Chromium-safe.

    /// <summary>Clean: quiet serif display over a Segoe UI body — maximum ATS safety, more presence
    /// in the name block than before but still deliberately understated.</summary>
    private static string CleanCss(string accent) => $@"
:root{{--accent:{accent};--ink:#1a1a1e;--muted:#5c6370;--line:#d9d7e0}}
@page{{size:A4;margin:14mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10.5pt/1.45 'Segoe UI',Arial,sans-serif}}
header{{margin-bottom:6pt;border-bottom:2pt solid var(--accent);padding-bottom:8pt}}
h1{{font:400 27pt/1.12 Georgia,'Times New Roman',serif;margin:0;letter-spacing:.01em}}
.headline{{font:italic 11.5pt Georgia,serif;color:var(--muted);margin-top:2pt}}
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
.glabel{{font-weight:600}}
@media print{{body{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}";

    /// <summary>Accent: rich single column — rounded colour header band (name in white), timeline
    /// spine + dots on entries, tinted skill chips. Prints exactly (color-adjust) and stays one flow.</summary>
    private static string AccentCss(string accent) => $@"
:root{{--accent:{accent};--soft:{accent}14;--spine:{accent}33;--ink:#1c1b22;--muted:#5c6370;--line:#e4e2ec}}
@page{{size:A4;margin:12mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10.5pt/1.45 'Segoe UI',Roboto,Arial,sans-serif}}
header{{background:var(--accent);color:#fff;border-radius:10px;padding:14pt 16pt;margin-bottom:12pt}}
h1{{font-size:23pt;margin:0;letter-spacing:-.02em;font-weight:800;color:#fff}}
.headline{{font-size:11pt;color:rgba(255,255,255,.88);font-weight:600;margin-top:2pt}}
.contact{{font:8.5pt 'Cascadia Code',Consolas,monospace;color:rgba(255,255,255,.85);margin-top:7pt}}
.contact a{{color:#fff;text-decoration:none;border-bottom:.6pt solid rgba(255,255,255,.45)}}
.sep{{color:rgba(255,255,255,.45)}}
h2{{display:flex;align-items:center;gap:6pt;font:700 9pt 'Segoe UI',Arial,sans-serif;letter-spacing:.15em;
   text-transform:uppercase;color:var(--accent);margin:13pt 0 7pt}}
h2::before{{content:'';width:8pt;height:8pt;background:var(--accent);border-radius:2.5pt;flex:none}}
h2::after{{content:'';flex:1;height:.6pt;background:var(--line);margin-left:4pt}}
.summary{{margin:0;background:var(--soft);border-left:2.5pt solid var(--accent);border-radius:0 8px 8px 0;padding:8pt 10pt}}
.timeline{{border-left:1.6pt solid var(--spine);padding-left:12pt;margin-left:3pt}}
.entry{{position:relative;margin-bottom:9pt}}
.entry::before{{content:'';position:absolute;left:-16.2pt;top:3.5pt;width:7pt;height:7pt;border-radius:50%;
   background:var(--accent);box-shadow:0 0 0 2.2pt var(--soft)}}
.ehead{{display:flex;justify-content:space-between;gap:12pt}}
.role{{font-weight:700;color:var(--ink);text-decoration:none}}
a.role{{color:var(--accent)}}
.co{{color:var(--muted)}}
.dates{{color:var(--muted);font:9pt 'Cascadia Code',Consolas,monospace;white-space:nowrap}}
ul{{margin:3pt 0 0;padding-left:12pt}}
li{{margin-bottom:2pt}}
.skills{{margin-bottom:5pt}}
.glabel{{font-weight:700;display:inline-block;margin-right:4pt}}
.chip{{display:inline-block;background:var(--soft);color:var(--accent);border-radius:6pt;
   padding:1.5pt 7pt;margin:0 4pt 4pt 0;font-size:9pt;font-weight:600}}
@media print{{body{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}";

    /// <summary>Sidebar: tinted side panel (contact/skills/languages/certs) + main column with
    /// timeline entries — the classic "designed" CV. Less ATS-friendly than the single-column pair.</summary>
    private static string SidebarCss(string accent) => $@"
:root{{--accent:{accent};--soft:{accent}12;--soft2:{accent}1E;--spine:{accent}33;--ink:#1c1b22;--muted:#5c6370;--line:#e4e2ec}}
@page{{size:A4;margin:12mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10.5pt/1.45 'Segoe UI',Roboto,Arial,sans-serif}}
.wrap{{display:grid;grid-template-columns:60mm 1fr;gap:8mm}}
aside{{background:var(--soft);border-radius:10px;padding:9mm 6mm;min-height:calc(297mm - 26mm)}}
aside h3{{font:700 8.5pt 'Segoe UI',Arial,sans-serif;letter-spacing:.15em;text-transform:uppercase;
   color:var(--accent);margin:0 0 5pt;padding-bottom:3pt;border-bottom:1pt solid var(--soft2)}}
aside .side{{margin-bottom:12pt}}
aside .citem{{font-size:9.5pt;margin-bottom:3pt;word-break:break-word}}
aside .citem a{{color:var(--accent);text-decoration:none;font-weight:600}}
aside .glabel{{font-weight:700;font-size:9pt;margin:5pt 0 2pt}}
aside .chips{{margin-bottom:4pt}}
.chip{{display:inline-block;background:#fff;color:var(--accent);border:1pt solid var(--soft2);border-radius:6pt;
   padding:1.5pt 6pt;margin:0 3pt 3.5pt 0;font-size:8.5pt;font-weight:600}}
main{{padding-top:2mm}}
h1{{font-size:25pt;margin:0;letter-spacing:-.02em;font-weight:800}}
.headline{{font-size:11.5pt;color:var(--accent);font-weight:600;margin-top:2pt;margin-bottom:8pt}}
h2{{display:flex;align-items:center;gap:6pt;font:700 9pt 'Segoe UI',Arial,sans-serif;letter-spacing:.15em;
   text-transform:uppercase;color:var(--accent);margin:12pt 0 7pt}}
h2::before{{content:'';width:8pt;height:8pt;background:var(--accent);border-radius:2.5pt;flex:none}}
h2::after{{content:'';flex:1;height:.6pt;background:var(--line);margin-left:4pt}}
.summary{{margin:0}}
.timeline{{border-left:1.6pt solid var(--spine);padding-left:12pt;margin-left:3pt}}
.entry{{position:relative;margin-bottom:9pt}}
.entry::before{{content:'';position:absolute;left:-16.2pt;top:3.5pt;width:7pt;height:7pt;border-radius:50%;
   background:var(--accent);box-shadow:0 0 0 2.2pt var(--soft2)}}
.ehead{{display:flex;justify-content:space-between;gap:12pt}}
.role{{font-weight:700;color:var(--ink);text-decoration:none}}
a.role{{color:var(--accent)}}
.co{{color:var(--muted)}}
.dates{{color:var(--muted);font-size:9pt;white-space:nowrap}}
ul{{margin:3pt 0 0;padding-left:12pt}}
li{{margin-bottom:2pt}}
@media print{{body{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}";
}
