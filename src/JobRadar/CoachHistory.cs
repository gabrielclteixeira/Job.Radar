using System.Text.Json;

namespace JobRadar;

/// <summary>One persisted Coach message (transcript storage shape — the VM wraps it for display).</summary>
public class CoachStoredMessage
{
    public bool IsUser { get; set; }
    public string Text { get; set; } = "";
    public List<string>? Images { get; set; }
}

/// <summary>
/// On-disk Coach conversation history (machine-local, gitignored), one thread per company — keyed by
/// <see cref="CompanyCache.Key"/>, with "" as the general "(no company)" thread. The company dropdown
/// in the Coach view doubles as the thread selector, so no separate conversation-list UI is needed.
/// </summary>
public static class CoachHistory
{
    private static readonly JsonSerializerOptions J = new() { WriteIndented = true };

    public static Dictionary<string, List<CoachStoredMessage>> Load(string? path)
    {
        var map = new Dictionary<string, List<CoachStoredMessage>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return map;
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<CoachStoredMessage>>>(File.ReadAllText(path));
            if (raw is null) return map;
            foreach (var (k, v) in raw)
                if (v is { Count: > 0 }) map[k] = v;
        }
        catch { /* ignore a bad history file */ }
        return map;
    }

    public static void Save(string? path, Dictionary<string, List<CoachStoredMessage>> map)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.WriteAllText(path, JsonSerializer.Serialize(map, J)); }
        catch { /* best-effort */ }
    }
}
