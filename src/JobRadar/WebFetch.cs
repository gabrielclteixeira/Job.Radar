namespace JobRadar;

/// <summary>
/// Keyless page fetch via Jina Reader (https://r.jina.ai/{url}) — turns any URL, including JS-heavy pages,
/// into clean markdown the model can read. Search SNIPPETS leave the real figures (e.g. salary tables)
/// behind the link; this reads the page so the model gets the actual numbers. No key, no signup (~20 RPM,
/// far more than we need). Best-effort: returns "" on any failure so callers can degrade gracefully.
/// </summary>
public static class WebFetch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    // Jina renders pages (often via a headless browser, ~8s typical), so allow more time than a search,
    // but still abandon a stuck fetch rather than hang the whole career-plan flow.
    private const int PerRequestMs = 14000;

    /// <summary>Fetches <paramref name="url"/> as clean markdown (truncated to <paramref name="maxChars"/>).
    /// Returns "" if the fetch fails or the URL is unusable.</summary>
    public static async Task<string> FetchMarkdownAsync(string url, int maxChars = 2500, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "";
        try
        {
            // Jina takes the target URL appended RAW (not percent-encoded) after the host.
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://r.jina.ai/" + url);
            req.Headers.TryAddWithoutValidation("Accept", "text/plain");
            req.Headers.TryAddWithoutValidation("User-Agent", "JobRadar/1.0");
            req.Headers.TryAddWithoutValidation("X-Return-Format", "markdown");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PerRequestMs);
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return "";
            string text = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            return text.Length > maxChars ? text[..maxChars] + "…" : text;
        }
        catch { return ""; }
    }
}
