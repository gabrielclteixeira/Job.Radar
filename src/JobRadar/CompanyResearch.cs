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

        // 2) Ask the model to synthesise from the snippets only — as Markdown for readability.
        string prompt =
$@"You are given web search snippets about an employer. Using ONLY this information (do not invent),
write a SHORT, honest briefing for a candidate considering this company. Format it as **Markdown**:
- Use bold sub-headings: **Reputation**, **Salary**, **Bottom line**.
- Under Reputation, a bullet list of concrete pros and cons.
- Under Salary, the comparable range for the role (numbers/currency when present; say if unknown).
- Bottom line: one sentence.
Cite sources inline as [n]. Keep it tight. If the snippets are thin or off-topic, say so plainly.

Company: {company}
Role: {role}
Location: {location ?? "—"}

== SEARCH RESULTS ==
{sb}";

        string? summary = await LlmClient.CompleteAsync(llm, prompt, ct);
        if (string.IsNullOrWhiteSpace(summary)) return null;

        // Append clickable Markdown sources.
        var outp = new StringBuilder(summary.Trim());
        outp.AppendLine().AppendLine().AppendLine("**Fontes**").AppendLine();
        for (int i = 0; i < results.Count; i++)
            outp.AppendLine($"{i + 1}. [{Host(results[i].Url)}]({results[i].Url})");
        return outp.ToString();
    }

    private static string Host(string url)
    {
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch { return url; }
    }
}
