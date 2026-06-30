using System.Text.Json;

namespace JobRadar;

/// <summary>
/// Per-company on-disk cache of <see cref="CompanyReport"/> (machine-local, gitignored). Keyed by the
/// normalized company name. Ratings and especially layoffs are time-sensitive, so entries older than
/// <see cref="TtlDays"/> are dropped on load and a fresh research is needed — repeats within the window
/// reuse the brief with no re-spend. Mirrors the career-research cache in <see cref="CareerPlan"/>.
/// </summary>
public static class CompanyCache
{
    public const int TtlDays = 7;
    private static readonly JsonSerializerOptions J = new() { WriteIndented = true };

    /// <summary>Case-/whitespace-insensitive key so "Celfocus" and " celfocus " hit the same entry.</summary>
    public static string Key(string company) => (company ?? "").Trim().ToLowerInvariant();

    public static Dictionary<string, CompanyReport> Load(string? path)
    {
        var map = new Dictionary<string, CompanyReport>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return map;
            var raw = JsonSerializer.Deserialize<Dictionary<string, CompanyReport>>(File.ReadAllText(path));
            if (raw is null) return map;
            foreach (var (k, v) in raw)
                if (v is not null && Fresh(v)) map[k] = v;   // drop stale entries on load
        }
        catch { /* ignore a bad cache file */ }
        return map;
    }

    public static void Save(string? path, Dictionary<string, CompanyReport> map)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.WriteAllText(path, JsonSerializer.Serialize(map, J)); }
        catch { /* best-effort */ }
    }

    /// <summary>True if the report was built within the TTL window.</summary>
    public static bool Fresh(CompanyReport r)
    {
        if (!DateTime.TryParse(r.AsOfUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            return false;
        return DateTime.UtcNow - d.ToUniversalTime() <= TimeSpan.FromDays(TtlDays);
    }
}
