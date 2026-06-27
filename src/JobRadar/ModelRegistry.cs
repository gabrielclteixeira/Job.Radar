using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobRadar;

/// <summary>One model in the live browser. <see cref="Source"/> is "ollama" or "lmstudio"; <see cref="Repo"/>
/// is the Ollama library name or the Hugging Face repo id; <see cref="Sizes"/> are Ollama tag sizes or
/// (loaded lazily) HF GGUF quant filenames.</summary>
public record RegistryModel(
    string Source, string Name, string Repo, string Description,
    List<string> Capabilities, List<string> Sizes, string Pulls, string Updated);

/// <summary>
/// Live model discovery, keyless and dependency-free (plain HTTP — no `lms`/`ollama` CLI needed):
///   • Ollama  → scrape ollama.com/search (stable x-test-* hooks).
///   • LM Studio → the curated catalog IS the `lmstudio-community` Hugging Face org; queried via the HF API,
///     which also hands us direct GGUF download URLs. Best-effort: returns [] on any failure.
/// </summary>
public static class ModelRegistry
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    // ---- Ollama: scrape ollama.com/search ----
    public static async Task<List<RegistryModel>> SearchOllamaAsync(string query, CancellationToken ct = default)
    {
        var list = new List<RegistryModel>();
        try
        {
            string html = await GetAsync("https://ollama.com/search?q=" + Uri.EscapeDataString(query ?? ""), ct);
            // Each result is a <li x-test-model …> card; that attribute appears once per card.
            foreach (var card in html.Split("x-test-model").Skip(1))
            {
                if (list.Count >= 25) break;
                var nameM = Regex.Match(card, @"href=""/library/([^""?#]+)""");
                if (!nameM.Success) continue;
                string repo = nameM.Groups[1].Value.Trim();
                string title = One(card, @"x-test-search-response-title[^>]*>([^<]+)<");
                var caps = All(card, @"x-test-capability[^>]*>([^<]+)<").Select(c => c.ToLowerInvariant()).Distinct().ToList();
                var sizes = All(card, @"x-test-size[^>]*>([^<]+)<").Distinct().ToList();
                list.Add(new RegistryModel(
                    "ollama", string.IsNullOrWhiteSpace(title) ? repo : title, repo,
                    Strip(One(card, @"<p[^>]*>(.*?)</p>")), caps, sizes,
                    One(card, @"x-test-pull-count[^>]*>([^<]+)<"), One(card, @"x-test-updated[^>]*>([^<]+)<")));
            }
        }
        catch { /* best-effort */ }
        return list;
    }

    // ---- LM Studio: the lmstudio-community HF org (its curated catalog), via the HF API ----
    public static async Task<List<RegistryModel>> SearchLmStudioAsync(string query, CancellationToken ct = default)
    {
        var list = new List<RegistryModel>();
        try
        {
            // full=true so each entry carries tags (→ capability badges) and lastModified.
            string url = "https://huggingface.co/api/models?author=lmstudio-community&filter=gguf&sort=downloads&direction=-1&limit=25&full=true";
            if (!string.IsNullOrWhiteSpace(query)) url += "&search=" + Uri.EscapeDataString(query);
            using var doc = JsonDocument.Parse(await GetAsync(url, ct));
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= 25) break;
                string repo = Str(m, "id");
                if (string.IsNullOrWhiteSpace(repo)) continue;
                string name = Regex.Replace(repo.Contains('/') ? repo[(repo.IndexOf('/') + 1)..] : repo,
                    @"-GGUF$", "", RegexOptions.IgnoreCase);
                var tags = m.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : new List<string>();
                long dl = m.TryGetProperty("downloads", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0;
                list.Add(new RegistryModel("lmstudio", name, repo, "", CapsFromTags(tags), new(), Human(dl), ShortDate(Str(m, "lastModified"))));
            }
        }
        catch { /* best-effort */ }
        return list;
    }

    /// <summary>Lazily lists a HF repo's GGUF quant files (the size/quant pills for an LM Studio result).</summary>
    public static async Task<List<string>> GetGgufFilesAsync(string repo, CancellationToken ct = default)
    {
        var files = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(await GetAsync("https://huggingface.co/api/models/" + repo, ct));
            if (doc.RootElement.TryGetProperty("siblings", out var sib) && sib.ValueKind == JsonValueKind.Array)
                foreach (var s in sib.EnumerateArray())
                {
                    string f = Str(s, "rfilename");
                    // Skip mmproj-* (vision projector sidecars, not a loadable model on their own).
                    if (f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
                        files.Add(f);
                }
        }
        catch { /* best-effort */ }
        return files;
    }

    // ---- helpers ----
    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UA);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(8000);
        using var resp = await Http.SendAsync(req, cts.Token);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(cts.Token) : "";
    }

    private static List<string> CapsFromTags(List<string> tags)
    {
        bool Has(params string[] ks) => tags.Any(tg => ks.Any(k => tg.Contains(k, StringComparison.OrdinalIgnoreCase)));
        var caps = new List<string>();
        if (Has("vision", "image-text", "multimodal")) caps.Add("vision");
        if (Has("tool", "function")) caps.Add("tools");
        if (Has("reasoning", "thinking")) caps.Add("reasoning");
        return caps;
    }

    private static string Human(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1_000 ? $"{n / 1_000.0:0.#}K" : n.ToString();
    private static string ShortDate(string iso) => DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToString("yyyy-MM-dd") : "";
    private static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string One(string s, string pat) { var m = Regex.Match(s, pat, RegexOptions.Singleline); return m.Success ? m.Groups[1].Value.Trim() : ""; }
    private static IEnumerable<string> All(string s, string pat) => Regex.Matches(s, pat, RegexOptions.Singleline).Select(m => m.Groups[1].Value.Trim());
    private static string Strip(string s) => WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<.*?>", "")).Trim();
}
