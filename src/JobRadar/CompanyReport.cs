namespace JobRadar;

/// <summary>
/// Structured employer-health signals for one company, aggregated from public web-search snippets.
/// Numbers are deliberately nullable / strings empty: that means "unknown" (the public data was too
/// thin) — never a fabricated value. Every signal is source-linked (<see cref="Sources"/>) and the
/// model self-tags an overall <see cref="Confidence"/>. Cached per company with an "as of" date.
/// </summary>
public class CompanyReport
{
    public string Company { get; set; } = "";

    // Employee satisfaction (Glassdoor / kununu / Indeed ★).
    public double? Rating { get; set; }            // e.g. 3.9 — null = unknown
    public double RatingScale { get; set; } = 5;   // out of 5
    public int? ReviewCount { get; set; }
    public string RatingSource { get; set; } = ""; // "Glassdoor" / "kununu" / …
    public int? RecommendPct { get; set; }         // % who'd recommend to a friend
    public int? CeoApprovalPct { get; set; }       // % CEO approval

    // Sub-ratings (0–5), best-effort from Glassdoor/Comparably overview pages.
    public double? WorkLifeRating { get; set; }
    public double? CultureRating { get; set; }
    public double? CareerRating { get; set; }
    public double? ManagementRating { get; set; }
    public double? CompensationRating { get; set; }
    public double? DiversityRating { get; set; }
    public int? ENps { get; set; }                 // Comparably employee net promoter score (-100..100)
    public string InterviewDifficulty { get; set; } = ""; // e.g. "Average · 2.9/5"

    // Recent layoffs (date + scale + source), most recent first.
    public List<LayoffEvent> Layoffs { get; set; } = new();

    // Recent notable events — funding, acquisition, hiring freeze, leadership change (with inline [n]).
    public List<string> Signals { get; set; } = new();

    // Typical pay at this company for the candidate's role, anchored to the local market.
    public string PayBand { get; set; } = "";      // "€38k–€52k/yr" — empty = unknown
    public string PayRole { get; set; } = "";      // the role the band refers to

    // Average tenure — the weakest signal (LinkedIn-only), usually unknown.
    public string Tenure { get; set; } = "";

    // Firmographics — preferentially from Wikidata (authoritative, keyless), else from snippets.
    public string Industry { get; set; } = "";
    public string CompanySize { get; set; } = "";  // e.g. "1,000–5,000 employees"
    public string Headquarters { get; set; } = "";
    public string Founded { get; set; } = "";       // year, e.g. "1996"
    public string Ceo { get; set; } = "";
    public string Website { get; set; } = "";

    // Tech stack seen in THIS employer's own job postings the radar scored (grounded, candidate-relevant).
    public List<string> TechStack { get; set; } = new();

    public List<string> Pros { get; set; } = new();
    public List<string> Cons { get; set; } = new();
    public List<string> RedFlags { get; set; } = new();
    public string BottomLine { get; set; } = "";

    public string Confidence { get; set; } = "";   // unknown | low | medium | high (model self-tagged)
    public string RawFallback { get; set; } = "";  // set when JSON parsing failed
    public List<SourceRef> Sources { get; set; } = new();
    public string AsOfUtc { get; set; } = "";       // ISO8601 when this report was built

    // ---- view helpers ----
    public bool HasRating => Rating is > 0;
    public string RatingText => HasRating ? $"{Rating:0.0} / {RatingScale:0}" : "";
    public bool HasRatingSource => !string.IsNullOrWhiteSpace(RatingSource);
    public bool HasReviewCount => ReviewCount is > 0;
    public string ReviewsText => ReviewCount is > 0 ? $"{ReviewCount:N0}" : "";
    /// <summary>True when we found a review count but no aggregate star average — show the count on its own.</summary>
    public bool ShowReviewsOnly => HasReviewCount && !HasRating;
    public bool HasSatisfaction => HasRating || HasReviewCount;
    public bool HasRecommend => RecommendPct is > 0;
    public string RecommendText => RecommendPct is int p ? $"{p}%" : "";
    public bool HasCeoApproval => CeoApprovalPct is > 0;
    public string CeoApprovalText => CeoApprovalPct is int c ? $"{c}%" : "";

    // Sub-ratings (each shown as "label  value/5" when present).
    public bool HasWorkLife => WorkLifeRating is > 0;
    public bool HasCulture => CultureRating is > 0;
    public bool HasCareer => CareerRating is > 0;
    public bool HasManagement => ManagementRating is > 0;
    public bool HasCompensation => CompensationRating is > 0;
    public bool HasDiversity => DiversityRating is > 0;
    public bool HasSubRatings => HasWorkLife || HasCulture || HasCareer || HasManagement || HasCompensation || HasDiversity;
    public string WorkLifeText => WorkLifeRating is double a ? $"{a:0.0}" : "";
    public string CultureText => CultureRating is double a ? $"{a:0.0}" : "";
    public string CareerText => CareerRating is double a ? $"{a:0.0}" : "";
    public string ManagementText => ManagementRating is double a ? $"{a:0.0}" : "";
    public string CompensationText => CompensationRating is double a ? $"{a:0.0}" : "";
    public string DiversityText => DiversityRating is double a ? $"{a:0.0}" : "";
    public bool HasENps => ENps.HasValue;
    public string ENpsText => ENps is int n ? n.ToString() : "";
    public bool HasInterviewDifficulty => !string.IsNullOrWhiteSpace(InterviewDifficulty);

    public bool HasLayoffs => Layoffs.Count > 0;
    public bool HasSignals => Signals.Count > 0;
    public bool HasTechStack => TechStack.Count > 0;
    public bool HasPayBand => !string.IsNullOrWhiteSpace(PayBand);
    public bool HasPayRole => !string.IsNullOrWhiteSpace(PayRole);
    public bool HasTenure => !string.IsNullOrWhiteSpace(Tenure);
    public bool HasIndustry => !string.IsNullOrWhiteSpace(Industry);
    public bool HasCompanySize => !string.IsNullOrWhiteSpace(CompanySize);
    public bool HasHeadquarters => !string.IsNullOrWhiteSpace(Headquarters);
    public bool HasFounded => !string.IsNullOrWhiteSpace(Founded);
    public bool HasCeo => !string.IsNullOrWhiteSpace(Ceo);
    public bool HasWebsite => !string.IsNullOrWhiteSpace(Website);
    public bool HasFirmographics => HasIndustry || HasCompanySize || HasHeadquarters || HasFounded;
    public string Firmographics => string.Join("  ·  ",
        new[] { Industry, CompanySize, Headquarters, HasFounded ? $"est. {Founded}" : "" }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
    public string TechStackText => string.Join("  ·  ", TechStack);

    public bool HasPros => Pros.Count > 0;
    public bool HasCons => Cons.Count > 0;
    public bool HasRedFlags => RedFlags.Count > 0;
    public bool HasBottomLine => !string.IsNullOrWhiteSpace(BottomLine);
    public bool HasConfidence => !string.IsNullOrWhiteSpace(Confidence);
    public bool HasFallback => !string.IsNullOrWhiteSpace(RawFallback);
    public bool HasSources => Sources.Count > 0;

    /// <summary>True when nothing structured was extracted — the UI shows an honest "no public data" note.</summary>
    public bool IsThin => !HasSatisfaction && !HasLayoffs && !HasPayBand && !HasTenure
                          && !HasRecommend && !HasCeoApproval && !HasSubRatings && !HasENps
                          && !HasFirmographics && !HasCeo && !HasWebsite && !HasTechStack && !HasSignals
                          && !HasPros && !HasCons && !HasRedFlags && !HasBottomLine && !HasFallback;

    public DateTime? AsOfDate =>
        DateTime.TryParse(AsOfUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}

/// <summary>One dated workforce-reduction event, with the source it came from.</summary>
public class LayoffEvent
{
    public string Period { get; set; } = "";  // "2024", "Q1 2025", "May 2025"
    public string Scale { get; set; } = "";    // "~500 roles", "10% of staff"
    public string Note { get; set; } = "";
    public string Url { get; set; } = "";

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public string Display => string.Join("  ·  ",
        new[] { Period, Scale, Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
