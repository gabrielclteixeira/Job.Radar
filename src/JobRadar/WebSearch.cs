using System.Net;
using System.Text.RegularExpressions;

namespace JobRadar;

public record WebResult(string Title, string Url, string Snippet);

/// <summary>
/// Minimal, key-free web search via DuckDuckGo's HTML endpoint. Best-effort: returns an empty list
/// if the endpoint is unreachable or its markup changes. Used to give a local (offline) model some
/// real context to summarise.
/// </summary>
public static class WebSearch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static WebSearch()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124 Safari/537.36");
    }

    public static async Task<List<WebResult>> SearchAsync(string query, int max = 6, CancellationToken ct = default)
    {
        // DuckDuckGo's HTML endpoint throttles repeated scraping and then returns an empty page.
        // One retry after a short pause rides out most transient throttling.
        var list = await AttemptAsync(query, max, ct);
        if (list.Count == 0)
        {
            try { await Task.Delay(700, ct); } catch { }
            list = await AttemptAsync(query, max, ct);
        }
        return list;
    }

    private static async Task<List<WebResult>> AttemptAsync(string query, int max, CancellationToken ct)
    {
        var list = new List<WebResult>();
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query });
            using var resp = await Http.PostAsync("https://html.duckduckgo.com/html/", content, ct);
            if (!resp.IsSuccessStatusCode) return list;
            string html = await resp.Content.ReadAsStringAsync(ct);

            var links = Regex.Matches(html, "<a[^>]*class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            var snips = Regex.Matches(html, "class=\"result__snippet\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            for (int i = 0; i < links.Count && list.Count < max; i++)
            {
                string title = Strip(links[i].Groups[2].Value);
                if (string.IsNullOrWhiteSpace(title)) continue;
                string url = DecodeUrl(links[i].Groups[1].Value);
                string snippet = i < snips.Count ? Strip(snips[i].Groups[1].Value) : "";
                if (snippet.Length > 240) snippet = snippet[..240] + "…";
                list.Add(new WebResult(title, url, snippet));
            }
        }
        catch { /* best-effort */ }
        return list;
    }

    private static string DecodeUrl(string href)
    {
        var m = Regex.Match(href, "uddg=([^&]+)");
        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        return href.StartsWith("//") ? "https:" + href : href;
    }

    private static string Strip(string s)
        => WebUtility.HtmlDecode(Regex.Replace(s, "<.*?>", "")).Trim();
}
