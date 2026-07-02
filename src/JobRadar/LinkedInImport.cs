using System.Text;
using System.Text.Json;

namespace JobRadar;

/// <summary>
/// The paste-and-extract LinkedIn import: the user opens LinkedIn Jobs in THEIR browser (their session),
/// copies the results page(s), pastes into the app, and the model extracts structured jobs into
/// <c>linkedin-jobs.json</c> — which the pipeline already merges (Source="linkedin", dedupe by Url falling
/// back to Title|Company). Nothing is automated against the user's account (ToS-safe by construction) and
/// the extraction runs on the user's own engine. Copied text carries no hrefs, so Url is usually empty.
/// </summary>
public static class LinkedInImport
{
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    // Pasted results pages are big and noisy; extract in bounded chunks so local models finish reliably.
    private const int ChunkChars = 9000;
    private const int MaxChunks = 4;

    /// <summary>Extracts job listings from pasted LinkedIn page text. Returns the jobs found plus an error
    /// message when a chunk failed (partial results still come back). Cancellation propagates.</summary>
    public static async Task<(List<LinkedInJob> jobs, string? error)> ExtractAsync(
        ClaudeConfig llm, string pasted, IProgress<string>? log = null, CancellationToken ct = default)
    {
        pasted = (pasted ?? "").Trim();
        if (pasted.Length == 0) return (new(), null);
        if (pasted.Length > ChunkChars * MaxChunks)
            log?.Report(Loc.Instance.F("linkedin.import.truncated", ChunkChars * MaxChunks));

        var chunks = Chunk(pasted);
        var all = new List<LinkedInJob>();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks.Count > 1) log?.Report(Loc.Instance.F("linkedin.import.chunk", i + 1, chunks.Count));
            string? raw;
            try { raw = await LlmClient.CompleteAsync(llm, BuildPrompt(chunks[i]), ct, json: true); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (Dedupe(all), ex.Message); }
            all.AddRange(Parse(raw));
        }
        return (Dedupe(all), null);
    }

    /// <summary>Merges freshly extracted jobs into the linkedin-jobs.json file without duplicating what a
    /// previous paste already saved. Returns how many were new and the file's total.</summary>
    public static (int added, int total) SaveMerged(string path, List<LinkedInJob> jobs)
    {
        List<LinkedInJob> existing = new();
        try
        {
            if (File.Exists(path))
                existing = JsonSerializer.Deserialize<List<LinkedInJob>>(File.ReadAllText(path), J) ?? new();
        }
        catch { /* corrupt file — recreate it rather than losing the new batch */ }

        var seen = new HashSet<string>(existing.Select(Key));
        int added = 0;
        foreach (var j in jobs)
            if (seen.Add(Key(j))) { existing.Add(j); added++; }
        File.WriteAllText(path, JsonSerializer.Serialize(existing, J));
        return (added, existing.Count);
    }

    public static int CountSaved(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            return JsonSerializer.Deserialize<List<LinkedInJob>>(File.ReadAllText(path), J)?.Count ?? 0;
        }
        catch { return 0; }
    }

    public static void Clear(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    private static string BuildPrompt(string text) =>
$@"You extract job listings from text a user copied from a LinkedIn Jobs page (a results list or a single job page).
Reply with ONLY one valid JSON object, double-quoted keys/values, shape:
{{""jobs"":[{{""title"":""..."",""company"":""..."",""location"":""..."",""url"":""..."",""description"":""...""}}]}}
Rules:
- One entry per DISTINCT job listing in the text. If there are none, reply {{""jobs"":[]}}.
- NEVER invent a URL: set ""url"" to """" unless a real job URL appears verbatim in the text.
- location: copy as written (city / Remote / Hybrid). description: that job's requirements/summary text if present, else """".
- Ignore page chrome: navigation, filters, ads, ""people also viewed"", footers, cookie banners.
== PASTED TEXT ==
{text}";

    private sealed class Dto { public List<ItemDto>? Jobs { get; set; } }
    private sealed class ItemDto
    {
        public string? Title { get; set; }
        public string? Company { get; set; }
        public string? Location { get; set; }
        public string? Url { get; set; }
        public string? Description { get; set; }
    }

    private static List<LinkedInJob> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try
        {
            int a = raw.IndexOf('{'); int b = raw.LastIndexOf('}');
            if (a < 0 || b <= a) return new();
            var dto = JsonSerializer.Deserialize<Dto>(raw[a..(b + 1)], J);
            return (dto?.Jobs ?? new())
                .Where(x => !string.IsNullOrWhiteSpace(x.Title) && !string.IsNullOrWhiteSpace(x.Company))
                .Select(x => new LinkedInJob(null, x.Title!.Trim(), x.Company!.Trim(),
                    (x.Location ?? "").Trim(), (x.Url ?? "").Trim(), (x.Description ?? "").Trim()))
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>Same identity the pipeline's dedupe uses: Url when present, else Title|Company.</summary>
    private static string Key(LinkedInJob j) =>
        !string.IsNullOrWhiteSpace(j.Url) ? j.Url!.Trim().ToLowerInvariant()
        : $"{(j.Title ?? "").Trim().ToLowerInvariant()}|{(j.Company ?? "").Trim().ToLowerInvariant()}";

    private static List<LinkedInJob> Dedupe(List<LinkedInJob> xs)
    {
        var seen = new HashSet<string>();
        var kept = new List<LinkedInJob>();
        foreach (var j in xs) if (seen.Add(Key(j))) kept.Add(j);
        return kept;
    }

    private static List<string> Chunk(string text)
    {
        var chunks = new List<string>();
        if (text.Length <= ChunkChars) { chunks.Add(text); return chunks; }
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (sb.Length + line.Length + 1 > ChunkChars && sb.Length > 0)
            {
                chunks.Add(sb.ToString()); sb.Clear();
                if (chunks.Count == MaxChunks) return chunks;   // hard cap — the caller warned about truncation
            }
            sb.AppendLine(line);
        }
        if (sb.Length > 0 && chunks.Count < MaxChunks) chunks.Add(sb.ToString());
        return chunks;
    }
}
