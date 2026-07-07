using System.Net;
using System.Text;

namespace JobRadar;

/// <summary>
/// Renders a <see cref="CvDocument"/> to a self-contained HTML page for the Edge HTML→PDF path.
/// Five templates — each a distinct design direction, all real text with standard section headers
/// (rich for humans AND parseable by ATS/LLM screeners; no icons/photos/skill bars anywhere):
///   - "clean"      — minimal serif display, single column: the maximum-ATS baseline.
///   - "editorial"  — Swiss typographic: huge name, thick section rules, dates in a left rail.
///   - "terminal"   — developer-native: monospace metadata, prompt-style meta line, code-fence skills.
///   - "stationery" — Constantia serif, two-tone centred name, small caps, double rules.
///   - "panel"      — dark side panel with monogram + chips; the richest look (less ATS-friendly).
/// Section headers follow cv.Lang ("pt"/"en"), independent of the app UI language. Edge renders
/// with SYSTEM fonts only (Segoe UI, Georgia, Constantia, Cascadia) — bundled app fonts don't reach it.
/// </summary>
public static class CvTemplates
{
    public static readonly (string Id, string LocKey)[] All =
    {
        ("clean", "cv.template.clean"),
        ("editorial", "cv.template.editorial"),
        ("terminal", "cv.template.terminal"),
        ("stationery", "cv.template.stationery"),
        ("panel", "cv.template.panel"),
    };

    public static string Render(CvDocument cv)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        (string css, string body) = cv.TemplateId switch
        {
            "editorial" => (EditorialCss(cv.AccentColor), EditorialBody(cv)),
            "terminal" => (TerminalCss(cv.AccentColor), StandardBody(cv, fences: true)),
            "stationery" => (StationeryCss(cv.AccentColor), StationeryBody(cv)),
            "panel" => (PanelCss(cv.AccentColor), PanelBody(cv)),
            _ => (CleanCss(cv.AccentColor), StandardBody(cv, fences: false)),
        };
        return $@"<!doctype html><html lang=""{cv.Lang}""><head><meta charset=""utf-8"">
<title>{E(cv.Header.FullName)} — CV</title>
<style>{css}</style></head><body>{body}</body></html>";
    }

    /// <summary>A John Doe document so the template picker can preview any style without the user's data.</summary>
    public static CvDocument SampleDoc(string lang) => new()
    {
        Lang = lang is "en" ? "en" : "pt",
        Header = new CvHeader
        {
            FullName = "John Doe",
            Title = lang == "en" ? "Product Engineer — Web · Data · Cloud" : "Engenheiro de Produto — Web · Dados · Cloud",
            Email = "john.doe@example.com",
            Phone = "+351 900 000 000",
            Location = lang == "en" ? "Porto, Portugal" : "Porto, Portugal",
            Links = new()
            {
                new CvLink { Label = "LinkedIn", Url = "https://linkedin.com/in/johndoe" },
                new CvLink { Label = "GitHub", Url = "https://github.com/johndoe" },
            },
        },
        Summary = lang == "en"
            ? "Lorem ipsum dolor sit amet, consectetur adipiscing elit — pragmatic engineer shipping web platforms and data products end-to-end, sed do eiusmod tempor incididunt ut labore, with a focus on measurable outcomes."
            : "Lorem ipsum dolor sit amet, consectetur adipiscing elit — engenheiro pragmático que entrega plataformas web e produtos de dados de ponta a ponta, sed do eiusmod tempor incididunt, com foco em resultados mensuráveis.",
        Experience = new()
        {
            new CvExperience
            {
                Company = "Acme Corp", Role = lang == "en" ? "Senior Engineer" : "Engenheiro Sénior",
                Start = "2021", End = "", Location = "Porto",
                Bullets = new()
                {
                    "Lorem ipsum dolor sit amet: rebuilt the billing pipeline, cutting errors by 40%.",
                    "Consectetur adipiscing elit — led a team of 4 through two major releases.",
                    "Sed do eiusmod tempor: automated reporting, saving ~12 hours per week.",
                },
            },
            new CvExperience
            {
                Company = "Globex", Role = lang == "en" ? "Software Developer" : "Programador",
                Start = "2018", End = "2021", Location = "Lisboa",
                Bullets = new()
                {
                    "Ut enim ad minim veniam: shipped the customer portal used by 30k users.",
                    "Quis nostrud exercitation — introduced CI/CD, halving release time.",
                },
            },
        },
        Education = new()
        {
            new CvEducation
            {
                School = "Universidade do Porto", Degree = lang == "en" ? "BSc, Computer Science" : "Licenciatura, Engenharia Informática",
                Start = "2014", End = "2017", Location = "Porto",
            },
        },
        Projects = new()
        {
            new CvProject
            {
                Name = "Open Widget", Link = "https://github.com/johndoe/widget",
                Description = lang == "en" ? "open-source dashboard toolkit" : "toolkit open-source de dashboards",
                Bullets = new() { "Lorem ipsum dolor sit amet, 1.2k stars, consectetur adipiscing elit." },
            },
        },
        Certifications = new() { "Cloud Practitioner (AWS, 2022)" },
        SkillGroups = new()
        {
            new CvSkillGroup { Label = lang == "en" ? "Languages" : "Linguagens", Skills = new() { "C#", "Python", "TypeScript", "SQL" } },
            new CvSkillGroup { Label = "Web", Skills = new() { "ASP.NET Core", "React", "REST APIs" } },
            new CvSkillGroup { Label = lang == "en" ? "Data & Cloud" : "Dados & Cloud", Skills = new() { "PostgreSQL", "Docker", "AWS", "CI/CD" } },
        },
        Languages = new() { lang == "en" ? "Portuguese (native)" : "Português (nativo)", "English (C1)", "Spanish (B2)" },
    };

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
            : $"{E2(start)} — {(string.IsNullOrWhiteSpace(end) ? H("present", lang) : E2(end))}";

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

    /// <summary>Optional user photo as a self-contained data-URI img tag ("" when absent/unreadable).
    /// Each template places and shapes .photo with its own CSS.</summary>
    private static string PhotoImg(CvDocument cv)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cv.PhotoPath) || !File.Exists(cv.PhotoPath)) return "";
            byte[] bytes = File.ReadAllBytes(cv.PhotoPath);
            string mime = Path.GetExtension(cv.PhotoPath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png",
            };
            return $@"<img class=""photo"" alt="""" src=""data:{mime};base64,{Convert.ToBase64String(bytes)}"">";
        }
        catch { return ""; }
    }

    private static string Initials(string fullName)
    {
        var words = (fullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "CV",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[^1][0])}",
        };
    }

    /// <summary>Splits "English (C2)" / "English C2" / "Português nativo" into (name, level) when possible.</summary>
    private static (string Name, string Level) SplitLang(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s.Trim(),
            @"^(.*?)[\s\(–-]*\b(native|nativo|nativa|fluent|fluente|[ABC][12])\)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Value.Trim().TrimEnd('(', '-', '–').Trim(), m.Groups[2].Value) : (s, "");
    }

    // =====================================================================================
    // Shared single-column body: "clean" (plain lists) and "terminal" (code-fence skills).
    // =====================================================================================
    private static string StandardBody(CvDocument cv, bool fences)
    {
        string lang = cv.Lang;
        var sb = new StringBuilder();

        sb.Append("<header>");
        sb.Append(PhotoImg(cv));   // float:right in clean/terminal CSS
        sb.Append($@"<h1>{E2(cv.Header.FullName)}</h1>");
        if (!string.IsNullOrWhiteSpace(cv.Header.Title))
        {
            string title = E2(cv.Header.Title);
            sb.Append(fences ? $@"<div class=""meta""><b>{title}</b></div>" : $@"<div class=""headline"">{title}</div>");
        }
        var contact = ContactParts(cv.Header);
        if (contact.Count > 0)
            sb.Append($@"<div class=""contact"">{string.Join(@"<span class=""sep""> · </span>", contact)}</div>");
        sb.Append("</header>");

        if (!string.IsNullOrWhiteSpace(cv.Summary))
            sb.Append($@"<section><h2>{H("summary", lang)}</h2><p class=""summary"">{E2(cv.Summary)}</p></section>");

        if (cv.Experience.Count > 0)
        {
            sb.Append($@"<section><h2>{H("experience", lang)}</h2>");
            foreach (var e in cv.Experience)
            {
                sb.Append(@"<div class=""entry"">");
                EHead(sb, e.Role, e.Company, e.Location, Dates(e.Start, e.End, lang));
                Bullets(sb, e.Bullets);
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
                    ? $@"<span class=""role"">{E2(p.Name)}</span>"
                    : $@"<a class=""role"" href=""{E2(p.Link)}"">{E2(p.Name)}</a>");
                if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($@" <span class=""co"">— {E2(p.Description)}</span>");
                sb.Append("</div></div>");
                Bullets(sb, p.Bullets);
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
                EHead(sb, e.Degree, e.School, e.Location, Dates(e.Start, e.End, lang));
                Bullets(sb, e.Details);
                sb.Append("</div>");
            }
            sb.Append("</section>");
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
                string skills = E2(string.Join(fences ? " · " : ", ", g.Skills.Where(s => !string.IsNullOrWhiteSpace(s))));
                string label = string.IsNullOrWhiteSpace(g.Label) ? "" : $@"<b>{E2(g.Label)}</b>&nbsp;&nbsp;";
                sb.Append(fences
                    ? $@"<div class=""fence"">{label}{skills}</div>"
                    : $@"<div class=""skills"">{(label.Length > 0 ? $@"<span class=""glabel"">{E2(g.Label)}:</span> " : "")}{skills}</div>");
            }
            sb.Append("</section>");
        }

        if (cv.Languages.Any(l => !string.IsNullOrWhiteSpace(l)))
        {
            string langs = E2(string.Join(" · ", cv.Languages.Where(l => !string.IsNullOrWhiteSpace(l))));
            sb.Append($@"<section><h2>{H("languages", lang)}</h2>");
            sb.Append(fences ? $@"<div class=""fence"">{langs}</div>" : $@"<div class=""skills"">{langs}</div>");
            sb.Append("</section>");
        }

        return sb.ToString();
    }

    // =====================================================================================
    // Editorial: asymmetric header (name left, contact right), thick rules, left date rail.
    // =====================================================================================
    private static string EditorialBody(CvDocument cv)
    {
        string lang = cv.Lang;
        var sb = new StringBuilder();

        var nameWords = (cv.Header.FullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string nameHtml = nameWords.Length > 1
            ? E2(string.Join(' ', nameWords[..^1])) + "<br>" + E2(nameWords[^1])
            : E2(cv.Header.FullName);

        sb.Append($@"<header><div><h1>{nameHtml}</h1>");
        if (!string.IsNullOrWhiteSpace(cv.Header.Title))
            sb.Append($@"<div class=""title"">{E2(cv.Header.Title)}</div>");
        sb.Append("</div>");
        sb.Append(PhotoImg(cv));   // sits between the name block and the contact column
        var contact = ContactParts(cv.Header);
        if (contact.Count > 0)
            sb.Append($@"<div class=""contact"">{string.Join("<br>", contact)}</div>");
        sb.Append("</header>");

        void Section(string headerKey, Action inner)
        {
            sb.Append($@"<section><h2>{H(headerKey, lang)}</h2>");
            inner();
            sb.Append("</section>");
        }

        if (!string.IsNullOrWhiteSpace(cv.Summary))
            Section("summary", () => sb.Append($@"<p class=""summary"">{E2(cv.Summary)}</p>"));

        void RailEntry(string dates, Action inner)
        {
            sb.Append($@"<div class=""entry""><div class=""rail"">{dates}</div><div>");
            inner();
            sb.Append("</div></div>");
        }

        if (cv.Experience.Count > 0)
            Section("experience", () =>
            {
                foreach (var e in cv.Experience)
                    RailEntry(Dates(e.Start, e.End, lang), () =>
                    {
                        sb.Append($@"<div class=""role"">{E2(e.Role)}");
                        if (!string.IsNullOrWhiteSpace(e.Company)) sb.Append($@" <span class=""co"">· {E2(e.Company)}</span>");
                        if (!string.IsNullOrWhiteSpace(e.Location)) sb.Append($@" <span class=""co"">· {E2(e.Location)}</span>");
                        sb.Append("</div>");
                        Bullets(sb, e.Bullets);
                    });
            });

        if (cv.Projects.Count > 0)
            Section("projects", () =>
            {
                foreach (var p in cv.Projects)
                    RailEntry("", () =>
                    {
                        sb.Append(@"<div class=""role"">");
                        sb.Append(string.IsNullOrWhiteSpace(p.Link)
                            ? E2(p.Name) : $@"<a href=""{E2(p.Link)}"">{E2(p.Name)}</a>");
                        if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($@" <span class=""co"">— {E2(p.Description)}</span>");
                        sb.Append("</div>");
                        Bullets(sb, p.Bullets);
                    });
            });

        if (cv.Education.Count > 0)
            Section("education", () =>
            {
                foreach (var e in cv.Education)
                    RailEntry(Dates(e.Start, e.End, lang), () =>
                    {
                        sb.Append($@"<div class=""role"">{E2(e.Degree)}");
                        if (!string.IsNullOrWhiteSpace(e.School)) sb.Append($@" <span class=""co"">· {E2(e.School)}</span>");
                        sb.Append("</div>");
                        Bullets(sb, e.Details);
                    });
            });

        if (cv.Certifications.Any(c => !string.IsNullOrWhiteSpace(c)))
            Section("certs", () =>
            {
                foreach (var c in cv.Certifications.Where(c => !string.IsNullOrWhiteSpace(c)))
                    RailEntry("", () => sb.Append($@"<div class=""role"">{E2(c)}</div>"));
            });

        if (cv.SkillGroups.Any(g => g.Skills.Count > 0))
            Section("skills", () =>
            {
                foreach (var g in cv.SkillGroups.Where(g => g.Skills.Count > 0))
                {
                    sb.Append($@"<div class=""entry""><div class=""glabel"">{E2(g.Label)}</div><div>");
                    sb.Append(E2(string.Join(", ", g.Skills.Where(s => !string.IsNullOrWhiteSpace(s)))));
                    sb.Append("</div></div>");
                }
            });

        if (cv.Languages.Any(l => !string.IsNullOrWhiteSpace(l)))
            Section("languages", () =>
            {
                sb.Append($@"<div class=""entry""><div class=""glabel""></div><div>");
                sb.Append(E2(string.Join(" · ", cv.Languages.Where(l => !string.IsNullOrWhiteSpace(l)))));
                sb.Append("</div></div>");
            });

        return sb.ToString();
    }

    // =====================================================================================
    // Stationery: centred two-tone name (last word italic accent), small caps, double rule.
    // =====================================================================================
    private static string StationeryBody(CvDocument cv)
    {
        string lang = cv.Lang;
        var sb = new StringBuilder();

        var words = (cv.Header.FullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string nameHtml = words.Length > 1
            ? E2(string.Join(' ', words[..^1])) + " <em>" + E2(words[^1]) + "</em>"
            : E2(cv.Header.FullName);

        sb.Append("<header>");
        sb.Append(PhotoImg(cv));   // centred circle above the name
        sb.Append($@"<h1>{nameHtml}</h1>");
        if (!string.IsNullOrWhiteSpace(cv.Header.Title))
            sb.Append($@"<div class=""title"">{E2(cv.Header.Title)}</div>");
        var contact = ContactParts(cv.Header);
        if (contact.Count > 0)
            sb.Append($@"<div class=""contact"">{string.Join(" &nbsp;·&nbsp; ", contact)}</div>");
        sb.Append(@"<div class=""rule""></div></header>");

        if (!string.IsNullOrWhiteSpace(cv.Summary))
            sb.Append($@"<h2>{H("summary", lang)}</h2><p class=""summary"">{E2(cv.Summary)}</p>");

        void Entry(string role, string org, string loc, string dates, List<string>? bullets, string? link = null, string? desc = null)
        {
            sb.Append(@"<div class=""entry""><div class=""ehead""><div>");
            sb.Append(string.IsNullOrWhiteSpace(link)
                ? $@"<span class=""role"">{E2(role)}</span>"
                : $@"<a class=""role"" href=""{E2(link)}"">{E2(role)}</a>");
            if (!string.IsNullOrWhiteSpace(org)) sb.Append($@" <span class=""co"">· {E2(org)}</span>");
            if (!string.IsNullOrWhiteSpace(loc)) sb.Append($@" <span class=""co"">· {E2(loc)}</span>");
            if (!string.IsNullOrWhiteSpace(desc)) sb.Append($@" <span class=""co"">— {E2(desc)}</span>");
            sb.Append("</div>");
            if (dates.Length > 0) sb.Append($@"<div class=""dates"">{dates}</div>");
            sb.Append("</div>");
            if (bullets is not null) Bullets(sb, bullets);
            sb.Append("</div>");
        }

        if (cv.Experience.Count > 0)
        {
            sb.Append($@"<h2>{H("experience", lang)}</h2>");
            foreach (var e in cv.Experience) Entry(e.Role, e.Company, e.Location, Dates(e.Start, e.End, lang), e.Bullets);
        }
        if (cv.Projects.Count > 0)
        {
            sb.Append($@"<h2>{H("projects", lang)}</h2>");
            foreach (var p in cv.Projects) Entry(p.Name, "", "", "", p.Bullets, p.Link, p.Description);
        }
        if (cv.Education.Count > 0)
        {
            sb.Append($@"<h2>{H("education", lang)}</h2>");
            foreach (var e in cv.Education) Entry(e.Degree, e.School, e.Location, Dates(e.Start, e.End, lang), e.Details);
        }
        if (cv.Certifications.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            sb.Append($@"<h2>{H("certs", lang)}</h2>");
            foreach (var c in cv.Certifications.Where(c => !string.IsNullOrWhiteSpace(c)))
                sb.Append($@"<div class=""skills"">{E2(c)}</div>");
        }
        if (cv.SkillGroups.Any(g => g.Skills.Count > 0))
        {
            sb.Append($@"<h2>{H("skills", lang)}</h2>");
            foreach (var g in cv.SkillGroups.Where(g => g.Skills.Count > 0))
            {
                sb.Append(@"<div class=""skills"">");
                if (!string.IsNullOrWhiteSpace(g.Label)) sb.Append($@"<span class=""glabel"">{E2(g.Label)}</span> ");
                sb.Append(E2(string.Join(", ", g.Skills.Where(s => !string.IsNullOrWhiteSpace(s)))));
                sb.Append("</div>");
            }
        }
        if (cv.Languages.Any(l => !string.IsNullOrWhiteSpace(l)))
        {
            sb.Append($@"<h2>{H("languages", lang)}</h2><div class=""skills"">");
            sb.Append(E2(string.Join(" · ", cv.Languages.Where(l => !string.IsNullOrWhiteSpace(l)))));
            sb.Append("</div>");
        }

        return sb.ToString();
    }

    // =====================================================================================
    // Panel: dark side panel (monogram, contact, chips, languages, certs) + light main column.
    // =====================================================================================
    private static string PanelBody(CvDocument cv)
    {
        string lang = cv.Lang;
        var sb = new StringBuilder();
        sb.Append(@"<div class=""wrap""><aside>");
        string photo = PhotoImg(cv);
        sb.Append(photo.Length > 0 ? photo                       // circular photo replaces the monogram
            : $@"<div class=""mark"">{E2(Initials(cv.Header.FullName))}</div>");

        var contact = ContactParts(cv.Header);
        if (contact.Count > 0)
        {
            sb.Append($@"<div class=""blk""><h3>{H("contact", lang)}</h3>");
            foreach (var c in contact) sb.Append($@"<div class=""citem"">{c}</div>");
            sb.Append("</div>");
        }
        if (cv.SkillGroups.Any(g => g.Skills.Count > 0))
        {
            sb.Append($@"<div class=""blk""><h3>{H("skills", lang)}</h3>");
            foreach (var g in cv.SkillGroups.Where(g => g.Skills.Count > 0))
            {
                if (!string.IsNullOrWhiteSpace(g.Label)) sb.Append($@"<div class=""glabel"">{E2(g.Label)}</div>");
                sb.Append("<div>");
                foreach (var s in g.Skills.Where(s => !string.IsNullOrWhiteSpace(s)))
                    sb.Append($@"<span class=""chip"">{E2(s)}</span>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }
        if (cv.Languages.Any(l => !string.IsNullOrWhiteSpace(l)))
        {
            sb.Append($@"<div class=""blk""><h3>{H("languages", lang)}</h3>");
            foreach (var l in cv.Languages.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var (name, level) = SplitLang(l);
                sb.Append(level.Length > 0
                    ? $@"<div class=""lang""><span>{E2(name)}</span><b>{E2(level)}</b></div>"
                    : $@"<div class=""citem"">{E2(l)}</div>");
            }
            sb.Append("</div>");
        }
        if (cv.Certifications.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            sb.Append($@"<div class=""blk""><h3>{H("certs", lang)}</h3>");
            foreach (var c in cv.Certifications.Where(c => !string.IsNullOrWhiteSpace(c)))
                sb.Append($@"<div class=""citem"">{E2(c)}</div>");
            sb.Append("</div>");
        }

        sb.Append("</aside><main>");
        sb.Append($@"<h1>{E2(cv.Header.FullName)}</h1>");
        if (!string.IsNullOrWhiteSpace(cv.Header.Title))
            sb.Append($@"<div class=""headline"">{E2(cv.Header.Title)}</div>");

        if (!string.IsNullOrWhiteSpace(cv.Summary))
            sb.Append($@"<h2>{H("summary", lang)}</h2><p class=""summary"">{E2(cv.Summary)}</p>");

        if (cv.Experience.Count > 0)
        {
            sb.Append($@"<h2>{H("experience", lang)}</h2>");
            foreach (var e in cv.Experience)
            {
                sb.Append(@"<div class=""entry"">");
                EHead(sb, e.Role, e.Company, e.Location, Dates(e.Start, e.End, lang));
                Bullets(sb, e.Bullets);
                sb.Append("</div>");
            }
        }
        if (cv.Projects.Count > 0)
        {
            sb.Append($@"<h2>{H("projects", lang)}</h2>");
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
        }
        if (cv.Education.Count > 0)
        {
            sb.Append($@"<h2>{H("education", lang)}</h2>");
            foreach (var e in cv.Education)
            {
                sb.Append(@"<div class=""entry"">");
                EHead(sb, e.Degree, e.School, e.Location, Dates(e.Start, e.End, lang));
                Bullets(sb, e.Details);
                sb.Append("</div>");
            }
        }

        sb.Append("</main></div>");
        return sb.ToString();
    }

    // =====================================================================================
    // CSS per template. Alpha-suffixed hex (#RRGGBBAA) is Chromium-safe.
    // =====================================================================================

    private static string CleanCss(string accent) => $@"
:root{{--accent:{accent};--ink:#1a1a1e;--muted:#5c6370;--line:#d9d7e0}}
@page{{size:A4;margin:14mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10.5pt/1.45 'Segoe UI',Arial,sans-serif}}
header{{margin-bottom:6pt;border-bottom:2pt solid var(--accent);padding-bottom:8pt}}
.photo{{float:right;width:24mm;height:24mm;object-fit:cover;border-radius:50%;margin-left:6mm;border:1.5pt solid var(--line)}}
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

    private static string EditorialCss(string accent) => $@"
:root{{--accent:{accent};--ink:#16151A;--muted:#6B6876;--line:#DEDCE6}}
@page{{size:A4;margin:15mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10pt/1.5 'Segoe UI',Arial,sans-serif}}
header{{display:flex;justify-content:space-between;align-items:flex-end;gap:10mm;margin-bottom:10pt}}
.photo{{width:26mm;height:26mm;object-fit:cover;border-radius:5pt;align-self:flex-start}}
h1{{font-size:34pt;line-height:1.02;margin:0;font-weight:800;letter-spacing:-.035em}}
.title{{font-size:11pt;font-weight:650;color:var(--accent);margin-top:4pt}}
.contact{{text-align:right;font-size:8.5pt;color:var(--muted);line-height:1.65;white-space:nowrap}}
.contact a{{color:var(--ink);text-decoration:none;font-weight:600}}
section{{border-top:2.4pt solid var(--ink);padding-top:5pt;margin-top:12pt}}
h2{{font:700 8.5pt 'Segoe UI',Arial,sans-serif;letter-spacing:.22em;text-transform:uppercase;margin:0 0 8pt;color:var(--ink)}}
.summary{{margin:0;font-size:10.5pt;max-width:150mm}}
.entry{{display:grid;grid-template-columns:27mm 1fr;gap:6mm;margin-bottom:9pt}}
.rail{{font:8.5pt 'Cascadia Code',Consolas,monospace;color:var(--muted);padding-top:1.5pt}}
.role{{font-weight:700;font-size:10.5pt}}
.role a{{color:var(--accent);text-decoration:none}}
.co{{color:var(--muted);font-weight:400}}
ul{{margin:3pt 0 0;padding-left:11pt}}
li{{margin-bottom:2.5pt}}
li::marker{{color:var(--accent)}}
.glabel{{font:700 8.5pt 'Segoe UI',Arial,sans-serif;letter-spacing:.06em;text-transform:uppercase;color:var(--muted);padding-top:1pt}}
a{{color:var(--accent);text-decoration:none}}
@media print{{body{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}";

    private static string TerminalCss(string accent) => $@"
:root{{--accent:{accent};--ink:#1A1922;--muted:#66626F;--line:#E2E0EA;--fence:#F5F4F9}}
@page{{size:A4;margin:14mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10pt/1.5 'Segoe UI',Arial,sans-serif}}
header{{border-left:3pt solid var(--accent);padding-left:12pt;margin-bottom:14pt}}
.photo{{float:right;width:22mm;height:22mm;object-fit:cover;border-radius:6pt;margin-left:6mm;filter:grayscale(1);border:1pt solid var(--line)}}
h1{{font:700 26pt/1.05 'Cascadia Code',Consolas,monospace;margin:0;letter-spacing:-.045em;text-transform:lowercase}}
.meta{{font:9pt 'Cascadia Code',Consolas,monospace;color:var(--muted);margin-top:5pt;text-transform:lowercase}}
.meta b{{color:var(--accent);font-weight:600}}
.contact{{font:8.5pt 'Cascadia Code',Consolas,monospace;color:var(--muted);margin-top:3pt}}
.contact a{{color:var(--ink);text-decoration:none;border-bottom:.6pt solid var(--line)}}
.sep{{color:var(--line)}}
section{{margin:0}}
h2{{font:600 9.5pt 'Cascadia Code',Consolas,monospace;letter-spacing:.04em;color:var(--ink);margin:14pt 0 6pt;
   display:flex;align-items:center;gap:7pt;text-transform:lowercase}}
h2::before{{content:'';width:9pt;height:2.2pt;background:var(--accent)}}
h2::after{{content:'';flex:1;height:.6pt;background:var(--line)}}
.summary{{margin:0}}
.entry{{margin-bottom:9pt}}
.ehead{{display:flex;justify-content:space-between;gap:10pt;align-items:baseline}}
.role{{font-weight:700;font-size:10.5pt;text-decoration:none;color:var(--ink)}}
a.role{{color:var(--accent)}}
.co{{color:var(--muted)}}
.dates{{font:8.5pt 'Cascadia Code',Consolas,monospace;color:var(--accent);white-space:nowrap;text-transform:lowercase}}
ul{{margin:3pt 0 0;padding-left:11pt}}
li{{margin-bottom:2.5pt}}
li::marker{{color:var(--accent);content:'▸  '}}
.fence{{background:var(--fence);border:1pt solid var(--line);border-radius:6pt;padding:7pt 9pt;margin-bottom:5pt;
  font:9pt 'Cascadia Code',Consolas,monospace;line-height:1.7}}
.fence b{{color:var(--accent);font-weight:600}}
a{{color:var(--accent);text-decoration:none}}
@media print{{body{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}";

    private static string StationeryCss(string accent) => $@"
:root{{--accent:{accent};--ink:#201D26;--muted:#6B6876;--line:#DEDCE6}}
@page{{size:A4;margin:16mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10pt/1.52 'Segoe UI',Arial,sans-serif}}
header{{text-align:center;margin-bottom:8pt}}
.photo{{width:26mm;height:26mm;object-fit:cover;border-radius:50%;margin:0 auto 6pt;display:block;border:1.5pt solid var(--line)}}
h1{{font:400 34pt/1.05 Constantia,Cambria,Georgia,serif;margin:0;letter-spacing:.005em}}
h1 em{{font-style:italic;color:var(--accent)}}
.title{{font:italic 11.5pt Constantia,Cambria,Georgia,serif;color:var(--muted);margin-top:4pt}}
.contact{{font-size:8.5pt;color:var(--muted);margin-top:7pt;letter-spacing:.02em}}
.contact a{{color:var(--ink);text-decoration:none;font-weight:600}}
.rule{{border-top:1pt solid var(--ink);border-bottom:.5pt solid var(--ink);height:2.5pt;margin:10pt 0 0}}
h2{{font:600 12pt Constantia,Cambria,Georgia,serif;font-variant:small-caps;letter-spacing:.14em;
   color:var(--ink);margin:15pt 0 7pt;text-align:center}}
h2::after{{content:'';display:block;width:26pt;height:1pt;background:var(--accent);margin:4pt auto 0}}
.summary{{margin:0 auto;font:italic 10.5pt Constantia,Cambria,Georgia,serif;text-align:center;max-width:140mm}}
.entry{{margin-bottom:9pt}}
.ehead{{display:flex;justify-content:space-between;gap:10pt;align-items:baseline}}
.role{{font-weight:700;font-size:10.5pt;text-decoration:none;color:var(--ink)}}
a.role{{color:var(--accent)}}
.co{{color:var(--muted)}}
.dates{{font:italic 9.5pt Constantia,Cambria,Georgia,serif;color:var(--muted);white-space:nowrap}}
ul{{margin:3pt 0 0;padding-left:11pt}}
li{{margin-bottom:2.5pt}}
li::marker{{color:var(--accent)}}
.skills{{margin-bottom:4pt;text-align:center}}
.glabel{{font:600 9.5pt Constantia,Cambria,Georgia,serif;font-variant:small-caps;letter-spacing:.06em;margin-right:4pt}}
a{{color:var(--accent);text-decoration:none}}
@media print{{body{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}";

    private static string PanelCss(string accent) => $@"
:root{{--accent:{accent};--panel:#1E1B26;--ink:#1C1B22;--muted:#66626F;--line:#E2E0EA}}
@page{{size:A4;margin:11mm}}
*{{box-sizing:border-box}}
body{{margin:0;color:var(--ink);font:10pt/1.5 'Segoe UI',Roboto,Arial,sans-serif}}
.wrap{{display:grid;grid-template-columns:58mm 1fr;gap:8mm}}
aside{{background:var(--panel);color:#EDEAF4;border-radius:12px;padding:9mm 6.5mm;min-height:calc(297mm - 24mm)}}
.mark{{width:34pt;height:34pt;border-radius:9pt;background:var(--accent);color:#fff;display:flex;align-items:center;justify-content:center;
  font:800 15pt 'Segoe UI',Arial,sans-serif;letter-spacing:-.02em;margin-bottom:12pt;border:1pt solid rgba(255,255,255,.25)}}
aside .photo{{width:34mm;height:34mm;object-fit:cover;border-radius:50%;display:block;margin:0 auto 12pt;border:2pt solid rgba(255,255,255,.35)}}
aside h3{{font:700 8pt 'Segoe UI',Arial,sans-serif;letter-spacing:.2em;text-transform:uppercase;color:#B8B2C8;margin:0 0 6pt}}
aside .blk{{margin-bottom:13pt}}
aside .citem{{font-size:9pt;margin-bottom:3.5pt;color:#D8D4E4;word-break:break-word}}
aside .citem a{{color:#fff;text-decoration:none;font-weight:600}}
aside .glabel{{font-weight:700;font-size:8.5pt;color:#fff;margin:6pt 0 3pt}}
.chip{{display:inline-block;background:rgba(255,255,255,.07);color:#E4E0F0;border:1pt solid rgba(255,255,255,.28);border-radius:5pt;
  padding:1.5pt 6pt;margin:0 3pt 3.5pt 0;font-size:8.5pt;font-weight:600}}
aside .lang{{display:flex;justify-content:space-between;font-size:9pt;color:#D8D4E4;margin-bottom:3pt}}
aside .lang b{{color:#fff;font-weight:700}}
main{{padding-top:3mm}}
h1{{font-size:29pt;margin:0;letter-spacing:-.025em;font-weight:800;line-height:1.05}}
.headline{{font-size:11.5pt;color:var(--accent);font-weight:650;margin:3pt 0 10pt}}
h2{{display:flex;align-items:center;gap:7pt;font:700 8.5pt 'Segoe UI',Arial,sans-serif;letter-spacing:.18em;text-transform:uppercase;
   color:var(--ink);margin:13pt 0 7pt}}
h2::before{{content:'';width:14pt;height:2.4pt;background:var(--accent)}}
h2::after{{content:'';flex:1;height:.6pt;background:var(--line)}}
.summary{{margin:0}}
.entry{{margin-bottom:9pt}}
.ehead{{display:flex;justify-content:space-between;gap:10pt;align-items:baseline}}
.role{{font-weight:700;font-size:10.5pt;text-decoration:none;color:var(--ink)}}
a.role{{color:var(--accent)}}
.co{{color:var(--muted)}}
.dates{{color:var(--muted);font-size:9pt;white-space:nowrap;font-weight:600}}
ul{{margin:3pt 0 0;padding-left:11pt}}
li{{margin-bottom:2.5pt}}
li::marker{{color:var(--accent)}}
a{{color:var(--accent);text-decoration:none}}
@media print{{body{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}";
}
