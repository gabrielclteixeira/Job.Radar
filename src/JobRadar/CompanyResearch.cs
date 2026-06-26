using System.Text;

namespace JobRadar;

/// <summary>
/// Researches an employer by running a couple of web searches (reviews + comparable salaries) and
/// handing the snippets to the configured LLM to summarise. Works with a local model too: the model
/// stays offline and only summarises the fetched text. Returns null if nothing could be gathered.
/// </summary>
public static class CompanyResearch
{
    public static async Task<string?> ResearchAsync(
        ClaudeConfig llm, string company, string role, string? location, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(company)) return null;

        // 1) Gather context from the web (key-free, best-effort).
        var results = new List<WebResult>();
        results.AddRange(await WebSearch.SearchAsync($"{company} employee reviews", 5, ct));
        results.AddRange(await WebSearch.SearchAsync($"{company} {role} salary", 5, ct));

        // Dedupe by URL, keep order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        results = results.Where(r => !string.IsNullOrWhiteSpace(r.Url) && seen.Add(r.Url)).Take(8).ToList();
        if (results.Count == 0) return null;

        var sb = new StringBuilder();
        for (int i = 0; i < results.Count; i++)
            sb.AppendLine($"[{i + 1}] {results[i].Title}\n{results[i].Snippet}\n{results[i].Url}\n");

        // 2) Ask the model to synthesise from the snippets only.
        string prompt =
$@"You are given web search snippets about an employer. Using ONLY this information (do not invent),
write a SHORT, honest briefing for a candidate considering this company. Plain text, no markdown headers.
Cover, if the snippets support it:
- Reputation / employee reviews: a few concrete pros and cons.
- Comparable salary range for the role (give numbers/currency when present; say if unknown).
- A one-line bottom line.
Cite sources inline as [n]. If the snippets are thin or off-topic, say so plainly rather than guessing.

Company: {company}
Role: {role}
Location: {location ?? "—"}

== SEARCH RESULTS ==
{sb}";

        string? summary = await LlmClient.CompleteAsync(llm, prompt, ct);
        if (string.IsNullOrWhiteSpace(summary)) return null;

        var outp = new StringBuilder(summary.Trim());
        outp.AppendLine().AppendLine().Append("Fontes:");
        for (int i = 0; i < results.Count; i++) outp.AppendLine().Append($"[{i + 1}] {results[i].Url}");
        return outp.ToString();
    }
}
