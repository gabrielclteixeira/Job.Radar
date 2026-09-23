namespace JobRadar;

/// <summary>
/// Comma-list helpers for the profile editor. The form shows each list as one "a, b, c" line, but items
/// themselves may contain commas inside brackets — "C# / .NET (ASP.NET Core, Blazor)" is ONE skill. A naive
/// Split(',') cut it into "C# / .NET (ASP.NET Core" + "Blazor)", which then matched nothing when filtering.
/// </summary>
public static class TextLists
{
    /// <summary>Splits on commas (and newlines) that sit OUTSIDE any (), [] or {} — trimmed, empties dropped.</summary>
    public static List<string> Split(string? s)
    {
        var items = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return items;
        int depth = 0, start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            char c = i < s.Length ? s[i] : ',';
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            else if ((c == ',' && depth == 0) || c == '\n' || i == s.Length)
            {
                string item = s[start..i].Trim();
                if (item.Length > 0) items.Add(item);
                start = i + 1;
                if (c == '\n') depth = 0; // a stray "(" never swallows the following lines
            }
        }
        return items;
    }

    /// <summary>Re-joins items a naive comma split cut inside brackets (["C# (ASP.NET Core", "Blazor)"] →
    /// ["C# (ASP.NET Core, Blazor)"]). Repairs profiles saved before <see cref="Split"/> existed; lists that are
    /// already well-formed come back unchanged.</summary>
    public static List<string> RepairSplitItems(IEnumerable<string>? items)
    {
        var fixedItems = new List<string>();
        string? open = null;
        foreach (var raw in items ?? Enumerable.Empty<string>())
        {
            string item = raw?.Trim() ?? "";
            if (item.Length == 0) continue;
            open = open is null ? item : open + ", " + item;
            if (Balance(open) <= 0) { fixedItems.Add(open); open = null; }
        }
        if (open is not null) fixedItems.Add(open); // never balanced — keep it rather than lose text
        return fixedItems;
    }

    private static int Balance(string s)
    {
        int b = 0;
        foreach (char c in s)
            if (c is '(' or '[' or '{') b++;
            else if (c is ')' or ']' or '}') b--;
        return b;
    }
}
