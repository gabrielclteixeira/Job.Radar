using System.Net;
using System.Text.RegularExpressions;

namespace JobRadar;

public record WebResult(string Title, string Url, string Snippet);

/// <summary>
/// Minimal, key-free web search. Primary engine is Mojeek (independent, scraping-tolerant); DuckDuckGo
/// (HTML then Lite) is a fallback. Browser-like headers + a rotating user-agent + a backoff. DuckDuckGo
/// frequently returns an HTTP 202 "anomaly" anti-bot page from data-centre/repeat IPs, hence Mojeek first.
/// Best-effort: returns an empty list if every engine fails.
/// </summary>
public static class WebSearch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private sealed record Engine(bool Get, string Url);

    private static readonly Engine[] Engines =
    {
        new(true,  "https://www.mojeek.com/search?q="),       // primary — works where DDG blocks
        new(false, "https://html.duckduckgo.com/html/"),       // fallback
        new(false, "https://lite.duckduckgo.com/lite/"),       // fallback
    };

    // (link-with-href+title, snippet) regex pairs — tried in order against whatever HTML comes back.
    private static readonly (Regex Link, Regex Snip)[] Parsers =
    {
        (new Regex("<a class=\"title\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline), new Regex("<p class=\"s\">(.*?)</p>", RegexOptions.Singleline)),                 // Mojeek
        (new Regex("<a[^>]*class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline), new Regex("class=\"result__snippet\"[^>]*>(.*?)</a>", RegexOptions.Singleline)), // DDG html
        (new Regex("<a[^>]*class=\"result-link\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline), new Regex("class=\"result-snippet\"[^>]*>(.*?)</td>", RegexOptions.Singleline)), // DDG lite
    };

    private static readonly string[] UserAgents =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:124.0) Gecko/20100101 Firefox/124.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15",
    };

    // Per-request budget: a real result returns in well under a second, so a hung/tarpitted engine
    // (Mojeek holds the connection when throttling) must be abandoned fast — otherwise the HttpClient's
    // own timeout (kept high for other callers) would let each failing attempt run for ~20s.
    private const int PerRequestMs = 5000;

    public static async Task<List<WebResult>> SearchAsync(string query, int max = 6, CancellationToken ct = default)
    {
        // One quick pass over the engines (primary Mojeek first). No extra retry pass — a throttled
        // engine just tarpits to the per-request timeout, so retrying only doubles the wait.
        for (int e = 0; e < Engines.Length; e++)
        {
            var list = await AttemptAsync(Engines[e], query, max, UserAgents[e % UserAgents.Length], ct);
            if (list.Count > 0) return list;
        }
        return new List<WebResult>();
    }

    private static async Task<List<WebResult>> AttemptAsync(Engine engine, string query, int max, string ua, CancellationToken ct)
    {
        var list = new List<WebResult>();
        try
        {
            using var req = engine.Get
                ? new HttpRequestMessage(HttpMethod.Get, engine.Url + Uri.EscapeDataString(query))
                : new HttpRequestMessage(HttpMethod.Post, engine.Url)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query }),
                };
            req.Headers.TryAddWithoutValidation("User-Agent", ua);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PerRequestMs);
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return list;            // e.g. DDG's 202 anti-bot page
            string html = await resp.Content.ReadAsStringAsync(ct);

            foreach (var (linkRx, snipRx) in Parsers)
            {
                var links = linkRx.Matches(html);
                if (links.Count == 0) continue;
                var snips = snipRx.Matches(html);
                for (int i = 0; i < links.Count && list.Count < max; i++)
                {
                    string title = Strip(links[i].Groups[2].Value);
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    string url = DecodeUrl(links[i].Groups[1].Value);
                    string snippet = i < snips.Count ? Strip(snips[i].Groups[1].Value) : "";
                    if (snippet.Length > 240) snippet = snippet[..240] + "…";
                    list.Add(new WebResult(title, url, snippet));
                }
                break; // first parser that matched wins
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
