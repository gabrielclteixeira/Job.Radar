using System.Text;

namespace JobRadar;

/// <summary>One turn of the Coach conversation, provider-agnostic. Role is "user" or "assistant";
/// ImagePaths are absolute paths to screenshots the user attached to that turn.</summary>
public sealed record ChatMessage(string Role, string Text, IReadOnlyList<string>? ImagePaths = null)
{
    public bool HasImages => ImagePaths is { Count: > 0 };
}

/// <summary>
/// Core of the in-app career-coach chat: builds the grounded system prompt (candidate profile +
/// matched-market signal + optional cached company research) and caps the transcript so it fits both
/// the Windows argv limit (the Claude CLI prompt travels as a command-line argument) and the small
/// context windows of local models.
/// </summary>
public static class Coach
{
    // Windows CreateProcess caps the command line at ~32k chars and the CLI prompt travels in argv —
    // keep system+transcript comfortably under it. The OpenAI budget (~6k tokens) keeps 8k-ctx local
    // models from evicting the system prompt.
    public const int CliBudget = 20_000;
    public const int OpenAiBudget = 24_000;
    public const int TurnCharCap = 4_000;

    /// <summary>Persona + grounding blocks. Empty market/company blocks are simply omitted.</summary>
    public static string BuildSystemPrompt(UserProfile profile, string marketContext, string? companyContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a candid, practical career coach helping a job seeker with applications, " +
            "salary questions and interview answers. Be concrete and honest; when you don't know, say so — " +
            "never invent figures. Ground every piece of advice in the candidate context below. " +
            $"Use short markdown lists when helpful. Write in {Loc.Instance.T("ai.lang")}.");
        sb.AppendLine();
        sb.AppendLine("== CANDIDATE ==");
        sb.AppendLine(profile.ToScoringText().Trim());
        if (!string.IsNullOrWhiteSpace(marketContext))
        {
            sb.AppendLine();
            sb.AppendLine("== THE CANDIDATE'S MATCHED MARKET (jobs this app scored — a signal, not authoritative) ==");
            sb.AppendLine(marketContext.Trim());
        }
        if (!string.IsNullOrWhiteSpace(companyContext))
        {
            sb.AppendLine();
            sb.AppendLine("== COMPANY UNDER DISCUSSION (cached research, may be up to 7 days old) ==");
            sb.AppendLine(companyContext.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("If the user shares a screenshot of an application question, draft the answer AS the " +
            "candidate (first person), grounded in the profile above — honest about experience levels, " +
            "never overclaiming.");
        return sb.ToString();
    }

    /// <summary>Compact plain-text block (~2.5k chars max) from the cached company research —
    /// report and/or per-job brief, both optional.</summary>
    public static string FormatCompanyContext(string name, CompanyReport? report, CompanyBrief? brief)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Company: {name}");
        if (report is not null)
        {
            if (report.HasRating) sb.AppendLine($"Rating: {report.RatingText}" +
                (report.HasRatingSource ? $" on {report.RatingSource}" : "") +
                (report.HasReviewCount ? $" ({report.ReviewsText} reviews)" : ""));
            if (report.HasRecommend) sb.AppendLine($"Would recommend: {report.RecommendText}");
            if (report.HasFirmographics) sb.AppendLine($"Firmographics: {report.Firmographics}");
            if (report.HasPayBand) sb.AppendLine($"Typical pay: {report.PayBand}" +
                (report.HasPayRole ? $" ({report.PayRole})" : ""));
            foreach (var l in report.Layoffs.Take(3)) sb.AppendLine($"Layoffs: {l.Display}");
            foreach (var s in report.Signals.Take(3)) sb.AppendLine($"Signal: {s}");
            if (report.HasTechStack) sb.AppendLine($"Tech stack seen in their postings: {report.TechStackText}");
            if (report.HasPros) sb.AppendLine("Pros: " + string.Join("; ", report.Pros.Take(3)));
            if (report.HasCons) sb.AppendLine("Cons: " + string.Join("; ", report.Cons.Take(3)));
            if (report.HasRedFlags) sb.AppendLine("Red flags: " + string.Join("; ", report.RedFlags.Take(3)));
            if (report.HasBottomLine) sb.AppendLine($"Bottom line: {report.BottomLine}");
            if (report.HasConfidence) sb.AppendLine($"Data confidence: {report.Confidence}");
        }
        if (brief is not null)
        {
            if (brief.HasSalaryFound) sb.AppendLine($"Salary figures found: {brief.SalaryFound}");
            if (brief.HasSalaryExpectation) sb.AppendLine($"Salary expectation for the candidate: {brief.SalaryExpectation}");
            if (brief.HasSalaryRationale) sb.AppendLine($"Rationale: {brief.SalaryRationale}");
            if (report is null)
            {
                if (brief.HasPros) sb.AppendLine("Pros: " + string.Join("; ", brief.Pros.Take(3)));
                if (brief.HasCons) sb.AppendLine("Cons: " + string.Join("; ", brief.Cons.Take(3)));
                if (brief.HasReputationNote) sb.AppendLine($"Reputation: {brief.ReputationNote}");
                if (brief.HasBottomLine) sb.AppendLine($"Bottom line: {brief.BottomLine}");
            }
        }
        string text = sb.ToString().Trim();
        return text.Length > 2_500 ? text[..2_500] : text;
    }

    /// <summary>
    /// Caps the transcript to <paramref name="budget"/> chars (on top of the system prompt): each turn
    /// is truncated to <see cref="TurnCharCap"/>, then turns are kept newest-first until the budget is
    /// spent. The LAST user message always survives (truncated harder if it alone busts the budget).
    /// Image paths count toward the budget (they travel in the CLI argv).
    /// </summary>
    public static List<ChatMessage> Cap(IReadOnlyList<ChatMessage> history, int systemChars, int budget)
    {
        int remaining = Math.Max(1_000, budget - systemChars);
        var kept = new List<ChatMessage>();
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            string text = m.Text.Length > TurnCharCap ? m.Text[..TurnCharCap] + "…" : m.Text;
            int cost = text.Length + (m.ImagePaths?.Sum(p => p.Length + 2) ?? 0) + 16;
            if (cost > remaining)
            {
                if (kept.Count == 0)   // last message alone busts the budget — keep a hard-truncated copy
                {
                    int max = Math.Max(500, remaining - 16);
                    kept.Add(m with { Text = text.Length > max ? text[..max] + "…" : text });
                }
                break;
            }
            kept.Add(m with { Text = text });
            remaining -= cost;
        }
        kept.Reverse();
        return kept;
    }
}
